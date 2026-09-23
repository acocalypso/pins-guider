// SPDX-License-Identifier: MPL-2.0

using PinsGuider.Engine.Calibration;
using PinsGuider.Engine.Core;
using PinsGuider.Engine.Guiding;

namespace PinsGuider.Engine.Coach;

/// <summary>What the guide loop saw in one frame, handed to a coach frame hook (on the loop task).</summary>
internal sealed record CoachFrame
{
    public DateTimeOffset Time { get; init; }

    public long FrameNumber { get; init; }

    public GuiderState State { get; init; }

    /// <summary>Primary star measured this frame (not estimated from secondaries).</summary>
    public bool StarFound { get; init; }

    /// <summary>Primary position, camera px.</summary>
    public GuidePoint StarPosition { get; init; } = GuidePoint.Invalid;

    /// <summary>Offset from the lock position in mount axes, px (RA = X, Dec = Y).</summary>
    public GuidePoint MountOffset { get; init; } = GuidePoint.Invalid;

    /// <summary>Primary position transformed to mount axes, px (absolute; only differences are meaningful).</summary>
    public GuidePoint MountPosition { get; init; } = GuidePoint.Invalid;

    public double Snr { get; init; }

    public double Mass { get; init; }

    public double Hfd { get; init; }

    public int StarsUsed { get; init; }

    public required MountTransform Transform { get; init; }

    public int FrameWidth { get; init; }

    public int FrameHeight { get; init; }

    public int SearchRegion { get; init; }

    public double PixelScale { get; init; }

    public double ExposureMs { get; init; }

    public double? DeclinationDeg { get; init; }

    public int MaxRaDurationMs { get; init; }

    public int MaxDecDurationMs { get; init; }

    // normal guiding (observer hooks)
    public bool IsSettling { get; init; }

    public bool IsRecenterMove { get; init; }

    public int RaDurationMs { get; init; }

    public int DecDurationMs { get; init; }

    public GuideDirection? DecDirection { get; init; }

    public bool RaLimited { get; init; }

    public bool DecLimited { get; init; }
}

/// <summary>
/// Per-frame hook installed by a coach step. Called on the guide loop for every guide frame (star found or lost);
/// implementations must be quick and never block.
/// </summary>
internal interface ICoachFrameHook
{
    /// <summary>
    /// True: measurement mode — guiding output off, single-star tracking, frames excluded from statistics and safety
    /// monitors; pulses returned by <see cref="OnFrame"/> are issued. False: observe normal guiding (trials).
    /// </summary>
    bool SuspendsGuiding { get; }

    /// <summary>Handles a frame; returns pulses to issue after it (measurement mode only, clamped to the max durations).</summary>
    IReadOnlyList<PulseCommand> OnFrame(CoachFrame frame);

    /// <summary>
    /// Guiding stopped with an error while the hook was installed. Return true when the failure only ends the hook's
    /// measurement (e.g. a runaway during a trial); false interrupts the session.
    /// </summary>
    bool OnGuidingFailed(GuideErrorCode code);
}

/// <summary>Guiding Coach status update (event type "coach"; not a PHD2 event).</summary>
public sealed record CoachStatusEvent(DateTimeOffset Timestamp, CoachStatus Status) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Coach";
}

/// <summary>A new live coaching hint (event type "hint"; not a PHD2 event).</summary>
public sealed record CoachHintEvent(DateTimeOffset Timestamp, CoachFinding Hint) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Hint";
}
