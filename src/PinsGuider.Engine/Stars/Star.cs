// SPDX-License-Identifier: MPL-2.0

using PinsGuider.Engine.Core;

namespace PinsGuider.Engine.Stars;

// API CONTRACT (M1 scaffold). Implementations may add members but must keep these signatures.

public enum StarFindMode
{
    Centroid,
    Peak,
}

public enum StarFindResult
{
    Ok = 0,
    Saturated,
    LowSnr,
    LowMass,
    LowHfd,
    HighHfd,
    TooNearEdge,
    MassChange,
    Error,
}

/// <summary>A measured star (port of PHD2 Star).</summary>
public class Star
{
    public GuidePoint Position { get; set; } = GuidePoint.Invalid;

    public double X => Position.X;

    public double Y => Position.Y;

    public bool IsValid => Position.IsValid;

    public double Mass { get; set; }

    public double Snr { get; set; }

    public double Hfd { get; set; }

    public ushort PeakValue { get; set; }

    public StarFindResult LastFindResult { get; set; } = StarFindResult.Error;

    public static bool WasFound(StarFindResult r) => r is StarFindResult.Ok or StarFindResult.Saturated;

    public bool WasFound() => IsValid && WasFound(LastFindResult);

    public void Invalidate() => Position = GuidePoint.Invalid;

    public void SetError(StarFindResult error) => LastFindResult = error;

    /// <summary>
    /// Finds the star near (<paramref name="baseX"/>, <paramref name="baseY"/>) within
    /// ±<paramref name="searchRegion"/>. Returns true when the star was found (Ok or Saturated).
    /// Port of PHD2 Star::Find. <paramref name="saturation"/> is the saturation ADU, 0 for
    /// auto-detection from the star profile.
    /// </summary>
    public virtual bool Find(GuideFrame img, int searchRegion, int baseX, int baseY, StarFindMode mode, double minHfd, double maxHfd, ushort saturation)
        => throw new NotImplementedException();

    /// <summary>Find using the star's current position as the search hint.</summary>
    public bool Find(GuideFrame img, int searchRegion, StarFindMode mode, double minHfd, double maxHfd, ushort saturation)
        => Find(img, searchRegion, (int)Math.Round(X), (int)Math.Round(Y), mode, minHfd, maxHfd, saturation);
}

/// <summary>A star tracked by the guider (port of PHD2 GuideStar).</summary>
public class GuideStar : Star
{
    public GuideStar()
    {
    }

    public GuideStar(Star s)
    {
        Position = s.Position;
        Mass = s.Mass;
        Snr = s.Snr;
        Hfd = s.Hfd;
        PeakValue = s.PeakValue;
        LastFindResult = s.LastFindResult;
        ReferencePoint = s.Position;
    }

    public GuidePoint ReferencePoint { get; set; } = GuidePoint.Invalid;

    public int MissCount { get; set; }

    public int ZeroCount { get; set; }

    /// <summary>Offset from the primary star, set in AutoFind.</summary>
    public GuidePoint OffsetFromPrimary { get; set; } = new(0, 0);

    public bool WasLost { get; set; }
}
