// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using PinsGuider.Engine.Core;
using PinsGuider.Engine.Guiding;
using PinsGuider.Engine.Simulation;

namespace PinsGuider.Engine.Tests.Guiding;

/// <summary>
/// End-to-end tests of <see cref="Guider"/> against the closed-loop simulator in virtual time.
/// Guiding quality is judged against the simulator's ground truth, not the guider's own measurements.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ClosedLoopTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    [Test]
    public async Task Calibrates_and_guides_a_good_mount()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        (await guide).Success.Should().BeTrue();
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle();
        var cal = h.Events.OfType<CalibrationCompleteEvent>().Single().Calibration;

        // expected RA rate: 0.5 × 15.041″/s × cos(20°) / scale, in px/ms
        double scale = h.Guider.PixelScale;
        double expectedX = 0.5 * 15.041 * Math.Cos(20 * Math.PI / 180) / scale / 1000.0;
        double expectedY = 0.5 * 15.041 / scale / 1000.0;
        cal.XRate.Should().BeApproximately(expectedX, expectedX * 0.1);
        cal.YRate.Should().BeApproximately(expectedY, expectedY * 0.1);
        Engine.Calibration.MountTransform.OrthogonalityErrorDegrees(cal.XAngle, cal.YAngle).Should().BeLessThan(5);

        var (guided, unguided) = h.TrueRms(fromSec: 300);
        TestContext.Out.WriteLine($"GoodMount true RMS guided {guided:F2}″ unguided {unguided:F2}″; guider RMS {h.Guider.Statistics!.Session.RmsTotalArcsec:F2}″");
        guided.Should().BeLessThan(0.8);
        guided.Should().BeLessThan(unguided * 0.5);
        h.Guider.State.Should().Be(GuiderState.Stopped, "the loop ended with the virtual-time cancellation");
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    [Test]
    public async Task Tames_large_periodic_error()
    {
        var h = new Harness(SimulatorScenario.PoorPeriodicError);
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(25));

        (await guide).Success.Should().BeTrue();
        var (guided, unguided) = h.TrueRms(fromSec: 300);
        TestContext.Out.WriteLine($"PoorPE true RMS guided {guided:F2}″ unguided {unguided:F2}″");
        guided.Should().BeLessThan(unguided * 0.25);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    [Test]
    public async Task Dithers_and_settles()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        var results = new List<Task<SettleResult>>();
        int dithers = 0;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && dithers < 3)
            {
                dithers++;
                results.Add(h.Guider.DitherAsync(5, false, new SettleParams(1.5, 10, 120)));
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        results.Should().HaveCount(3);
        foreach (var r in results)
        {
            (await r).Success.Should().BeTrue();
        }

        h.Events.OfType<GuidingDitheredEvent>().Should().HaveCount(3);
        h.Events.OfType<SettleDoneEvent>().Should().HaveCount(4).And.OnlyContain(s => s.Status == 0);
        h.Events.OfType<GuideStepEvent>().Should().Contain(s => s.IsRecenterMove);
    }

    [Test]
    public async Task Survives_passing_clouds()
    {
        var h = new Harness(SimulatorScenario.Clouds);
        var guide = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        (await guide).Success.Should().BeTrue();
        h.Events.OfType<StarLostEvent>().Should().NotBeEmpty();
        h.Events.OfType<AlertEvent>().Should().Contain(a => a.Code == GuideErrorCode.StarReacquired);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
        h.Events.OfType<GuideStepEvent>().Last().Time.Should().BeGreaterThan(400);
    }

    [Test]
    public async Task Fails_after_reacquire_timeout()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Sky = SimulatorScenario.GoodMount.Sky with { Transparency = [new TransparencyWindow(300, 500, 0.0, 2.0)] },
        };
        var h = new Harness(scenario);
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        h.Guider.State.Should().Be(GuiderState.Failed);
        h.Guider.LastError!.Code.Should().Be(GuideErrorCode.StarReacquireTimeout);
        var lost = h.Events.OfType<StarLostEvent>().First().Timestamp;
        var failed = h.Events.OfType<AlertEvent>().Single(a => a.Code == GuideErrorCode.StarReacquireTimeout).Timestamp;
        (failed - lost).TotalSeconds.Should().BeInRange(60, 66);
        h.Events.OfType<GuideStepEvent>().Where(s => s.Timestamp > lost).Should().BeEmpty("no corrections while the star is lost");
    }

    [Test]
    public async Task Stops_on_runaway_when_corrections_have_the_wrong_sign()
    {
        var h = new Harness(SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { DecDriftArcsecPerMin = 3 } });
        DateTimeOffset? faultAt = null;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && faultAt is null)
            {
                faultAt = e.Timestamp;
                h.Sim.Mount.InvertDecPulses = true;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        h.Guider.State.Should().Be(GuiderState.Failed);
        h.Guider.LastError!.Code.Should().Be(GuideErrorCode.RunawayDetected);
        var stop = h.Events.OfType<AlertEvent>().Single(a => a.Code == GuideErrorCode.RunawayDetected).Timestamp;
        TestContext.Out.WriteLine($"runaway detected {(stop - faultAt!.Value).TotalSeconds:F0} s after the fault; true error then {h.Sim.TruePointingError(h.Seconds(stop)).Dec:F1}″");
        (stop - faultAt!.Value).TotalMinutes.Should().BeLessThan(5);
    }

    [Test]
    public async Task Stops_when_the_mount_ignores_pulses()
    {
        var h = new Harness(SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { RaDriftArcsecPerMin = 20 } });
        DateTimeOffset? faultAt = null;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && faultAt is null)
            {
                faultAt = e.Timestamp;
                h.Sim.Mount.NotResponding = true;
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(15));

        h.Guider.State.Should().Be(GuiderState.Failed);
        h.Guider.LastError!.Code.Should().BeOneOf(GuideErrorCode.MountNotResponding, GuideErrorCode.RunawayDetected);
        var stop = h.Events.OfType<AlertEvent>().First(a => a.Severity == GuideErrorSeverity.Critical).Timestamp;
        (stop - faultAt!.Value).TotalSeconds.Should().BeLessThan(90);
    }

    [Test]
    public async Task Retries_transient_camera_failures_and_fails_on_persistent_ones()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        int steps = 0;
        h.OnEvent = e =>
        {
            if (e is GuideStepEvent && ++steps == 50)
            {
                h.Sim.Camera.InjectFaults(CaptureFault.Failure, 2);
            }

            if (e is GuideStepEvent && steps == 150)
            {
                h.Sim.Camera.InjectFaults(CaptureFault.Failure, 100);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        h.Events.OfType<AlertEvent>().Count(a => a.Code == GuideErrorCode.CameraCaptureFailed).Should().BeGreaterThanOrEqualTo(2);
        h.Events.OfType<AlertEvent>().Should().Contain(a => a.Code == GuideErrorCode.CameraReconnecting);
        h.Guider.State.Should().Be(GuiderState.Failed);
        h.Guider.LastError!.Code.Should().Be(GuideErrorCode.CameraFailed);
        steps.Should().BeGreaterThanOrEqualTo(150, "guiding continued after the transient failures");
    }

    [Test]
    public async Task Pauses_during_a_short_slew_and_resumes()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        bool slewed = false;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && !slewed)
            {
                slewed = true;
                h.Sim.Mount.StartSlew(new SkyOffset(0, 0), TimeSpan.FromSeconds(10));
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        h.Events.OfType<PausedEvent>().Should().ContainSingle(p => p.Reason == "Slewing");
        h.Events.OfType<ResumedEvent>().Should().ContainSingle();
        var resumed = h.Events.OfType<ResumedEvent>().Single().Timestamp;
        h.Events.OfType<GuideStepEvent>().Should().Contain(s => s.Timestamp > resumed && s.RaDuration + s.DecDuration > 0);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    [Test]
    public async Task Stays_paused_after_a_large_slew_until_resumed()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        bool slewed = false;
        DateTimeOffset? resumeRequestedAt = null;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && !slewed)
            {
                slewed = true;
                h.Sim.Mount.StartSlew(new SkyOffset(600, 300), TimeSpan.FromSeconds(10));
            }

            if (slewed && resumeRequestedAt is null && e is LoopingExposuresEvent or GuideStepEvent or StarLostEvent &&
                h.Seconds(e.Timestamp) > h.Seconds(h.Events.OfType<PausedEvent>().FirstOrDefault()?.Timestamp ?? DateTimeOffset.MaxValue) + 60)
            {
                resumeRequestedAt = e.Timestamp;
                h.Guider.Resume();
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(10));

        var paused = h.Events.OfType<PausedEvent>().Single().Timestamp;
        resumeRequestedAt.Should().NotBeNull();
        h.Events.OfType<GuideStepEvent>().Where(s => s.Timestamp > paused && s.Timestamp < resumeRequestedAt!.Value && s.RaDuration + s.DecDuration > 0)
            .Should().BeEmpty("no corrections while paused after a large move");
        h.Events.OfType<StarSelectedEvent>().Should().Contain(s => s.Timestamp > resumeRequestedAt!.Value, "a new star is selected after resuming");
        h.Events.OfType<StartGuidingEvent>().Should().HaveCount(2);
        h.Events.OfType<CalibrationCompleteEvent>().Should().ContainSingle("the calibration is reused");
    }

    [Test]
    public async Task Post_flip_dec_self_check_fixes_wrong_dec_flip_setting()
    {
        // the mount really requires a Dec flip after the meridian flip, but the setting says it doesn't
        var scenario = SimulatorScenario.GoodMount with
        {
            Mount = SimulatorScenario.GoodMount.Mount with { DecGuideReversedOnWestPier = false, DecDriftArcsecPerMin = 3 },
        };
        var h = new Harness(scenario);
        int phase = 0;
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (phase == 0 && e is SettleDoneEvent)
            {
                phase = 1;
                h.Guider.StopGuiding();
                h.Sim.Mount.MeridianFlip();
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        second.Should().NotBeNull();
        (await second!).Success.Should().BeTrue();
        h.Events.OfType<CalibrationDataFlippedEvent>().Should().ContainSingle();
        h.Events.OfType<AlertEvent>().Should().ContainSingle(a => a.Code == GuideErrorCode.DecFlipCorrected);
        h.Events.OfType<CalibrationUpdatedEvent>().Last().DecFlipRequired.Should().BeTrue();
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
        h.Guider.Settings.DecFlipRequired.Should().BeTrue();
    }

    [Test]
    public async Task Correct_dec_flip_setting_is_left_alone()
    {
        var h = new Harness(SimulatorScenario.GoodMount with { Mount = SimulatorScenario.GoodMount.Mount with { DecDriftArcsecPerMin = 3 } });
        int phase = 0;
        Task<SettleResult>? second = null;
        h.OnEvent = e =>
        {
            if (phase == 0 && e is SettleDoneEvent)
            {
                phase = 1;
                h.Guider.StopGuiding();
                h.Sim.Mount.MeridianFlip();
                second = h.Guider.StartGuidingAsync(Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(20));

        (await second!).Success.Should().BeTrue();
        h.Events.OfType<CalibrationDataFlippedEvent>().Should().ContainSingle();
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.DecFlipCorrected);
        h.Alerts(GuideErrorSeverity.Critical).Should().BeEmpty();
    }

    [Test]
    public async Task Emits_phd2_event_sequence_for_guide_start()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(8));

        var names = h.Events.Select(e => e.Phd2Name).Where(n => n is "StarSelected" or "StartCalibration" or "CalibrationComplete" or "StartGuiding"
            or "SettleBegin" or "SettleDone").Distinct().ToList();
        names.Should().Equal("StarSelected", "StartCalibration", "CalibrationComplete", "StartGuiding", "SettleBegin", "SettleDone");
        h.Events.OfType<CalibratingEvent>().Should().NotBeEmpty();
        var step = h.Events.OfType<GuideStepEvent>().Last();
        step.Snr.Should().BeGreaterThan(10);
        step.StarsUsed.Should().BeGreaterThan(1, "multi-star guiding is active on a rich field");
        step.Stars.Should().Contain(s => s.IsPrimary);
    }

    [Test]
    public async Task Writes_a_phd2_compatible_guide_log()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        var sw = new StringWriter();
        var log = new Engine.Logging.GuidingLog(sw, h.Clock);
        log.EnableLogging();
        using var bridge = new Engine.Logging.GuideLogBridge(h.Guider, log, h.Sim.Mount, () => new Engine.Logging.GuideLogContext
        {
            CameraName = "Simulator", MountName = "Simulated mount", SensorWidth = 1936, SensorHeight = 1216, PixelSizeUm = 5.86,
        });
        bool dithered = false;
        h.OnEvent = e =>
        {
            if (e is SettleDoneEvent && !dithered)
            {
                dithered = true;
                _ = h.Guider.DitherAsync(4, false, Settle);
            }
        };

        _ = h.Guider.StartGuidingAsync(Settle);
        await h.RunFor(TimeSpan.FromMinutes(6));
        log.Close();

        string text = sw.ToString();
        text.Should().Contain("Calibration Begins at").And.Contain("Calibration complete").And.Contain("Guiding Begins at");
        text.Should().Contain("Frame,Time,mount,dx,dy,RARawDistance,DECRawDistance,RAGuideDistance,DECGuideDistance");
        text.Should().Contain("INFO: DITHER").And.Contain("INFO: SETTLING STATE CHANGE, Settling complete");
        text.Should().Contain("Guiding Ends at");
        int rows = text.Split('\n').Count(l => l.Length > 0 && char.IsDigit(l[0]) && l.Contains(",\"Mount\","));
        rows.Should().BeGreaterThan(100);
    }

    /// <summary>Wires a simulator, a guider and an event recorder together.</summary>
    private sealed class Harness
    {
        private readonly object gate = new();
        private readonly List<GuiderEvent> events = [];

        public Harness(SimulatorScenario scenario, Func<GuiderSettings, GuiderSettings>? configure = null)
        {
            Clock = new VirtualClock();
            Sim = new Simulator(scenario, Clock);
            var settings = new GuiderSettings { FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000 };
            Guider = new Guider(Sim.Camera, Sim.Mount, Sim.Mount, Clock, configure?.Invoke(settings) ?? settings, ditherSeed: 5)
            {
                AutoStartLoop = false,
            };
            Guider.EventRaised += (_, e) =>
            {
                lock (gate)
                {
                    events.Add(e);
                }

                OnEvent?.Invoke(e);
            };
        }

        public VirtualClock Clock { get; }

        public Simulator Sim { get; }

        public Guider Guider { get; }

        public Action<GuiderEvent>? OnEvent { get; set; }

        public IReadOnlyList<GuiderEvent> Events
        {
            get
            {
                lock (gate)
                {
                    return events.ToList();
                }
            }
        }

        public IEnumerable<AlertEvent> Alerts(GuideErrorSeverity severity) => Events.OfType<AlertEvent>().Where(a => a.Severity == severity);

        public double Seconds(DateTimeOffset t) => (t - Clock.Epoch).TotalSeconds;

        public async Task RunFor(TimeSpan duration)
        {
            Guider.StartLooping(Clock.CancelAt(Clock.Elapsed + duration));
            var loop = Guider.WaitForLoopAsync();
            var finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromMinutes(5)));
            finished.Should().BeSameAs(loop, "the virtual-time run must finish in reasonable wall time");
            await loop;
        }

        /// <summary>
        /// True RMS (″, population σ per axis, combined) of the pointing error over guide frames after
        /// <paramref name="fromSec"/> of guiding, excluding settling; and the same for the unguided error.
        /// </summary>
        public (double Guided, double Unguided) TrueRms(double fromSec)
        {
            var steps = Events.OfType<GuideStepEvent>().Where(s => !s.IsSettling && !s.IsRecenterMove && s.Time >= fromSec).ToList();
            steps.Should().NotBeEmpty();
            var g = steps.Select(s => Sim.Mount.GetPointingErrorBreakdown(Seconds(s.Timestamp))).ToList();
            double Rms(IEnumerable<double> v)
            {
                var a = v.ToArray();
                double m = a.Average();
                return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / a.Length);
            }

            double guided = Math.Sqrt(Math.Pow(Rms(g.Select(b => b.Total.Ra)), 2) + Math.Pow(Rms(g.Select(b => b.Total.Dec)), 2));
            double unguided = Math.Sqrt(Math.Pow(Rms(g.Select(b => b.Total.Ra - b.Guiding.Ra)), 2) + Math.Pow(Rms(g.Select(b => b.Total.Dec - b.Guiding.Dec)), 2));
            return (guided, unguided);
        }
    }
}
