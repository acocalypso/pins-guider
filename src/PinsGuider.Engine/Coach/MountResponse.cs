// SPDX-License-Identifier: MPL-2.0

using PinsGuider.Engine.Core;
using PinsGuider.Engine.Guiding;

namespace PinsGuider.Engine.Coach;

/// <summary>Evaluation of the pulse-response test (docs/COACH.md §2.3).</summary>
public static class MountResponseAnalyzer
{
    /// <summary>A pulse is effective when it moves at least this fraction of the expected distance.</summary>
    public const double EffectiveRatio = 0.5;

    /// <summary>Pulses at least this long are used for rate and asymmetry (short pulses are dominated by noise and stiction).</summary>
    public const int RateMinPulseMs = 500;

    public static bool IsRa(string direction) => direction is "East" or "West";

    /// <summary>
    /// Smallest pulse duration of the axis whose mean move reaches 50 % of the expected move, provided all longer tested
    /// durations do too. Null when even the longest pulses fall short (or nothing was measured).
    /// </summary>
    public static int? MinEffectivePulse(IEnumerable<CoachPulse> pulses, bool ra)
    {
        var bySize = pulses.Where(p => IsRa(p.Direction) == ra).GroupBy(p => p.DurationMs).OrderBy(g => g.Key)
            .Select(g => (Ms: g.Key, Ratio: g.Average(p => p.Ratio))).ToList();
        int? result = null;
        for (int i = bySize.Count - 1; i >= 0; i--)
        {
            if (bySize[i].Ratio < EffectiveRatio)
            {
                break;
            }

            result = bySize[i].Ms;
        }

        return result;
    }

    /// <summary>Ratio of the mean moves (moved/expected) of two directions for pulses ≥ <paramref name="minMs"/>; null when a direction is missing.</summary>
    public static double? Asymmetry(IEnumerable<CoachPulse> pulses, string numerator, string denominator, int minMs)
    {
        var list = pulses.Where(p => p.DurationMs >= minMs).ToList();
        var a = list.Where(p => p.Direction == numerator).Select(p => p.Ratio).ToList();
        var b = list.Where(p => p.Direction == denominator).Select(p => p.Ratio).ToList();
        if (a.Count == 0 || b.Count == 0 || b.Average() <= 0.05)
        {
            return null;
        }

        return a.Average() / b.Average();
    }

    /// <summary>Measured/calibrated rate of an axis: mean ratio of both directions for pulses ≥ <paramref name="minMs"/>.</summary>
    public static double? RateRatio(IEnumerable<CoachPulse> pulses, bool ra, int minMs)
    {
        var list = pulses.Where(p => IsRa(p.Direction) == ra && p.DurationMs >= minMs).ToList();
        return list.Count == 0 ? null : list.Average(p => p.Ratio);
    }

    /// <summary>Pulse length used for rate/asymmetry: the effective minimum, at least 500 ms (or the longest tested).</summary>
    public static int RateBasis(IEnumerable<CoachPulse> pulses, bool ra, int? minEffective)
    {
        var sizes = pulses.Where(p => IsRa(p.Direction) == ra).Select(p => p.DurationMs).Distinct().ToList();
        if (sizes.Count == 0)
        {
            return RateMinPulseMs;
        }

        int basis = Math.Max(RateMinPulseMs, minEffective ?? RateMinPulseMs);
        return sizes.Any(s => s >= basis) ? basis : sizes.Max();
    }
}

/// <summary>Result of the mount response measurement.</summary>
internal sealed record MountResponseResult(CoachResponse Response, bool StarLost, bool Truncated, double? BacklashPxPerMs);

/// <summary>
/// Frame-driven mount response measurement (guiding output off): RA pulse response (W/E pairs), Dec backlash with an own
/// unbiased method, Dec pulse response per direction with the gear engaged, then a recenter towards the lock position.
/// </summary>
/// <remarks>
/// <para>Backlash: pulse North with fixed pulses until two consecutive moves reach 70 % of the calibrated move (gear
/// engaged North; their median gives the actual North rate r). Then pulse South with the same pulse P and record the
/// drift-corrected Dec position y'ₖ after each pulse. Once two consecutive moves reach 80 % of the full move, the gear is
/// engaged South and y'₀ − y'ₖ = r·(k·P − D) holds for those points, so the dead band is D = k·P − (y'₀ − y'ₖ)/r
/// (averaged over the two points). Unlike PHD2's method (which compares against 90 % of the median North move and so
/// underestimates by 10 % of the travel) this is unbiased; its noise is about √2·σ/r per point.</para>
/// <para>Each pulse is measured between the frame before and the frame after it, corrected for the linear drift measured
/// in the Drift step. Pulses are capped at the max pulse durations and skipped when the star would get too close to the
/// frame edge.</para>
/// </remarks>
internal sealed class MountResponseProcedure : ICoachFrameHook
{
    private const int MaxClearingPulses = 15;
    private const int MaxRecenterSteps = 10;
    private const double ClearAcceptRatio = 0.7;
    private const double FullRateRatio = 0.8;

    private readonly object gate = new();
    private readonly TaskCompletionSource<MountResponseResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly double raDriftPxPerSec;
    private readonly double decDriftPxPerSec;
    private readonly int[] sizes;
    private readonly int repetitions;
    private readonly bool measureBacklash;
    private readonly double lostTimeoutSec;
    private readonly Action<MountResponseProcedure>? onProgress;
    private readonly Queue<Action> plan = new();
    private readonly List<CoachPulse> pulses = [];
    private readonly List<CoachPoint> backlashPoints = [];
    private readonly List<double> clearMoves = [];
    private readonly List<(double Y, double T)> southPositions = [];

    private Pending? pending;
    private DateTimeOffset? lostSince;
    private bool skipRequested;
    private bool finished;
    private bool truncated;
    private bool starLost;
    private int issued;
    private int estimatedSteps;
    private string phase = "pulses";
    private CoachFrame? frame;
    private IReadOnlyList<PulseCommand> next = [];

    // backlash state
    private int backlashPulseMs;
    private double backlashCumulativeMs;
    private double backlashStartY;
    private bool backlashStartSet;
    private double? northRate;
    private double? backlashMs;
    private string backlashState = CoachBacklashStates.Skipped;
    private GuideDirection? lastDecDirection;
    private int clearCount;
    private int consecutiveGood;
    private int recenterSteps;
    private DateTimeOffset? southStartTime;

    public MountResponseProcedure(double raDriftPxPerSec, double decDriftPxPerSec, IReadOnlyList<int>? pulseSizes = null, int repetitions = 2,
        bool measureBacklash = true, double lostTimeoutSec = 30, Action<MountResponseProcedure>? onProgress = null)
    {
        this.raDriftPxPerSec = double.IsFinite(raDriftPxPerSec) ? raDriftPxPerSec : 0;
        this.decDriftPxPerSec = double.IsFinite(decDriftPxPerSec) ? decDriftPxPerSec : 0;
        sizes = (pulseSizes ?? [100, 250, 500, 1000]).Where(s => s > 0).Distinct().Order().ToArray();
        this.repetitions = Math.Max(1, repetitions);
        this.measureBacklash = measureBacklash;
        this.lostTimeoutSec = lostTimeoutSec;
        this.onProgress = onProgress;
        backlashState = measureBacklash ? CoachBacklashStates.Unreliable : CoachBacklashStates.Skipped;
    }

    public bool SuspendsGuiding => true;

    public Task<MountResponseResult> Completion => tcs.Task;

    /// <summary>Current sub-phase: pulses (RA), backlash, pulses (Dec), recenter.</summary>
    public string Phase
    {
        get
        {
            lock (gate)
            {
                return phase;
            }
        }
    }

    public double Progress
    {
        get
        {
            lock (gate)
            {
                return finished ? 1.0 : Math.Min(0.99, issued / (double)Math.Max(1, estimatedSteps));
            }
        }
    }

    /// <summary>Partial response data for live status.</summary>
    public CoachResponse Snapshot()
    {
        lock (gate)
        {
            return BuildResponse();
        }
    }

    /// <summary>Stops after the current frame; results so far are kept.</summary>
    public void Skip()
    {
        lock (gate)
        {
            skipRequested = true;
        }
    }

    public bool OnGuidingFailed(GuideErrorCode code)
    {
        lock (gate)
        {
            if (code == GuideErrorCode.StarReacquireTimeout)
            {
                starLost = true;
                FinishLocked();
                return true;
            }

            return false;
        }
    }

    public IReadOnlyList<PulseCommand> OnFrame(CoachFrame f)
    {
        IReadOnlyList<PulseCommand> result;
        lock (gate)
        {
            result = OnFrameLocked(f);
        }

        onProgress?.Invoke(this);
        return result;
    }

    private IReadOnlyList<PulseCommand> OnFrameLocked(CoachFrame f)
    {
        if (finished)
        {
            return [];
        }

        if (skipRequested)
        {
            FinishLocked();
            return [];
        }

        if (!f.StarFound)
        {
            lostSince ??= f.Time;
            pending = null;
            if ((f.Time - lostSince.Value).TotalSeconds > lostTimeoutSec)
            {
                starLost = true;
                FinishLocked();
            }

            return [];
        }

        lostSince = null;
        frame = f;
        if (estimatedSteps == 0)
        {
            BuildPlan(f);
        }

        if (pending is { } p)
        {
            pending = null;
            p.Complete(f);
        }

        next = [];
        while (next.Count == 0 && !finished)
        {
            if (plan.Count == 0)
            {
                FinishLocked();
                break;
            }

            plan.Dequeue()();
        }

        return next;
    }

    private void BuildPlan(CoachFrame f)
    {
        var raSizes = sizes.Select(s => Math.Min(s, f.MaxRaDurationMs)).Distinct().ToArray();
        var decSizes = sizes.Select(s => Math.Min(s, f.MaxDecDurationMs)).Distinct().ToArray();
        backlashPulseMs = BacklashPulse(f);

        foreach (int s in raSizes)
        {
            for (int r = 0; r < repetitions; r++)
            {
                plan.Enqueue(() => ResponsePulse(GuideDirection.West, s));
                plan.Enqueue(() => ResponsePulse(GuideDirection.East, s));
            }
        }

        plan.Enqueue(() => phase = "backlash");
        if (measureBacklash)
        {
            plan.Enqueue(ClearNorthStep);
        }
        else
        {
            plan.Enqueue(() => Engage(GuideDirection.South));
        }

        plan.Enqueue(() => phase = "pulses");
        foreach (int s in decSizes)
        {
            for (int r = 0; r < repetitions; r++)
            {
                plan.Enqueue(() => ResponsePulse(GuideDirection.South, s));
            }
        }

        plan.Enqueue(() => Engage(GuideDirection.North));
        foreach (int s in decSizes)
        {
            for (int r = 0; r < repetitions; r++)
            {
                plan.Enqueue(() => ResponsePulse(GuideDirection.North, s));
            }
        }

        plan.Enqueue(() => phase = "recenter");
        plan.Enqueue(RecenterStep);
        estimatedSteps = raSizes.Length * repetitions * 2 + decSizes.Length * repetitions * 2 + (measureBacklash ? 8 : 2) + 3;
    }

    private static int BacklashPulse(CoachFrame f)
    {
        double rate = f.Transform.YRate;
        if (!(rate > 0))
        {
            return 500;
        }

        // about 4 px per pulse (PHD2's backlash tool uses 4 px moves) but at most 60 % of the search region
        double ms = Math.Min(4.0 / rate, 0.6 * f.SearchRegion / rate);
        return (int)Math.Clamp(Math.Round(ms / 50) * 50, 200, f.MaxDecDurationMs);
    }

    private bool HasRoom(GuideDirection dir, int ms)
    {
        var f = frame!;
        double rate = dir.Axis() == GuideAxis.Ra ? f.Transform.XRate : f.Transform.YRate;
        double move = ms * Math.Max(rate, 0);
        double margin = f.SearchRegion + move + 5;
        var c = f.StarPosition;
        bool ok = c.X >= margin && c.Y >= margin && c.X <= f.FrameWidth - 1 - margin && c.Y <= f.FrameHeight - 1 - margin;
        if (!ok)
        {
            truncated = true;
        }

        return ok;
    }

    private void Issue(GuideDirection dir, int ms, Action<CoachFrame, double, double>? complete)
    {
        var f = frame!;
        var pos = f.MountPosition;
        next = [new PulseCommand(dir, ms)];
        issued++;
        if (dir.Axis() == GuideAxis.Dec)
        {
            lastDecDirection = dir;
        }

        if (complete is null)
        {
            return;
        }

        pending = new Pending(after =>
        {
            // move along the pulse direction, drift-corrected, px
            bool ra = dir.Axis() == GuideAxis.Ra;
            double delta = ra ? after.MountPosition.X - pos.X : after.MountPosition.Y - pos.Y;
            double dt = (after.Time - f.Time).TotalSeconds;
            double drift = (ra ? raDriftPxPerSec : decDriftPxPerSec) * dt;
            double sign = dir is GuideDirection.East or GuideDirection.North ? 1 : -1;
            complete(after, sign * (delta - drift), dt);
        });
    }

    private void ResponsePulse(GuideDirection dir, int ms)
    {
        if (!HasRoom(dir, ms))
        {
            return;
        }

        var f = frame!;
        double rate = dir.Axis() == GuideAxis.Ra ? f.Transform.XRate : f.Transform.YRate;
        double scale = f.PixelScale > 0 ? f.PixelScale : 1.0;
        Issue(dir, ms, (_, moved, _) =>
        {
            double expected = ms * rate;
            pulses.Add(new CoachPulse
            {
                Direction = dir.ToString(),
                DurationMs = ms,
                ExpectedArcsec = Math.Round(expected * scale, 3),
                MovedArcsec = Math.Round(moved * scale, 3),
                Ratio = expected > 0 ? Math.Round(moved / expected, 3) : 0,
            });
        });
    }

    private void AddBacklashPoint(CoachFrame f)
    {
        if (!backlashStartSet)
        {
            backlashStartSet = true;
            backlashStartY = f.MountPosition.Y;
        }

        double scale = f.PixelScale > 0 ? f.PixelScale : 1.0;
        backlashPoints.Add(new CoachPoint(Math.Round(backlashCumulativeMs, 1), Math.Round((f.MountPosition.Y - backlashStartY) * scale, 3)));
    }

    private void ClearNorthStep()
    {
        var f = frame!;
        double rate = f.Transform.YRate;
        if (!(rate > 0))
        {
            backlashState = CoachBacklashStates.Unreliable;
            plan.Enqueue(() => Engage(GuideDirection.South));
            return;
        }

        // too much Dec drift per frame cycle: the dead band cannot be separated from the drift
        double cycleSec = f.ExposureMs / 1000.0 + backlashPulseMs / 1000.0 + 0.5;
        if (Math.Abs(decDriftPxPerSec) * cycleSec > 0.35 * backlashPulseMs * rate)
        {
            backlashState = CoachBacklashStates.Unreliable;
            PrependEngage(GuideDirection.South);
            return;
        }

        if (clearCount == 0)
        {
            AddBacklashPoint(f);
        }

        if (clearCount >= MaxClearingPulses || !HasRoom(GuideDirection.North, backlashPulseMs))
        {
            // the mount never moved consistently North (or no room): no backlash result
            backlashState = CoachBacklashStates.Unreliable;
            PrependEngage(GuideDirection.South);
            return;
        }

        clearCount++;
        backlashCumulativeMs += backlashPulseMs;
        Issue(GuideDirection.North, backlashPulseMs, (after, moved, _) =>
        {
            AddBacklashPoint(after);
            if (moved >= ClearAcceptRatio * backlashPulseMs * rate)
            {
                clearMoves.Add(moved);
                consecutiveGood++;
            }
            else
            {
                consecutiveGood = 0;
                clearMoves.Clear();
            }

            if (consecutiveGood >= 2)
            {
                // engaged North: measured North rate from the consistent moves
                northRate = Median(clearMoves) / backlashPulseMs;
                consecutiveGood = 0;
                southPositions.Clear();
                PrependFirst(SouthStep);
            }
            else
            {
                PrependFirst(ClearNorthStep);
            }
        });
    }

    private void SouthStep()
    {
        var f = frame!;
        double r = northRate ?? f.Transform.YRate;
        if (southPositions.Count == 0)
        {
            southPositions.Add((f.MountPosition.Y, 0));
        }

        int k = southPositions.Count; // pulses issued so far + 1
        if (k > MaxClearingPulses || !HasRoom(GuideDirection.South, backlashPulseMs))
        {
            backlashState = CoachBacklashStates.Unreliable;
            PrependEngage(GuideDirection.South);
            return;
        }

        backlashCumulativeMs += backlashPulseMs;
        var origin = southStartTime ??= f.Time;
        Issue(GuideDirection.South, backlashPulseMs, (after, _, _) =>
        {
            AddBacklashPoint(after);
            double t = (after.Time - origin).TotalSeconds;
            double y = after.MountPosition.Y - decDriftPxPerSec * t;
            double prev = southPositions[^1].Y;
            southPositions.Add((y, t));
            double move = prev - y;
            consecutiveGood = move >= FullRateRatio * backlashPulseMs * r ? consecutiveGood + 1 : 0;
            if (consecutiveGood >= 2)
            {
                int n = southPositions.Count - 1; // pulses issued
                double y0 = southPositions[0].Y;
                double d1 = n * backlashPulseMs - (y0 - southPositions[n].Y) / r;
                double d2 = (n - 1) * backlashPulseMs - (y0 - southPositions[n - 1].Y) / r;
                double d = (d1 + d2) / 2;
                backlashMs = Math.Max(0, d);
                backlashState = d >= 50 ? CoachBacklashStates.Measured : CoachBacklashStates.None;
            }
            else
            {
                PrependFirst(SouthStep);
            }
        });
    }

    /// <summary>Takes up the Dec gear slack in <paramref name="dir"/> before response pulses in that direction.</summary>
    private void Engage(GuideDirection dir)
    {
        if (lastDecDirection == dir)
        {
            return;
        }

        var f = frame!;
        int pulse = backlashPulseMs > 0 ? backlashPulseMs : BacklashPulse(f);
        int ms = backlashMs is { } d ? (int)Math.Min(f.MaxDecDurationMs, d + pulse) : Math.Min(f.MaxDecDurationMs, 2 * pulse);
        if (!HasRoom(dir, pulse))
        {
            return;
        }

        Issue(dir, ms, null);
        if (backlashMs is { } dd && dd + pulse > f.MaxDecDurationMs)
        {
            // the take-up exceeds the max pulse: continue in further pulses
            double remaining = dd + pulse - f.MaxDecDurationMs;
            PrependFirst(() => EngageRemainder(dir, remaining));
        }
    }

    private void EngageRemainder(GuideDirection dir, double remainingMs)
    {
        var f = frame!;
        int ms = (int)Math.Min(f.MaxDecDurationMs, Math.Ceiling(remainingMs));
        if (ms <= 0 || !HasRoom(dir, ms))
        {
            return;
        }

        Issue(dir, ms, null);
        if (remainingMs > ms)
        {
            PrependFirst(() => EngageRemainder(dir, remainingMs - ms));
        }
    }

    private void RecenterStep()
    {
        var f = frame!;
        var ofs = f.MountOffset;
        if (!ofs.IsValid || recenterSteps >= MaxRecenterSteps || (Math.Abs(ofs.X) < 1.0 && Math.Abs(ofs.Y) < 1.0))
        {
            return;
        }

        recenterSteps++;
        var list = new List<PulseCommand>(2);
        var ra = f.Transform.RaPulse(ofs.X);
        if (Math.Abs(ofs.X) >= 1.0 && ra.DurationMs > 0)
        {
            list.Add(ra with { DurationMs = Math.Min(ra.DurationMs, f.MaxRaDurationMs) });
        }

        var dec = f.Transform.DecPulse(ofs.Y);
        if (Math.Abs(ofs.Y) >= 1.0 && dec.DurationMs > 0)
        {
            int extra = lastDecDirection is { } last && last != dec.Direction && backlashMs is { } d ? (int)d : 0;
            list.Add(dec with { DurationMs = Math.Min(dec.DurationMs + extra, f.MaxDecDurationMs) });
            lastDecDirection = dec.Direction;
        }

        if (list.Count > 0)
        {
            next = list;
            issued++;
            PrependFirst(RecenterStep);
        }
    }

    private void PrependEngage(GuideDirection dir) => PrependFirst(() => Engage(dir));

    private void PrependFirst(Action a)
    {
        var rest = plan.ToArray();
        plan.Clear();
        plan.Enqueue(a);
        foreach (var x in rest)
        {
            plan.Enqueue(x);
        }
    }

    private void FinishLocked()
    {
        if (finished)
        {
            return;
        }

        finished = true;
        pending = null;
        tcs.TrySetResult(new MountResponseResult(BuildResponse(), starLost, truncated, northRate));
    }

    private CoachResponse BuildResponse()
    {
        double scale = frame?.PixelScale is > 0 and var s ? s : 1.0;
        double? rate = northRate ?? frame?.Transform.YRate;
        int? minRa = MountResponseAnalyzer.MinEffectivePulse(pulses, ra: true);
        int? minDec = MountResponseAnalyzer.MinEffectivePulse(pulses, ra: false);
        int raBasis = MountResponseAnalyzer.RateBasis(pulses, true, minRa);
        int decBasis = MountResponseAnalyzer.RateBasis(pulses, false, minDec);
        return new CoachResponse
        {
            BacklashMs = backlashMs is { } b && backlashState is CoachBacklashStates.Measured or CoachBacklashStates.None ? Math.Round(b) : null,
            BacklashArcsec = backlashMs is { } b2 && rate is { } r && backlashState is CoachBacklashStates.Measured or CoachBacklashStates.None
                ? Math.Round(b2 * r * scale, 2)
                : null,
            BacklashState = backlashState,
            BacklashPoints = backlashPoints.ToList(),
            Pulses = pulses.ToList(),
            MinEffectivePulseRaMs = minRa,
            MinEffectivePulseDecMs = minDec,
            AsymmetryRa = Round(MountResponseAnalyzer.Asymmetry(pulses, "West", "East", raBasis)),
            AsymmetryDec = Round(MountResponseAnalyzer.Asymmetry(pulses, "North", "South", decBasis)),
            RateRatioRa = Round(MountResponseAnalyzer.RateRatio(pulses, true, raBasis)),
            RateRatioDec = Round(MountResponseAnalyzer.RateRatio(pulses, false, decBasis)),
        };
    }

    private static double? Round(double? v) => v is { } d ? Math.Round(d, 3) : null;

    private static double Median(List<double> v)
    {
        var a = v.Order().ToArray();
        return a.Length == 0 ? 0 : a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
    }

    private sealed class Pending(Action<CoachFrame> complete)
    {
        public void Complete(CoachFrame after) => complete(after);
    }
}
