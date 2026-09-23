// SPDX-License-Identifier: MPL-2.0

using PinsGuider.Engine.Core;
using PinsGuider.Engine.Guiding;

namespace PinsGuider.Engine.Coach;

/// <summary>Samples of the drift measurement.</summary>
internal sealed record DriftMeasurementResult(IReadOnlyList<DriftSample> Samples, double ElapsedSeconds, bool StarLost, double ExposureSeconds,
    double PixelScale, double? DeclinationDeg);

/// <summary>Drift measurement hook: guiding output off, samples the mount-axis offset of the primary every frame.</summary>
internal sealed class DriftMeasurement(double targetSeconds, double lostTimeoutSec, Action<DriftMeasurement>? onProgress = null) : ICoachFrameHook
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<DriftMeasurementResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<DriftSample> samples = [];
    private DateTimeOffset? start;
    private DateTimeOffset? lostSince;
    private double elapsed;
    private double exposureSeconds;
    private double pixelScale = 1.0;
    private double? declination;
    private bool finished;

    public bool SuspendsGuiding => true;

    public Task<DriftMeasurementResult> Completion => tcs.Task;

    public double TargetSeconds => targetSeconds;

    public double ElapsedSeconds
    {
        get
        {
            lock (gate)
            {
                return elapsed;
            }
        }
    }

    public double PixelScale
    {
        get
        {
            lock (gate)
            {
                return pixelScale;
            }
        }
    }

    public IReadOnlyList<DriftSample> Samples
    {
        get
        {
            lock (gate)
            {
                return samples.ToList();
            }
        }
    }

    /// <summary>Ends the measurement now with the samples so far (skip).</summary>
    public void Stop()
    {
        lock (gate)
        {
            Finish(false);
        }
    }

    public bool OnGuidingFailed(GuideErrorCode code)
    {
        lock (gate)
        {
            if (code != GuideErrorCode.StarReacquireTimeout)
            {
                return false;
            }

            Finish(true);
            return true;
        }
    }

    public IReadOnlyList<PulseCommand> OnFrame(CoachFrame f)
    {
        lock (gate)
        {
            if (finished)
            {
                return [];
            }

            start ??= f.Time;
            elapsed = (f.Time - start.Value).TotalSeconds;
            exposureSeconds = f.ExposureMs / 1000.0;
            pixelScale = f.PixelScale;
            declination = f.DeclinationDeg ?? declination;
            if (f.StarFound && f.MountOffset.IsValid)
            {
                lostSince = null;
                samples.Add(new DriftSample(elapsed, f.MountOffset.X, f.MountOffset.Y, f.Snr));
            }
            else
            {
                lostSince ??= f.Time;
                if ((f.Time - lostSince.Value).TotalSeconds > lostTimeoutSec)
                {
                    Finish(true);
                }
            }

            if (elapsed >= targetSeconds)
            {
                Finish(false);
            }
        }

        onProgress?.Invoke(this);
        return [];
    }

    private void Finish(bool starLost)
    {
        if (finished)
        {
            return;
        }

        finished = true;
        tcs.TrySetResult(new DriftMeasurementResult(samples.ToList(), elapsed, starLost, exposureSeconds, pixelScale, declination));
    }
}

/// <summary>One guided trial frame (mount-axis offsets, px).</summary>
public readonly record struct TrialSample(double T, double RaPx, double DecPx, double Snr);

/// <summary>Statistics of a guided trial.</summary>
/// <param name="RhoRa">Lag-1 autocorrelation of the RA errors (unclamped; likewise Dec).</param>
/// <param name="EffectiveFrames">
/// Effective number of independent frames: the smaller of the per-axis N·(1−ρ)/(1+ρ), ρ clamped to [0, 0.95]
/// (see <see cref="TrialJudge.EffectiveFrames"/>).
/// </param>
public sealed record TrialStatistics(int Frames, double RmsRaArcsec, double RmsDecArcsec, double RmsTotalArcsec, double PeakArcsec, double OscillationIndex,
    double? SnrAvg, double RhoRa = 0, double RhoDec = 0, double EffectiveFrames = 0)
{
    /// <summary>
    /// RMS per axis as population σ about the mean (like the guiding statistics), total = hypot, peak = max distance from
    /// the lock position, RA oscillation index 1 − sameSideCount/(n − 1) (PHD2).
    /// </summary>
    public static TrialStatistics? Compute(IReadOnlyList<TrialSample> samples, double pixelScale)
    {
        int n = samples.Count;
        if (n < 2)
        {
            return null;
        }

        double scale = pixelScale > 0 ? pixelScale : 1.0;
        double mr = samples.Average(s => s.RaPx), md = samples.Average(s => s.DecPx);
        double rr = Math.Sqrt(samples.Sum(s => (s.RaPx - mr) * (s.RaPx - mr)) / n);
        double rd = Math.Sqrt(samples.Sum(s => (s.DecPx - md) * (s.DecPx - md)) / n);
        double peak = samples.Max(s => Math.Sqrt(s.RaPx * s.RaPx + s.DecPx * s.DecPx));
        int same = 0;
        for (int i = 1; i < n; i++)
        {
            if (samples[i].RaPx * samples[i - 1].RaPx > 0)
            {
                same++;
            }
        }

        var snr = samples.Where(s => s.Snr > 0).Select(s => s.Snr).ToList();
        double rhoRa = TrialJudge.LagOneAutocorrelation(samples.Select(s => s.RaPx).ToList());
        double rhoDec = TrialJudge.LagOneAutocorrelation(samples.Select(s => s.DecPx).ToList());
        return new TrialStatistics(n, rr * scale, rd * scale, Math.Sqrt(rr * rr + rd * rd) * scale, peak * scale, 1.0 - (double)same / (n - 1),
            snr.Count > 0 ? snr.Average() : null, rhoRa, rhoDec, Math.Min(TrialJudge.EffectiveFrames(n, rhoRa), TrialJudge.EffectiveFrames(n, rhoDec)));
    }
}

/// <summary>Noise-aware comparison of the trials (docs/COACH.md §2.4).</summary>
/// <param name="Baseline">Trial A (current settings), or A2 when A did not complete; null without either.</param>
/// <param name="Winner">
/// The significantly better alternative (B or C) when there is one, else the baseline (the current settings are as good as
/// the alternatives within the noise); null without a baseline.
/// </param>
/// <param name="Best">Lowest RMS of all completed trials.</param>
/// <param name="Significant">True when <paramref name="Winner"/> is an alternative that beat the baseline by more than 2 standard errors.</param>
/// <param name="ImprovementPercent">Improvement of the best alternative over the baseline (may be insignificant or negative).</param>
/// <param name="ConditionsChangePercent">|A2 − A| / A in percent when both completed.</param>
/// <param name="ConditionsChanged">The baseline changed by more than 25 % and by more than 2 standard errors.</param>
public sealed record TrialVerdict(CoachTrial? Baseline, CoachTrial? Winner, CoachTrial? Best, bool Significant, double? ImprovementPercent,
    double? ConditionsChangePercent, bool ConditionsChanged);

/// <summary>
/// Judges trials against the noise of their RMS values. Guide errors are autocorrelated (a frame's error persists into the
/// next), so the standard error uses an effective sample size instead of the frame count.
/// </summary>
public static class TrialJudge
{
    /// <summary>Upper clamp of the lag-1 autocorrelation.</summary>
    public const double MaxRho = 0.95;
    /// <summary>Relative change of the baseline (A vs A2) that counts as changed conditions.</summary>
    public const double ConditionsChangeThreshold = 0.25;

    /// <summary>Differences must exceed this many combined standard errors.</summary>
    public const double SignificanceSigmas = 2.0;

    /// <summary>Lag-1 autocorrelation about the mean (0 for fewer than 3 values or no variance).</summary>
    public static double LagOneAutocorrelation(IReadOnlyList<double> values)
    {
        int n = values.Count;
        if (n < 3)
        {
            return 0;
        }

        double m = values.Average();
        double num = 0, den = 0;
        for (int i = 0; i < n; i++)
        {
            double d = values[i] - m;
            den += d * d;
            if (i > 0)
            {
                num += d * (values[i - 1] - m);
            }
        }

        return den > 0 ? num / den : 0;
    }

    /// <summary>Effective sample size N·(1−ρ)/(1+ρ) with ρ clamped to [0, 0.95].</summary>
    public static double EffectiveFrames(int frames, double rho)
    {
        double r = double.IsFinite(rho) ? Math.Clamp(rho, 0, MaxRho) : 0;
        return frames * (1 - r) / (1 + r);
    }

    /// <summary>
    /// Standard error of a trial's RMS: RMS/√(2·N_eff); <paramref name="effectiveFrames"/> null = the frame count (independent frames).
    /// </summary>
    public static double StandardError(CoachTrial t, double? effectiveFrames = null) =>
        (t.RmsTotalArcsec ?? 0) / Math.Sqrt(2.0 * Math.Max(1, effectiveFrames ?? t.Frames));

    /// <summary>Combined standard error of the difference of two trials' RMS values.</summary>
    public static double CombinedStandardError(CoachTrial a, CoachTrial b, IReadOnlyDictionary<string, double>? effectiveFrames = null) =>
        Math.Sqrt(Sq(StandardError(a, Neff(a, effectiveFrames))) + Sq(StandardError(b, Neff(b, effectiveFrames))));

    /// <param name="trials">Trials of the session.</param>
    /// <param name="effectiveFrames">Effective sample size per trial id (<see cref="TrialStatistics.EffectiveFrames"/>); trials without one use their frame count.</param>
    public static TrialVerdict Judge(IReadOnlyList<CoachTrial> trials, IReadOnlyDictionary<string, double>? effectiveFrames = null)
    {
        var done = trials.Where(t => t.State == CoachTrialStates.Done && t.RmsTotalArcsec is > 0).ToList();
        var a = done.FirstOrDefault(t => t.Id == CoachTrialIds.A);
        var a2 = done.FirstOrDefault(t => t.Id == CoachTrialIds.A2);
        var baseline = a ?? a2;
        var best = done.OrderBy(t => t.RmsTotalArcsec).FirstOrDefault();

        double? changePercent = null;
        bool changed = false;
        if (a is not null && a2 is not null)
        {
            double diff = Math.Abs(a2.RmsTotalArcsec!.Value - a.RmsTotalArcsec!.Value);
            changePercent = diff / a.RmsTotalArcsec.Value * 100;
            changed = diff > ConditionsChangeThreshold * a.RmsTotalArcsec.Value && diff > SignificanceSigmas * CombinedStandardError(a, a2, effectiveFrames);
        }

        if (baseline is null)
        {
            return new TrialVerdict(null, null, best, false, null, changePercent, changed);
        }

        var alternative = done.Where(t => t.Kind is not (CoachTrialKinds.Current or CoachTrialKinds.CurrentRepeat)).OrderBy(t => t.RmsTotalArcsec).FirstOrDefault();
        if (alternative is null)
        {
            return new TrialVerdict(baseline, baseline, best, false, null, changePercent, changed);
        }

        double improvement = baseline.RmsTotalArcsec!.Value - alternative.RmsTotalArcsec!.Value;
        bool significant = improvement > SignificanceSigmas * CombinedStandardError(baseline, alternative, effectiveFrames);
        return new TrialVerdict(baseline, significant ? alternative : baseline, best, significant, improvement / baseline.RmsTotalArcsec.Value * 100,
            changePercent, changed);
    }

    private static double? Neff(CoachTrial t, IReadOnlyDictionary<string, double>? effectiveFrames) =>
        effectiveFrames is not null && effectiveFrames.TryGetValue(t.Id, out var n) ? n : null;

    private static double Sq(double v) => v * v;
}

/// <summary>Outcome of a trial observation.</summary>
internal sealed record TrialObservation(IReadOnlyList<TrialSample> Samples, double ElapsedSeconds, GuideErrorCode? Failure, bool StarLost, double PixelScale);

/// <summary>
/// Observes normal guiding for a trial (statistics and safety stay active). Installed before settling so guiding failures
/// then also end only the trial; samples are collected after <see cref="BeginMeasurement"/>.
/// </summary>
internal sealed class TrialObserver(double seconds, double lostTimeoutSec, Action<TrialObserver>? onProgress = null) : ICoachFrameHook
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<TrialObservation> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<TrialSample> samples = [];
    private DateTimeOffset? start;
    private DateTimeOffset? lostSince;
    private double elapsed;
    private double pixelScale = 1.0;
    private bool finished;
    private bool measuring;

    public bool SuspendsGuiding => false;

    public Task<TrialObservation> Completion => tcs.Task;

    /// <summary>Settling is over: collect samples from the next frame on.</summary>
    public void BeginMeasurement()
    {
        lock (gate)
        {
            measuring = true;
        }
    }

    public double ElapsedSeconds
    {
        get
        {
            lock (gate)
            {
                return elapsed;
            }
        }
    }

    public TrialStatistics? CurrentStatistics()
    {
        lock (gate)
        {
            return TrialStatistics.Compute(samples, pixelScale);
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            Finish(null, false);
        }
    }

    public bool OnGuidingFailed(GuideErrorCode code)
    {
        lock (gate)
        {
            // a runaway or unresponsive mount during a trial fails the trial only
            if (code is GuideErrorCode.RunawayDetected or GuideErrorCode.MountNotResponding or GuideErrorCode.StarReacquireTimeout)
            {
                Finish(code, code == GuideErrorCode.StarReacquireTimeout);
                return true;
            }

            return false;
        }
    }

    public IReadOnlyList<PulseCommand> OnFrame(CoachFrame f)
    {
        lock (gate)
        {
            if (finished || !measuring)
            {
                return [];
            }

            start ??= f.Time;
            elapsed = (f.Time - start.Value).TotalSeconds;
            pixelScale = f.PixelScale;
            if (f.StarFound && f.MountOffset.IsValid)
            {
                lostSince = null;
                if (!f.IsSettling && !f.IsRecenterMove)
                {
                    samples.Add(new TrialSample(elapsed, f.MountOffset.X, f.MountOffset.Y, f.Snr));
                }
            }
            else
            {
                lostSince ??= f.Time;
                if ((f.Time - lostSince.Value).TotalSeconds > lostTimeoutSec)
                {
                    Finish(null, true);
                }
            }

            if (elapsed >= seconds)
            {
                Finish(null, false);
            }
        }

        onProgress?.Invoke(this);
        return [];
    }

    private void Finish(GuideErrorCode? failure, bool starLost)
    {
        if (finished)
        {
            return;
        }

        finished = true;
        tcs.TrySetResult(new TrialObservation(samples.ToList(), elapsed, failure, starLost, pixelScale));
    }
}
