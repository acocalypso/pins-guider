// SPDX-License-Identifier: MPL-2.0

using PinsGuider.Engine.Core;

namespace PinsGuider.Engine.Guiding;

/// <summary>
/// Base of all guider events. <see cref="Phd2Name"/> is the PHD2 event-server name so hosts can map
/// events 1:1 to PHD2 semantics (logs, UI markers, sequencer expectations).
/// </summary>
public abstract record GuiderEvent(DateTimeOffset Timestamp)
{
    public abstract string Phd2Name { get; }
}

/// <summary>Per-star measurement for UI overlays.</summary>
public sealed record StarInfo(double X, double Y, double Snr, double Mass, double Hfd, bool IsPrimary, bool Used, double Weight, string? RejectReason);

/// <summary>PHD2 GuideStep plus extensions (per-star data, estimation flag, arcsec values).</summary>
public sealed record GuideStepEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "GuideStep";

    public long Frame { get; init; }

    /// <summary>Seconds since guiding started.</summary>
    public double Time { get; init; }

    public string Mount { get; init; } = "Mount";

    /// <summary>Camera-axis offset from the lock position, px.</summary>
    public double Dx { get; init; }

    public double Dy { get; init; }

    /// <summary>Mount-axis error, px (PHD2 RADistanceRaw / DECDistanceRaw).</summary>
    public double RaDistanceRaw { get; init; }

    public double DecDistanceRaw { get; init; }

    /// <summary>Algorithm output, px (PHD2 RADistanceGuide / DECDistanceGuide).</summary>
    public double RaDistanceGuide { get; init; }

    public double DecDistanceGuide { get; init; }

    public int RaDuration { get; init; }

    public GuideDirection? RaDirection { get; init; }

    public int DecDuration { get; init; }

    public GuideDirection? DecDirection { get; init; }

    public bool RaLimited { get; init; }

    public bool DecLimited { get; init; }

    public double StarMass { get; init; }

    public double Snr { get; init; }

    public double Hfd { get; init; }

    /// <summary>Smoothed distance (PHD2 AvgDist).</summary>
    public double AvgDist { get; init; }

    public int ErrorCode { get; init; }

    /// <summary>Image scale used for arcsec conversion.</summary>
    public double PixelScale { get; init; }

    public GuidePoint LockPosition { get; init; }

    public GuidePoint StarPosition { get; init; }

    public int StarsUsed { get; init; }

    public bool PrimaryEstimated { get; init; }

    public bool IsSettling { get; init; }

    /// <summary>True for fast-recenter moves after dither (bypass algorithms).</summary>
    public bool IsRecenterMove { get; init; }

    public IReadOnlyList<StarInfo> Stars { get; init; } = [];

    public double ProcessingMs { get; init; }

    /// <summary>Guiding Coach measurement frame: guiding output off, excluded from statistics (no correction fields).</summary>
    public bool CoachMeasurement { get; init; }
}

public sealed record StarLostEvent(DateTimeOffset Timestamp, long Frame, double Time, double StarMass, double Snr, double Hfd, double AvgDist, int ErrorCode, string Status)
    : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "StarLost";
}

public sealed record AppStateEvent(DateTimeOffset Timestamp, GuiderState State, GuiderState Previous) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "AppState";
}

public sealed record LoopingExposuresEvent(DateTimeOffset Timestamp, long Frame) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "LoopingExposures";
}

public sealed record LoopingExposuresStoppedEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "LoopingExposuresStopped";
}

public sealed record StarSelectedEvent(DateTimeOffset Timestamp, double X, double Y) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "StarSelected";
}

public sealed record LockPositionSetEvent(DateTimeOffset Timestamp, double X, double Y) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "LockPositionSet";
}

public sealed record LockPositionLostEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "LockPositionLost";
}

public sealed record StartCalibrationEvent(DateTimeOffset Timestamp, string Mount) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "StartCalibration";
}

public sealed record CalibratingEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Calibrating";

    public string Mount { get; init; } = "Mount";

    /// <summary>Direction name as PHD2 ("West", "East", "Backlash", "North", "South").</summary>
    public string Direction { get; init; } = string.Empty;

    public double Distance { get; init; }

    public double Dx { get; init; }

    public double Dy { get; init; }

    public GuidePoint Position { get; init; }

    public int Step { get; init; }

    public string State { get; init; } = string.Empty;

    /// <summary>Progress hint for the UI (0..1).</summary>
    public double Progress { get; init; }
}

public sealed record CalibrationCompleteEvent(DateTimeOffset Timestamp, string Mount, Calibration.CalibrationData Calibration) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "CalibrationComplete";
}

public sealed record CalibrationFailedEvent(DateTimeOffset Timestamp, string Mount, string Reason, GuideErrorCode Code) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "CalibrationFailed";
}

public sealed record CalibrationDataFlippedEvent(DateTimeOffset Timestamp, string Mount) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "CalibrationDataFlipped";
}

public sealed record StartGuidingEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "StartGuiding";
}

public sealed record GuidingStoppedEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "GuidingStopped";
}

public sealed record PausedEvent(DateTimeOffset Timestamp, string Reason) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Paused";
}

public sealed record ResumedEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Resumed";
}

public sealed record GuidingDitheredEvent(DateTimeOffset Timestamp, double Dx, double Dy) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "GuidingDithered";
}

public sealed record SettleBeginEvent(DateTimeOffset Timestamp) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "SettleBegin";
}

public sealed record SettlingEvent(DateTimeOffset Timestamp, double Distance, double Time, double SettleTime, bool StarLocked) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Settling";
}

public sealed record SettleDoneEvent(DateTimeOffset Timestamp, int Status, string? Error, int TotalFrames, int DroppedFrames) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "SettleDone";

    public bool Succeeded => Status == 0;
}

/// <summary>Alert with a stable error code (PHD2 'Alert' carries Msg + Type).</summary>
public sealed record AlertEvent(DateTimeOffset Timestamp, GuideErrorCode Code, GuideErrorSeverity Severity, string Message, string? Detail = null) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "Alert";

    public string Type => Severity switch
    {
        GuideErrorSeverity.Info => "info",
        GuideErrorSeverity.Warning => "warning",
        _ => "error",
    };
}

/// <summary>A processed frame is available (for the live view). Not part of PHD2's event set.</summary>
public sealed record FrameReadyEvent(DateTimeOffset Timestamp, GuideFrame Frame, IReadOnlyList<StarInfo> Stars, GuidePoint LockPosition) : GuiderEvent(Timestamp)
{
    public override string Phd2Name => "FrameReady";
}
