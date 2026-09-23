// SPDX-License-Identifier: MPL-2.0

using PinsGuider.Engine.Core;

namespace PinsGuider.Engine.Calibration;

// API CONTRACT (M1 scaffold). Implementations may add members but must keep these.

public enum GuideParity
{
    Unknown,
    Even,
    Odd,
    Unchanged,
}

/// <summary>Result of a calibration (port of PHD2 Calibration struct).</summary>
public sealed record CalibrationData
{
    /// <summary>Camera angle of the RA (west) axis, radians.</summary>
    public double XAngle { get; init; }

    /// <summary>Camera angle of the Dec (north) axis, radians.</summary>
    public double YAngle { get; init; }

    /// <summary>RA rate, pixels per millisecond of pulse at <see cref="Binning"/>.</summary>
    public double XRate { get; init; }

    /// <summary>Dec rate, pixels per millisecond; NaN/0 when Dec was not calibrated.</summary>
    public double YRate { get; init; }

    /// <summary>Declination at calibration, radians; null when unknown.</summary>
    public double? Declination { get; init; }

    public PierSide PierSide { get; init; }

    public double? RotatorAngleDeg { get; init; }

    public int Binning { get; init; } = 1;

    public GuideParity RaGuideParity { get; init; }

    public GuideParity DecGuideParity { get; init; }

    public double? GuideRateRa { get; init; }

    public double? GuideRateDec { get; init; }

    public double PixelScale { get; init; }

    public double PixelSizeUm { get; init; }

    public double FocalLengthMm { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public bool IsValid => XRate > 0 && !double.IsNaN(XAngle);
}
