// SPDX-License-Identifier: MPL-2.0

namespace PinsGuider.Engine.Core;

public enum GuideDirection
{
    North,
    South,
    East,
    West,
}

public enum GuideAxis
{
    Ra,
    Dec,
}

public enum PierSide
{
    Unknown,
    East,
    West,
}

public enum DecGuideMode
{
    Off,
    Auto,
    North,
    South,
}

public static class GuideDirectionExtensions
{
    public static GuideAxis Axis(this GuideDirection d) => d is GuideDirection.North or GuideDirection.South ? GuideAxis.Dec : GuideAxis.Ra;

    public static GuideDirection Opposite(this GuideDirection d) => d switch
    {
        GuideDirection.North => GuideDirection.South,
        GuideDirection.South => GuideDirection.North,
        GuideDirection.East => GuideDirection.West,
        _ => GuideDirection.East,
    };

    public static PierSide Opposite(this PierSide p) => p switch
    {
        PierSide.East => PierSide.West,
        PierSide.West => PierSide.East,
        _ => PierSide.Unknown,
    };
}
