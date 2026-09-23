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
public sealed record TrialStatistics(int Frames, double RmsRaArcsec, double RmsDecArcsec, double RmsTotalArcsec, double PeakArcsec, double OscillationIndex,
    double? SnrAvg)
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
        return new TrialStatistics(n, rr * scale, rd * scale, Math.Sqrt(rr * rr + rd * rd) * scale, peak * scale, 1.0 - (double)same / (n - 1),
            snr.Count > 0 ? snr.Average() : null);
    }
}

/// <summary>Outcome of a trial observation.</summary>
internal sealed record TrialObservation(IReadOnlyList<TrialSample> Samples, double ElapsedSeconds, GuideErrorCode? Failure, bool StarLost, double PixelScale);

/// <summary>Observes normal guiding for a trial (statistics and safety stay active).</summary>
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

    public bool SuspendsGuiding => false;

    public Task<TrialObservation> Completion => tcs.Task;

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
            if (finished)
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
