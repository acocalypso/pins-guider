// SPDX-License-Identifier: MPL-2.0

namespace PinsGuider.Engine.Stars;

/// <summary>Star detection settings (PHD2 defaults).</summary>
public sealed record StarFinderOptions
{
    public int SearchRegion { get; init; } = 15;

    public double MinHfd { get; init; } = 1.5;

    public double MaxHfd { get; init; } = 20.0;

    /// <summary>Minimum SNR for auto-selected stars.</summary>
    public double MinSnr { get; init; } = 6.0;

    /// <summary>Saturation ADU; 0 = detect from star profile.</summary>
    public ushort SaturationAdu { get; init; }

    public int MaxStars { get; init; } = 9;

    /// <summary>Auto-find downsample: 0 = auto (x2 when scale ≤ 0.6″/px), else 1 or 2.</summary>
    public int AutoFindDownsample { get; init; }

    /// <summary>Image scale ″/px, used by auto downsample.</summary>
    public double PixelScale { get; init; } = 1.0;
}
