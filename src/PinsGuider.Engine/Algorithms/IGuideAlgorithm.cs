// SPDX-License-Identifier: MPL-2.0

namespace PinsGuider.Engine.Algorithms;

// API CONTRACT (M1 scaffold).

/// <summary>Per-axis guide algorithm (port of PHD2 GuideAlgorithm). Input/output in mount-axis pixels.</summary>
public interface IGuideAlgorithm
{
    string Name { get; }

    double MinMove { get; set; }

    /// <summary>Returns the correction (pixels) for the measured axis error.</summary>
    double Result(double input);

    /// <summary>Correction to apply when no measurement is available (dark guiding). 0 by default.</summary>
    double DeduceResult() => 0.0;

    void Reset();

    void GuidingStarted() => Reset();

    void GuidingStopped() => Reset();

    void GuidingPaused()
    {
    }

    void GuidingResumed()
    {
    }

    void GuidingDithered(double amount)
    {
    }

    void GuidingDitherSettleDone(bool success)
    {
    }

    void DirectMoveApplied(double amount)
    {
    }

    IReadOnlyList<string> ParamNames { get; }

    bool TryGetParam(string name, out double value);

    bool TrySetParam(string name, double value);
}
