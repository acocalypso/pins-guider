// SPDX-License-Identifier: MPL-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using PinsGuider.Engine.Algorithms;
using PinsGuider.Engine.Calibration;
using PinsGuider.Engine.Coach;
using PinsGuider.Engine.Core;
using PinsGuider.Engine.MultiStar;

namespace PinsGuider.Engine.Guiding;

/// <summary>
/// Guiding Coach integration: temporary settings overlay, measurement/observer frame hooks, session interrupts,
/// camera lease for measurements outside the loop, events published from other threads and live hints.
/// </summary>
public sealed partial class Guider
{
    private readonly ConcurrentQueue<GuiderEvent> publishedEvents = new();
    private readonly object publishLock = new();
    private readonly LiveHintAnalyser liveHints = new();

    private Action<string>? coachInterrupt;
    private ICoachFrameHook? coachHook;
    private Func<GuiderSettings, GuiderSettings>? coachOverlay;
    private bool coachFailureHandled;
    private volatile bool coachStoppingCapture;
    private bool cameraLeased;
    private bool deferredLoopStart;
    private CancellationToken deferredLoopToken;
    private bool forcedRestart;
    private CancellationToken forcedRestartToken;

    /// <summary>Active live coaching hints (not expired, not dismissed) of the current guiding session.</summary>
    public IReadOnlyList<CoachFinding> ActiveHints => liveHints.GetActive(clock.UtcNow);

    /// <summary>Hides a live hint for the rest of the guiding session. Returns false for an unknown id.</summary>
    public bool DismissHint(string id) => liveHints.Dismiss(id);

    /// <summary>True while a Guiding Coach session is attached (guiding commands interrupt it).</summary>
    public bool IsCoachAttached => Volatile.Read(ref coachInterrupt) is not null;

    /// <summary>The live hint analyser (e.g. to hand it the seeing floor measured by the coach).</summary>
    public LiveHintAnalyser LiveHints => liveHints;

    internal ICameraSource Camera => camera;

    internal IPulseOutput Output => output;

    internal IMountState MountState => mount;

    internal IClock Clock => clock;

    /// <summary>Settings as set by the host, without the coach's temporary overlay.</summary>
    internal GuiderSettings BaseSettings => baseSettings;

    internal int TrackerSearchRegion => tracker.SearchRegion;

    /// <summary>Guiding output state of the corrector (false only during coach measurements).</summary>
    internal bool GuidingOutputEnabled => corrector.GuidingEnabled;

    internal bool HasCoachOverlay => coachOverlay is not null;

    internal bool HasCoachHook => coachHook is not null;

    private bool IsCoachMeasuring => coachHook is { SuspendsGuiding: true };

    #region coach session API (internal)

    /// <summary>Attaches a coach session; <paramref name="onInterrupt"/> is called once when a host command or event interrupts it.</summary>
    internal bool AttachCoach(Action<string> onInterrupt) => Interlocked.CompareExchange(ref coachInterrupt, onInterrupt, null) is null;

    /// <summary>Detaches the session (no further interrupts). Does not restore state; see <see cref="ClearCoachStateAsync"/>.</summary>
    internal void DetachCoach() => Volatile.Write(ref coachInterrupt, null);

    /// <summary>Installs (or removes with null) the frame hook; completes when the loop applied it.</summary>
    internal Task SetCoachHookAsync(ICoachFrameHook? hook) => RunOnLoopAsync(() => SetCoachHookCore(hook));

    /// <summary>Sets (or clears with null) the temporary settings overlay applied on top of the host settings.</summary>
    internal Task SetCoachOverlayAsync(Func<GuiderSettings, GuiderSettings>? overlay) => RunOnLoopAsync(() =>
    {
        coachOverlay = overlay;
        ApplySettings(ComposeSettings());
    });

    /// <summary>Removes hook and overlay: guiding output re-enabled, host settings restored.</summary>
    internal Task ClearCoachStateAsync() => RunOnLoopAsync(ClearCoachStateCore);

    internal Task<SettleResult> StartGuidingForCoachAsync(SettleParams settleParams, CancellationToken ct, CancellationToken loopToken) =>
        StartGuidingCoreAsync(settleParams, false, ct, loopToken, forceLoop: true);

    internal void StopGuidingForCoach() => Post(() => StopGuidingCore(GuiderState.Selected));

    /// <summary>Starts looping (e.g. after the camera check stopped the loop) without interrupting the session.</summary>
    internal void StartLoopingForCoach(CancellationToken loopToken)
    {
        Post(() =>
        {
            if (state is GuiderState.Stopped or GuiderState.Failed)
            {
                ClearRequests();
                SetState(GuiderState.Looping);
            }
        });
        EnsureLoop(loopToken, force: true);
    }

    /// <summary>Executes queued commands on the caller when the loop is not running (commands posted as it ended).</summary>
    internal void FlushCommandsIfIdle()
    {
        if (!IsLoopRunning)
        {
            DrainCommands();
        }
    }

    internal void PauseForCoach() => PauseCore(false);

    /// <summary>Stops the capture loop without interrupting the attached session (camera check).</summary>
    internal async Task StopCaptureForCoachAsync()
    {
        coachStoppingCapture = true;
        try
        {
            await StopCaptureCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            coachStoppingCapture = false;
        }
    }

    /// <summary>Reserves the camera for captures outside the loop. False while the loop runs.</summary>
    internal bool TryLeaseCamera()
    {
        lock (loopLock)
        {
            if (cameraLeased || loopTask is { IsCompleted: false })
            {
                return false;
            }

            cameraLeased = true;
            return true;
        }
    }

    /// <summary>Releases the camera; a loop start requested meanwhile happens now.</summary>
    internal void ReleaseCamera()
    {
        bool start;
        CancellationToken token;
        lock (loopLock)
        {
            cameraLeased = false;
            start = deferredLoopStart;
            token = deferredLoopToken;
            deferredLoopStart = false;
            deferredLoopToken = default;
        }

        if (start)
        {
            EnsureLoop(token, force: true);
        }
    }

    /// <summary>Publishes an event from outside the loop; delivered by the loop (deferred behind pulses) or directly when it is not running.</summary>
    internal void PublishEvent(GuiderEvent e)
    {
        publishedEvents.Enqueue(e);
        if (!IsLoopRunning)
        {
            lock (publishLock)
            {
                while (publishedEvents.TryDequeue(out var ev))
                {
                    Deliver(ev);
                }
            }
        }
    }

    /// <summary>Runs <paramref name="action"/> on the loop between frames, or right away when the loop is not running.</summary>
    private Task RunOnLoopAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (!IsLoopRunning)
        {
            DrainCommands();
        }

        return tcs.Task;
    }

    #endregion

    private GuiderSettings ComposeSettings()
    {
        if (coachOverlay is not { } overlay)
        {
            return baseSettings;
        }

        try
        {
            return overlay(baseSettings);
        }
        catch
        {
            return baseSettings;
        }
    }

    private void SetCoachHookCore(ICoachFrameHook? hook)
    {
        bool wasMeasuring = IsCoachMeasuring;
        coachHook = hook;
        bool measuring = IsCoachMeasuring;
        var now = clock.UtcNow;
        if (measuring && !wasMeasuring)
        {
            corrector.SetGuidingEnabled(false, now);
            recenter.Cancel();
            response.Reset();
            runaway.Reset();
        }
        else if (!measuring && wasMeasuring)
        {
            EndMeasurement(now);
        }
    }

    private void EndMeasurement(DateTimeOffset now)
    {
        corrector.SetGuidingEnabled(true, now);
        corrector.Backlash.ResetState();
        runaway.Reset();
        response.Reset();
        tracker.Distances.NeedReset = true;
    }

    private void ClearCoachStateCore()
    {
        SetCoachHookCore(null);
        if (coachOverlay is not null)
        {
            coachOverlay = null;
            ApplySettings(ComposeSettings());
        }
    }

    /// <summary>Called by host commands before they act: the session ends and its state is restored before the command runs.</summary>
    private void InterruptCoach(string reason)
    {
        var cb = Interlocked.Exchange(ref coachInterrupt, null);
        if (cb is null)
        {
            return;
        }

        Post(ClearCoachStateCore);
        if (!IsLoopRunning)
        {
            DrainCommands();
        }

        SafeInvoke(cb, reason);
    }

    /// <summary>Interrupt detected on the loop (mount moved, loop ended, guiding failed).</summary>
    private void InterruptCoachOnLoop(string reason)
    {
        var cb = Interlocked.Exchange(ref coachInterrupt, null);
        if (cb is null)
        {
            return;
        }

        ClearCoachStateCore();
        SafeInvoke(cb, reason);
    }

    private static void SafeInvoke(Action<string> cb, string reason)
    {
        try
        {
            cb(reason);
        }
        catch
        {
            // the session handles its own errors
        }
    }

    private void OnGuiderFailedForCoach(GuideErrorCode code)
    {
        if (Volatile.Read(ref coachInterrupt) is null)
        {
            return;
        }

        if (coachHook is { } hook)
        {
            bool handled;
            try
            {
                handled = hook.OnGuidingFailed(code);
            }
            catch
            {
                handled = false;
            }

            if (handled)
            {
                // only the hook's measurement ends (e.g. a runaway during a trial); the session restarts guiding
                coachFailureHandled = true;
                SetCoachHookCore(null);
                return;
            }
        }

        InterruptCoachOnLoop(code is GuideErrorCode.CameraFailed or GuideErrorCode.CameraDisconnected or GuideErrorCode.MountDisconnected
            ? CoachInterruptReasons.Disconnect
            : CoachInterruptReasons.Stopped);
    }

    private void OnLoopEndedForCoach()
    {
        if (!coachStoppingCapture && !coachFailureHandled)
        {
            InterruptCoachOnLoop(CoachInterruptReasons.Stopped);
        }
        else if (IsCoachMeasuring)
        {
            SetCoachHookCore(null);
        }
    }

    private void DrainPublishedEvents()
    {
        while (publishedEvents.TryDequeue(out var e))
        {
            Emit(e);
        }
    }

    private CoachFrame BuildCoachFrame(GuideFrame frame, MultiStarFrameResult r, MountTransform t, GuidePoint mountOfs, Core.MountSnapshot snapshot,
        DateTimeOffset now, bool found, GuideCorrection? corr, bool recenterMove) => new()
    {
        Time = now,
        FrameNumber = frame.FrameNumber,
        State = state,
        StarFound = found,
        StarPosition = found ? r.Primary.Position : GuidePoint.Invalid,
        MountOffset = found ? mountOfs : GuidePoint.Invalid,
        MountPosition = found ? t.CameraToMount(r.Primary.Position) : GuidePoint.Invalid,
        Snr = r.Primary.Snr,
        Mass = r.Primary.Mass,
        Hfd = r.Primary.Hfd,
        StarsUsed = r.StarsUsed,
        Transform = t,
        FrameWidth = frame.Width,
        FrameHeight = frame.Height,
        SearchRegion = tracker.SearchRegion,
        PixelScale = pixelScale,
        ExposureMs = settings.ExposureMs,
        DeclinationDeg = snapshot.DeclinationDeg,
        MaxRaDurationMs = settings.MaxRaDurationMs,
        MaxDecDurationMs = settings.MaxDecDurationMs,
        IsSettling = settle.IsActive,
        IsRecenterMove = recenterMove,
        RaDurationMs = corr?.RADuration ?? 0,
        DecDurationMs = corr?.DECDuration ?? 0,
        DecDirection = corr is { DECDuration: > 0 } ? corr.DECDirection : null,
        RaLimited = corr?.RALimited ?? false,
        DecLimited = corr?.DecLimited ?? false,
    };

    private IReadOnlyList<PulseCommand> CoachMeasurementFrame(GuideFrame frame, MultiStarFrameResult r, MountTransform t, GuidePoint mountOfs,
        Core.MountSnapshot snapshot, DateTimeOffset now, Stopwatch sw, bool raOnly)
    {
        Emit(new GuideStepEvent(now)
        {
            Frame = frame.FrameNumber,
            Time = (now - guideStart).TotalSeconds,
            Mount = MountName,
            Dx = r.CameraOffset.X,
            Dy = r.CameraOffset.Y,
            RaDistanceRaw = mountOfs.X,
            DecDistanceRaw = mountOfs.Y,
            StarMass = r.Primary.Mass,
            Snr = r.Primary.Snr,
            Hfd = r.Primary.Hfd,
            AvgDist = tracker.Distances.CurrentError(raOnly, now.ToUnixTimeMilliseconds()),
            ErrorCode = (int)r.ErrorCode,
            PixelScale = pixelScale,
            LockPosition = lockPosition,
            StarPosition = r.Primary.Position,
            StarsUsed = r.StarsUsed,
            PrimaryEstimated = r.IsEstimated,
            Stars = ToStarInfos(r),
            ProcessingMs = sw.Elapsed.TotalMilliseconds,
            CoachMeasurement = true,
        });

        var hook = coachHook!;
        bool found = r.Outcome == TrackerOutcome.Found;
        IReadOnlyList<PulseCommand> requested;
        try
        {
            requested = hook.OnFrame(BuildCoachFrame(frame, r, t, mountOfs, snapshot, now, found, null, false));
        }
        catch
        {
            InterruptCoachOnLoop(CoachInterruptReasons.Error);
            return [];
        }

        if (!found || requested.Count == 0)
        {
            return [];
        }

        // measurement pulses never exceed the configured max pulse
        return requested.Where(p => p.DurationMs > 0)
            .Select(p => p with { DurationMs = Math.Min(p.DurationMs, p.Direction.Axis() == GuideAxis.Ra ? settings.MaxRaDurationMs : settings.MaxDecDurationMs) })
            .ToList();
    }

    private void CoachObserveFrame(GuideFrame frame, MultiStarFrameResult r, MountTransform t, GuidePoint mountOfs, Core.MountSnapshot snapshot,
        DateTimeOffset now, GuideCorrection? corr, bool recenterMove)
    {
        if (coachHook is not { SuspendsGuiding: false } hook)
        {
            return;
        }

        try
        {
            hook.OnFrame(BuildCoachFrame(frame, r, t, mountOfs, snapshot, now, r.StarFound, corr, recenterMove));
        }
        catch
        {
            InterruptCoachOnLoop(CoachInterruptReasons.Error);
        }
    }

    private void CoachStarLostFrame(GuideFrame frame, MultiStarFrameResult r, MountTransform t, Core.MountSnapshot snapshot, DateTimeOffset now)
    {
        if (coachHook is not { } hook)
        {
            return;
        }

        try
        {
            hook.OnFrame(BuildCoachFrame(frame, r, t, GuidePoint.Invalid, snapshot, now, false, null, false));
        }
        catch
        {
            InterruptCoachOnLoop(CoachInterruptReasons.Error);
        }
    }

    private void UpdateLiveHints(GuideCorrection corr, MultiStarFrameResult r, DateTimeOffset now)
    {
        if (!settings.LiveHints || lastStats is null || Volatile.Read(ref coachInterrupt) is not null || state != GuiderState.Guiding)
        {
            return;
        }

        var input = new LiveHintInput
        {
            Time = now,
            Stats = lastStats,
            RaLimited = corr.RALimited,
            DecLimited = corr.DecLimited,
            RaDurationMs = corr.RADuration,
            DecDurationMs = corr.DECDuration,
            DecDirection = corr.DECDuration > 0 ? corr.DECDirection : null,
            Snr = r.Primary.Snr,
            Settings = settings,
        };

        foreach (var hint in liveHints.Analyse(input))
        {
            Emit(new CoachHintEvent(now, hint));
        }
    }

    private void ResetLiveHints() => liveHints.ResetSession();
}
