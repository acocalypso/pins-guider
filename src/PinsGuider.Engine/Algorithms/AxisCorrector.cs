// SPDX-License-Identifier: MPL-2.0 AND BSD-3-Clause
// Copyright (c) 2012 Bret McKee
// Copyright (c) 2006-2010 Craig Stark
// Ported from PHD2 src/mount.cpp (Mount::MoveOffset, Notify*) and src/scope.cpp (Scope::MoveAxis) (a6c02722)

using PinsGuider.Engine.Core;

namespace PinsGuider.Engine.Algorithms;

/// <summary>
/// Result of one correction computation, carrying PHD2's <c>GuideStepInfo</c> move fields.
/// </summary>
public sealed record GuideCorrection
{
    public MoveOptions Options { get; init; }

    /// <summary>Mount-axis RA offset fed in (px); for deduced moves the deduced amount.</summary>
    public double RADistanceRaw { get; init; }

    /// <summary>Mount-axis Dec offset fed in (px); for deduced moves the deduced amount.</summary>
    public double DECDistanceRaw { get; init; }

    /// <summary>RA distance after the guide algorithm (px).</summary>
    public double RADistanceGuide { get; init; }

    /// <summary>Dec distance after the guide algorithm (px).</summary>
    public double DECDistanceGuide { get; init; }

    /// <summary>RA pulse actually issued (ms, after clamp). 0 = no pulse.</summary>
    public int RADuration { get; init; }

    /// <summary>RA direction: West when <see cref="RADistanceGuide"/> &gt; 0, else East.</summary>
    public GuideDirection RADirection { get; init; }

    /// <summary>Dec pulse actually issued (ms, incl. backlash comp, after mode filter and clamp).</summary>
    public int DECDuration { get; init; }

    /// <summary>Dec direction: South when <see cref="DECDistanceGuide"/> &gt; 0, else North.</summary>
    public GuideDirection DECDirection { get; init; }

    public bool RALimited { get; init; }

    public bool DecLimited { get; init; }

    /// <summary>Backlash compensation added to the requested Dec pulse (ms), before mode filter/clamp.</summary>
    public int BacklashCompMs { get; init; }

    /// <summary>True when the Dec guide mode suppressed a non-zero Dec request.</summary>
    public bool DecSuppressedByMode { get; init; }

    /// <summary>
    /// Pulses to issue in order (RA first, then Dec); zero-duration axes omitted. Durations are the
    /// final clamped values.
    /// </summary>
    public IReadOnlyList<PulseCommand> Pulses { get; init; } = [];

    /// <summary>Limit-reached alerts raised by this step (0..2).</summary>
    public IReadOnlyList<PulseLimitAlert> Alerts { get; init; } = [];

    /// <summary>
    /// Estimated RA correction applied (px) with the sign of <see cref="RADistanceGuide"/>, i.e. the
    /// amount by which a positive offset is reduced: <c>±RADuration · xRate</c>.
    /// </summary>
    public double RaCorrectionPx { get; init; }

    /// <summary>
    /// Estimated Dec correction applied (px), <c>±max(0, DECDuration − BacklashCompMs) · yRate</c>
    /// (the backlash pulse is assumed to only take up slack).
    /// </summary>
    public double DecCorrectionPx { get; init; }
}

/// <summary>
/// Correction pipeline for a mount (non-AO) guider: guide algorithms → Dec backlash compensation →
/// Dec guide mode → max-duration clamp and limit alerts, following PHD2's
/// <c>Mount::MoveOffset</c> + <c>Scope::MoveAxis</c> order. Computes pulses only; issuing them is up to
/// the caller.
/// </summary>
/// <remarks>
/// <para>Direction convention (identical to PHD2): inputs are the star's offset from the lock position
/// transformed to mount axes (PHD2 <c>mountOfs</c>, px). The mount X axis is the direction a star moves
/// under an <em>East</em> pulse (PHD2 calibrates <c>xAngle = start.Angle(end)</c> after West pulses,
/// i.e. from the end point back to the start), and the Y axis is the direction a star moves under a
/// <em>South</em> pulse. Hence a positive RA distance is corrected with a <b>West</b> pulse (PHD2
/// <c>LEFT</c>) and a negative one with East (<c>RIGHT</c>); a positive Dec distance is corrected with a
/// <b>South</b> pulse (<c>DOWN</c>) and a negative one with North (<c>UP</c>). A zero distance reports
/// East/North with duration 0.</para>
/// <para>Durations are <c>ROUND(|distance / rate|)</c> with <c>ROUND(x) = floor(x + 0.5)</c> and rates in
/// px/ms (RA rate already declination-compensated by the caller, as PHD2's <c>m_xRate</c>).</para>
/// </remarks>
public sealed class AxisCorrector
{
    public AxisCorrector(IGuideAlgorithm raAlgorithm, IGuideAlgorithm decAlgorithm, PulseLimiter? limiter = null, BacklashCompensation? backlash = null)
    {
        RaAlgorithm = raAlgorithm;
        DecAlgorithm = decAlgorithm;
        Limiter = limiter ?? new PulseLimiter();
        Backlash = backlash ?? new BacklashCompensation(limiter: Limiter);
    }

    /// <summary>Creates a corrector with PHD2 defaults (Hysteresis RA, ResistSwitch Dec) and the given min-move.</summary>
    public static AxisCorrector CreateDefault(double minMove)
    {
        return new AxisCorrector(
            GuideAlgorithmFactory.Create(GuideAlgorithmFactory.DefaultRaAlgorithm, GuideAxis.Ra, minMove),
            GuideAlgorithmFactory.Create(GuideAlgorithmFactory.DefaultDecAlgorithm, GuideAxis.Dec, minMove));
    }

    public IGuideAlgorithm RaAlgorithm { get; set; }

    public IGuideAlgorithm DecAlgorithm { get; set; }

    public PulseLimiter Limiter { get; }

    public BacklashCompensation Backlash { get; }

    /// <summary>Dec guide mode. PHD2's mount default is Auto.</summary>
    public DecGuideMode DecGuideMode { get; set; } = DecGuideMode.Auto;

    /// <summary>
    /// When false, non-manual moves produce no pulses (algorithms still run and see the offsets), like
    /// PHD2's "guiding output disabled".
    /// </summary>
    public bool GuidingEnabled { get; private set; } = true;

    /// <summary>Enables/disables guide output (PHD2 <c>Mount::SetGuidingEnabled</c>).</summary>
    public void SetGuidingEnabled(bool enabled, DateTimeOffset now)
    {
        if (enabled == GuidingEnabled)
            return;
        GuidingEnabled = enabled;
        if (enabled)
        {
            Notify(a =>
            {
                if (a is GuideAlgorithmBase b)
                    b.GuidingEnabled();
                else
                    a.Reset();
            });

            // avoid sending false positive alerts after guiding is re-enabled
            Limiter.DeferAlertCheck(now);
        }
        else
        {
            Notify(a => (a as GuideAlgorithmBase)?.GuidingDisabled());
        }
    }

    /// <summary>Normal guide step: offsets go through the guide algorithms.</summary>
    public GuideCorrection GuideStep(double raDistancePx, double decDistancePx, double xRate, double yRate, DateTimeOffset now)
        => Move(raDistancePx, decDistancePx, xRate, yRate, MoveOptions.GuideStep, now)!;

    /// <summary>
    /// Dead-reckoning move from <see cref="IGuideAlgorithm.DeduceResult"/> (star lost / paused). Returns
    /// null when both algorithms deduce 0, as PHD2 then issues no move and logs no step.
    /// </summary>
    public GuideCorrection? DeducedStep(double xRate, double yRate, DateTimeOffset now)
        => Move(0, 0, xRate, yRate, MoveOptions.DeducedMove, now);

    /// <summary>
    /// General move (PHD2 <c>MoveOffset</c> with explicit options), e.g. <see cref="MoveOptions.RecoveryMove"/>
    /// for fast recenter. Returns null only for deduced moves with nothing to do.
    /// </summary>
    public GuideCorrection? Move(double raDistancePx, double decDistancePx, double xRate, double yRate, MoveOptions options, DateTimeOffset now)
    {
        double xDistance;
        double yDistance;
        double rawX;
        double rawY;

        if ((options & MoveOptions.AlgoDeduce) != 0)
        {
            xDistance = RaAlgorithm.DeduceResult();
            yDistance = DecAlgorithm.DeduceResult();
            if (xDistance == 0.0 && yDistance == 0.0)
                return null;
            rawX = xDistance;
            rawY = yDistance;
        }
        else
        {
            rawX = xDistance = raDistancePx;
            rawY = yDistance = decDistancePx;

            // Let BLC track the raw offsets in Dec
            Backlash.TrackResults(options, yDistance, DecAlgorithm.MinMove, yRate, now);

            if ((options & MoveOptions.AlgoResult) != 0)
            {
                // Feed the raw distances to the guide algorithms
                xDistance = RaAlgorithm.Result(xDistance);
                yDistance = DecAlgorithm.Result(yDistance);
            }
        }

        // Figure out the guide directions based on the (possibly) updated distances
        GuideDirection xDirection = xDistance > 0.0 ? GuideDirection.West : GuideDirection.East;
        GuideDirection yDirection = yDistance > 0.0 ? GuideDirection.South : GuideDirection.North;

        var alerts = new List<PulseLimitAlert>(2);

        int requestedXAmount = DurationFor(xDistance, xRate);
        var x = MoveAxis(xDirection, requestedXAmount, options, now, alerts, out _);

        int requestedYAmount = DurationFor(yDistance, yRate);
        int blc = Backlash.Apply(options, yDistance, ref requestedYAmount, now);
        var y = MoveAxis(yDirection, requestedYAmount, options, now, alerts, out bool decSuppressed);

        var pulses = new List<PulseCommand>(2);
        if (x.DurationMs > 0)
            pulses.Add(new PulseCommand(xDirection, x.DurationMs));
        if (y.DurationMs > 0)
            pulses.Add(new PulseCommand(yDirection, y.DurationMs));

        double raSign = xDirection == GuideDirection.West ? 1.0 : -1.0;
        double decSign = yDirection == GuideDirection.South ? 1.0 : -1.0;
        double raCorr = ValidRate(xRate) ? raSign * x.DurationMs * xRate : 0.0;
        double decCorr = ValidRate(yRate) ? decSign * Math.Max(0, y.DurationMs - blc) * yRate : 0.0;

        return new GuideCorrection
        {
            Options = options,
            RADistanceRaw = rawX,
            DECDistanceRaw = rawY,
            RADistanceGuide = xDistance,
            DECDistanceGuide = yDistance,
            RADuration = x.DurationMs,
            RADirection = xDirection,
            DECDuration = y.DurationMs,
            DECDirection = yDirection,
            RALimited = x.Limited,
            DecLimited = y.Limited,
            BacklashCompMs = blc,
            DecSuppressedByMode = decSuppressed,
            Pulses = pulses,
            Alerts = alerts,
            RaCorrectionPx = raCorr == 0.0 ? 0.0 : raCorr,
            DecCorrectionPx = decCorr == 0.0 ? 0.0 : decCorr,
        };
    }

    // Port of Scope::MoveAxis duration logic (without issuing the pulse).
    private LimitedPulse MoveAxis(GuideDirection direction, int duration, MoveOptions options, DateTimeOffset now, List<PulseLimitAlert> alerts, out bool suppressedByMode)
    {
        suppressedByMode = false;
        if (!GuidingEnabled && (options & MoveOptions.Manual) == 0)
        {
            // PHD2 throws "Guiding disabled" before any limit handling; the move reports 0 / not limited.
            return new LimitedPulse(direction, 0, false, null);
        }

        bool algoOrDeduce = (options & (MoveOptions.AlgoResult | MoveOptions.AlgoDeduce)) != 0;
        if (!algoOrDeduce)
            return new LimitedPulse(direction, duration, false, null);

        if (direction.Axis() == GuideAxis.Dec)
        {
            int filtered = DecGuideModeFilter.Apply(DecGuideMode, direction, duration);
            suppressedByMode = filtered == 0 && duration > 0;
            duration = filtered;
        }

        var limited = Limiter.Limit(direction, duration, now);
        if (limited.Alert is not null)
            alerts.Add(limited.Alert);
        return limited;
    }

    private static bool ValidRate(double rate) => rate > 0 && double.IsFinite(rate);

    /// <summary><c>ROUND(|distance / rate|)</c>; 0 for invalid rates.</summary>
    public static int DurationFor(double distancePx, double ratePxPerMs)
    {
        // Deviation from PHD2: an invalid (0/NaN/negative) rate yields 0 instead of undefined behaviour,
        // and durations saturate at int.MaxValue.
        if (!ValidRate(ratePxPerMs))
            return 0;
        double v = Math.Floor(Math.Abs(distancePx / ratePxPerMs) + 0.5);
        return v >= int.MaxValue ? int.MaxValue : (int)v;
    }

    /// <summary>PHD2 <c>NotifyGuidingStarted</c>.</summary>
    public void GuidingStarted() => Notify(a => a.GuidingStarted());

    /// <summary>PHD2 <c>NotifyGuidingStopped</c>: algorithms reset and backlash state cleared.</summary>
    public void GuidingStopped()
    {
        Notify(a => a.GuidingStopped());
        Backlash.ResetState();
    }

    public void GuidingPaused() => Notify(a => a.GuidingPaused());

    public void GuidingResumed() => Notify(a => a.GuidingResumed());

    /// <summary>
    /// PHD2 <c>NotifyGuidingDithered</c> with the dither in mount coordinates (px). Also starts the
    /// limit-alert grace period.
    /// </summary>
    public void GuidingDithered(double raPx, double decPx, DateTimeOffset now)
    {
        RaAlgorithm.GuidingDithered(raPx);
        DecAlgorithm.GuidingDithered(decPx);

        // Deviation from PHD2: PHD2 only defers limit alerts when guiding output is re-enabled; the
        // engine also defers them after a dither (fast-recenter/settle can legitimately saturate).
        Limiter.DeferAlertCheck(now);
    }

    public void GuidingDitherSettleDone(bool success) => Notify(a => a.GuidingDitherSettleDone(success));

    /// <summary>PHD2 <c>NotifyDirectMove</c> (mount px).</summary>
    public void DirectMoveApplied(double raPx, double decPx)
    {
        RaAlgorithm.DirectMoveApplied(raPx);
        DecAlgorithm.DirectMoveApplied(decPx);
    }

    private void Notify(Action<IGuideAlgorithm> action)
    {
        action(RaAlgorithm);
        action(DecAlgorithm);
    }
}
