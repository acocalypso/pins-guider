// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using PinsGuider.Engine.Algorithms;
using PinsGuider.Engine.Core;

namespace PinsGuider.Engine.Tests.Algorithms;

[TestFixture]
public class AxisCorrectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);
    private const double XRate = 0.005; // 1 px = 200 ms
    private const double YRate = 0.004; // 1 px = 250 ms

    private static AxisCorrector Identity() => new(new IdentityAlgorithm(), new IdentityAlgorithm());

    [Test]
    public void PositiveOffsetsAreCorrectedWestAndSouth()
    {
        var c = Identity().GuideStep(1.0, 2.0, XRate, YRate, T0);
        c.RADirection.Should().Be(GuideDirection.West);
        c.DECDirection.Should().Be(GuideDirection.South);
        c.RADuration.Should().Be(200);
        c.DECDuration.Should().Be(500);
        c.Pulses.Should().Equal(new PulseCommand(GuideDirection.West, 200), new PulseCommand(GuideDirection.South, 500));
        c.RaCorrectionPx.Should().BeApproximately(1.0, 1e-12);
        c.DecCorrectionPx.Should().BeApproximately(2.0, 1e-12);
    }

    [Test]
    public void NegativeOffsetsAreCorrectedEastAndNorth()
    {
        var c = Identity().GuideStep(-1.0, -2.0, XRate, YRate, T0);
        c.RADirection.Should().Be(GuideDirection.East);
        c.DECDirection.Should().Be(GuideDirection.North);
        c.RaCorrectionPx.Should().BeApproximately(-1.0, 1e-12);
        c.DecCorrectionPx.Should().BeApproximately(-2.0, 1e-12);
    }

    [Test]
    public void ZeroOffsetsReportEastNorthWithoutPulses()
    {
        var c = Identity().GuideStep(0, 0, XRate, YRate, T0);
        c.RADirection.Should().Be(GuideDirection.East);
        c.DECDirection.Should().Be(GuideDirection.North);
        c.Pulses.Should().BeEmpty();
    }

    [Test]
    public void DurationRoundsHalfUp()
    {
        AxisCorrector.DurationFor(0.25, 0.5).Should().Be(1); // 0.5 -> 1
        AxisCorrector.DurationFor(0.75, 0.5).Should().Be(2); // 1.5 -> 2
        AxisCorrector.DurationFor(-0.5, 0.005).Should().Be(100);
        AxisCorrector.DurationFor(1.0, 0.0).Should().Be(0);
        AxisCorrector.DurationFor(1.0, double.NaN).Should().Be(0);
    }

    [Test]
    public void AlgorithmOutputDrivesDuration()
    {
        var corr = new AxisCorrector(new HysteresisAlgorithm(), new ResistSwitchAlgorithm());
        var c = corr.GuideStep(1.0, 0.1, XRate, YRate, T0);
        c.RADistanceRaw.Should().Be(1.0);
        c.RADistanceGuide.Should().BeApproximately(0.63, 1e-12);
        c.RADuration.Should().Be(126);
        c.DECDistanceGuide.Should().Be(0);
        c.DECDuration.Should().Be(0);
    }

    [Test]
    public void DecModeSuppressesDisallowedDirection()
    {
        var corr = Identity();
        corr.DecGuideMode = DecGuideMode.North;
        var c = corr.GuideStep(0, 1.0, XRate, YRate, T0);
        c.DECDirection.Should().Be(GuideDirection.South);
        c.DECDuration.Should().Be(0);
        c.DecSuppressedByMode.Should().BeTrue();
        corr.GuideStep(0, -1.0, XRate, YRate, T0).DECDuration.Should().Be(250);

        corr.DecGuideMode = DecGuideMode.Off;
        corr.GuideStep(0, -1.0, XRate, YRate, T0).DECDuration.Should().Be(0);
    }

    [Test]
    public void DecModeDoesNotAffectRecoveryMoves()
    {
        var corr = Identity();
        corr.DecGuideMode = DecGuideMode.Off;
        var c = corr.Move(0, 1.0, XRate, YRate, MoveOptions.RecoveryMove, T0)!;
        c.DECDuration.Should().Be(250);
    }

    [Test]
    public void ClampsAndFlagsLimitedAxes()
    {
        var corr = Identity();
        var c = corr.GuideStep(20.0, -20.0, XRate, YRate, T0);
        c.RADuration.Should().Be(2500);
        c.RALimited.Should().BeTrue();
        c.DECDuration.Should().Be(2500);
        c.DecLimited.Should().BeTrue();
    }

    [Test]
    public void RecoveryMovesAreNotClampedOrFiltered()
    {
        var corr = Identity();
        var c = corr.Move(20.0, 0, XRate, YRate, MoveOptions.RecoveryMove, T0)!;
        c.RADuration.Should().Be(4000);
        c.RALimited.Should().BeFalse();
    }

    [Test]
    public void RepeatedClampsRaiseAlertThroughPipeline()
    {
        var corr = Identity();
        for (int i = 0; i < 5; i++)
            corr.GuideStep(20.0, 0, XRate, YRate, T0.AddSeconds(i)).Alerts.Should().BeEmpty();
        corr.GuideStep(20.0, 0, XRate, YRate, T0.AddSeconds(5)).Alerts.Should().ContainSingle().Which.Axis.Should().Be(GuideAxis.Ra);
    }

    [Test]
    public void DitherStartsAlertGracePeriod()
    {
        var corr = Identity();
        corr.GuidingDithered(3, 2, T0);
        for (int i = 0; i < 10; i++)
            corr.GuideStep(20.0, 0, XRate, YRate, T0.AddSeconds(i)).Alerts.Should().BeEmpty();
    }

    [Test]
    public void BacklashCompIsAddedOnDecReversalAndExcludedFromCorrectionEstimate()
    {
        var corr = new AxisCorrector(new IdentityAlgorithm(), new IdentityAlgorithm(), backlash: new BacklashCompensation(300, 20, 600, enabled: true));
        corr.GuideStep(0, 1.0, XRate, YRate, T0).BacklashCompMs.Should().Be(0);
        var c = corr.GuideStep(0, -1.0, XRate, YRate, T0);
        c.BacklashCompMs.Should().Be(300);
        c.DECDuration.Should().Be(550);
        c.DecCorrectionPx.Should().BeApproximately(-1.0, 1e-12);
    }

    [Test]
    public void GuidingDisabledProducesNoPulsesButAlgorithmsRun()
    {
        var hyst = new HysteresisAlgorithm();
        var corr = new AxisCorrector(hyst, new IdentityAlgorithm());
        corr.SetGuidingEnabled(false, T0);
        var c = corr.GuideStep(1.0, 1.0, XRate, YRate, T0);
        c.Pulses.Should().BeEmpty();
        c.RADistanceGuide.Should().BeApproximately(0.63, 1e-12);

        // manual moves are still allowed
        corr.Move(1.0, 0, XRate, YRate, MoveOptions.Manual, T0)!.RADuration.Should().Be(200);

        corr.SetGuidingEnabled(true, T0); // resets algorithms
        corr.GuideStep(1.0, 0, XRate, YRate, T0).RADistanceGuide.Should().BeApproximately(0.63, 1e-12);
    }

    [Test]
    public void DeducedStepWithNothingToDoReturnsNull()
    {
        Identity().DeducedStep(XRate, YRate, T0).Should().BeNull();
    }

    [Test]
    public void DeducedStepUsesDeduceResult()
    {
        var corr = new AxisCorrector(new FakeDeduce(0.5), new IdentityAlgorithm());
        var c = corr.DeducedStep(XRate, YRate, T0)!;
        c.Options.Should().Be(MoveOptions.DeducedMove);
        c.RADistanceRaw.Should().Be(0.5);
        c.RADuration.Should().Be(100);
    }

    [Test]
    public void StoppingResetsAlgorithmsAndBacklashDirection()
    {
        var corr = new AxisCorrector(new HysteresisAlgorithm(), new IdentityAlgorithm(), backlash: new BacklashCompensation(300, 20, 600, true));
        corr.GuideStep(1.0, 1.0, XRate, YRate, T0);
        corr.GuidingStopped();
        corr.Backlash.LastDirection.Should().BeNull();
        corr.GuideStep(1.0, 0, XRate, YRate, T0).RADistanceGuide.Should().BeApproximately(0.63, 1e-12);
    }

    [Test]
    public void CreateDefaultUsesPhd2Defaults()
    {
        var corr = AxisCorrector.CreateDefault(0.25);
        corr.RaAlgorithm.Should().BeOfType<HysteresisAlgorithm>().Which.MinMove.Should().Be(0.25);
        corr.DecAlgorithm.Should().BeOfType<ResistSwitchAlgorithm>().Which.MinMove.Should().Be(0.25);
        corr.DecGuideMode.Should().Be(DecGuideMode.Auto);
        corr.Limiter.MaxRaDurationMs.Should().Be(2500);
    }

    private sealed class FakeDeduce(double amount) : IGuideAlgorithm
    {
        public string Name => "fake";

        public double MinMove { get; set; } = -1;

        public IReadOnlyList<string> ParamNames => [];

        public double Result(double input) => input;

        public double DeduceResult() => amount;

        public void Reset()
        {
        }

        public bool TryGetParam(string name, out double value)
        {
            value = 0;
            return false;
        }

        public bool TrySetParam(string name, double value) => false;
    }
}
