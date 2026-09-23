// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using PinsGuider.Engine.Coach;
using PinsGuider.Engine.Guiding;
using PinsGuider.Engine.Simulation;

namespace PinsGuider.Engine.Tests.Coach;

/// <summary>
/// Guiding Coach sessions against the closed-loop simulator in virtual time: measurements are checked against the
/// simulator truth, and every exit path must leave the guider as it was (settings, guiding output, guiding state).
/// </summary>
[TestFixture]
[NonParallelizable]
public class CoachClosedLoopTests
{
    private static readonly SettleParams Settle = new(1.5, 10, 120);

    /// <summary>A mount with only the effects under test and modest seeing.</summary>
    private static SimulatorScenario Quiet(MountSimConfig? mount = null, double seeing = 0.4) => SimulatorScenario.GoodMount with
    {
        Name = "Quiet",
        Mount = mount ?? new MountSimConfig { RandomWalkArcsecPerSqrtSec = 0.01 },
        Sky = SimulatorScenario.GoodMount.Sky with { SeeingJitterArcsec = seeing },
    };

    [Test]
    public async Task Drift_recovers_seeing_periodic_error_and_polar_alignment()
    {
        var mount = new MountSimConfig
        {
            PeriodicError = [new PeriodicErrorTerm(8.0, 120.0)],
            DecDriftArcsecPerMin = 2.0,
            RandomWalkArcsecPerSqrtSec = 0.01,
        };
        var h = new Harness(Quiet(mount, seeing: 0.4));
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift], DriftSeconds = 300 });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        var d = status.Drift!;
        double cosDec = Math.Cos(20 * Math.PI / 180);
        TestContext.Out.WriteLine($"period {d.PeriodicErrorPeriodSeconds} s, amplitude {d.PeriodicErrorAmplitudeArcsec}″ (truth {8 * cosDec:F2}), " +
            $"PAE {d.PolarAlignmentErrorArcmin}′ (truth {3.8197 * 2 / cosDec:F2}), seeing {d.SeeingTotalArcsec}″, max rate {d.RaMaxRateArcsecPerSec}");
        d.PeriodicErrorPeriodSeconds!.Value.Should().BeApproximately(120, 12);
        d.PeriodicErrorAmplitudeArcsec!.Value.Should().BeApproximately(8 * cosDec, 0.15 * 8 * cosDec, "PE is measured on the sky");
        d.RaMaxRateArcsecPerSec!.Value.Should().BeApproximately(8 * cosDec * 2 * Math.PI / 120, 0.2 * 8 * cosDec * 2 * Math.PI / 120);
        d.PolarAlignmentErrorArcmin!.Value.Should().BeApproximately(3.8197 * 2 / cosDec, 0.15 * 3.8197 * 2 / cosDec);
        d.DeclinationAssumed.Should().BeFalse();

        // seeing: 0.4″ per axis for 1 s, averaging as 1/√T over the 2 s exposure, both axes
        double seeingTruth = Math.Sqrt(2) * 0.4 / Math.Sqrt(2.0);
        d.SeeingTotalArcsec!.Value.Should().BeApproximately(seeingTruth, 0.3 * seeingTruth);
        d.Samples.Should().HaveCountGreaterThan(100);

        // the PE model of the contract reproduces the published samples
        double Model(double t) => d.PeriodicErrorOffsetArcsec!.Value + d.RaDriftArcsecPerMin!.Value * t / 60
            + d.PeriodicErrorAmplitudeArcsec!.Value * Math.Sin(2 * Math.PI * t / d.PeriodicErrorPeriodSeconds!.Value + d.PeriodicErrorPhaseRad!.Value);
        double residual = Math.Sqrt(d.Samples.Average(x => Math.Pow(x.Ra - Model(x.T), 2)));
        TestContext.Out.WriteLine($"model residual {residual:F3}″ (RA seeing {d.SeeingRaArcsec}″)");
        residual.Should().BeLessThan(1.5 * d.SeeingRaArcsec!.Value);

        status.Findings.Should().Contain(f => f.Code == CoachCodes.DriftPolarAlignment && f.Severity == CoachSeverities.Warning);
        status.Findings.Should().Contain(f => f.Code == CoachCodes.DriftPeriodicError && (double?)f.Parameters["periodSeconds"] != null);
        status.Findings.Single(f => f.Code == CoachCodes.DriftMinMove).Changes.Select(c => c.Name)
            .Should().Equal(CoachSettingNames.RaMinMove, CoachSettingNames.DecMinMove);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Mount_response_measures_backlash_stiction_and_asymmetric_rates()
    {
        var mount = new MountSimConfig
        {
            DecBacklashArcsec = 12.0,
            RaStictionMs = 200,
            DecSouthEfficiency = 0.7,
            DecDriftArcsecPerMin = 0.3,
            RandomWalkArcsecPerSqrtSec = 0.01,
        };
        var h = new Harness(Quiet(mount, seeing: 0.3));
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift, CoachStepNames.MountResponse], DriftSeconds = 120 });

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        var r = status.Response!;
        // the dead band takes 1/0.7 longer with the slower South motor; its angle is the configured 12″
        double truthMs = mount.DecBacklashMs / 0.7;
        TestContext.Out.WriteLine($"backlash {r.BacklashMs} ms / {r.BacklashArcsec}″ ({r.BacklashState}), truth {truthMs:F0} ms / 12″; " +
            $"min pulse RA {r.MinEffectivePulseRaMs} Dec {r.MinEffectivePulseDecMs}; asym RA {r.AsymmetryRa} Dec {r.AsymmetryDec}; rate RA {r.RateRatioRa} Dec {r.RateRatioDec}");
        foreach (var p in r.Pulses)
        {
            TestContext.Out.WriteLine($"  {p.Direction} {p.DurationMs} ms: {p.MovedArcsec}″ / {p.ExpectedArcsec}″ = {p.Ratio}");
        }

        r.BacklashState.Should().Be(CoachBacklashStates.Measured);
        r.BacklashMs!.Value.Should().BeApproximately(truthMs, 0.12 * truthMs);
        r.BacklashArcsec!.Value.Should().BeApproximately(12.0, 1.5);
        r.BacklashPoints.Should().HaveCountGreaterThan(4);
        r.MinEffectivePulseRaMs.Should().Be(500, "200 ms of stiction: 250 ms pulses move less than half");
        r.MinEffectivePulseDecMs.Should().Be(100);
        r.AsymmetryDec!.Value.Should().BeApproximately(1 / 0.7, 0.2);
        r.AsymmetryRa!.Value.Should().BeApproximately(1.0, 0.15);
        r.RateRatioRa!.Value.Should().BeGreaterThan(1.1, "stiction made the calibration underestimate the RA rate");

        status.Findings.Should().Contain(f => f.Id == "response.minPulse:Ra" && f.Changes.Any(c => c.Name == CoachSettingNames.RaMinMove));
        status.Findings.Should().Contain(f => f.Id == "response.asymmetry:Dec");
        var blc = status.Findings.Single(f => f.Code == CoachCodes.ResponseDecBacklash);
        blc.Changes.Should().Contain(c => c.Name == CoachSettingNames.BacklashCompensation && c.Value == "true" && c.CurrentValue == "false");
        blc.Changes.Should().Contain(c => c.Name == CoachSettingNames.BacklashPulseMs);
        h.Events.OfType<AlertEvent>().Should().NotContain(a => a.Code == GuideErrorCode.RunawayDetected || a.Code == GuideErrorCode.MountNotResponding,
            "measurements never trip the safety monitors");
        h.Events.OfType<GuideStepEvent>().Where(e => e.CoachMeasurement).Should().OnlyContain(e => e.RaDuration == 0 && e.DecDuration == 0);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Full_session_from_guiding_recommends_proves_and_restores()
    {
        var scenario = SimulatorScenario.GoodMount with
        {
            Mount = SimulatorScenario.GoodMount.Mount with { DecBacklashArcsec = 8.0, DecDriftArcsecPerMin = 1.0 },
        };
        var h = new Harness(scenario);
        var options = new CoachOptions
        {
            ExposureSeconds = [1, 2],
            Gains = [0, 60],
            FramesPerCombination = 3,
            DriftSeconds = 120,
            TrialSeconds = 60,
        };
        var coachEvents = new List<CoachStatus>();
        h.OnEvent = e =>
        {
            if (e is CoachStatusEvent c)
            {
                lock (coachEvents)
                {
                    coachEvents.Add(c.Status);
                }
            }
        };
        var status = await h.RunFromGuiding(options, TimeSpan.FromMinutes(90));

        status.Phase.Should().Be(CoachPhases.Complete, status.Message);
        status.Steps.Should().OnlyContain(s => s.State == CoachStepStates.Done);
        status.Camera!.Results.Should().HaveCountGreaterThanOrEqualTo(4);
        status.Camera.Recommended.Should().NotBeNull();
        status.Drift!.SeeingTotalArcsec.Should().BeGreaterThan(0);
        status.Response!.BacklashState.Should().Be(CoachBacklashStates.Measured);
        status.Response.BacklashMs!.Value.Should().BeApproximately(scenario.Mount.DecBacklashMs, 0.15 * scenario.Mount.DecBacklashMs);
        status.Trials.Select(t => t.Id).Should().StartWith(CoachTrialIds.A).And.EndWith(CoachTrialIds.A2);
        status.Trials.Should().Contain(t => t.Id == CoachTrialIds.B && t.Settings.Count > 0);
        status.Trials.Where(t => t.State == CoachTrialStates.Done).Should().HaveCountGreaterThanOrEqualTo(3);
        status.Trials.Should().ContainSingle(t => t.IsWinner);
        status.Findings.Should().Contain(f => f.Code == CoachCodes.TrialsWinner || f.Code == CoachCodes.TrialsNoImprovement);
        foreach (var t in status.Trials)
        {
            TestContext.Out.WriteLine($"trial {t.Id} {t.State}: {t.RmsTotalArcsec}″ osc {t.OscillationIndex} frames {t.Frames} " +
                string.Join(", ", t.Settings.Select(c => $"{c.Name}={c.Value} (was {c.CurrentValue})")));
        }

        var report = status.Report!;
        report.GuidedSource.Should().Be(CoachGuidedSources.Trials);
        report.Grade.Should().NotBe(CoachGrades.Unknown);
        report.Steps.Should().Equal(CoachStepNames.All);
        report.ProfileName.Should().Be("Test");
        report.Actions.Should().OnlyContain(id => report.Findings.Any(f => f.Id == id && (f.Severity == CoachSeverities.Warning || f.Severity == CoachSeverities.Problem)));
        TestContext.Out.WriteLine($"grade {report.Grade} ({report.GradeRatio}), guided {report.GuidedRmsArcsec}″ = seeing {report.SeeingArcsec} + noise {report.CentroidNoiseArcsec} + mount {report.MountArcsec}");
        foreach (var f in report.Findings)
        {
            TestContext.Out.WriteLine($"  [{f.Severity}] {f.Id} impact {f.ImpactArcsec}: {f.Message}");
        }

        coachEvents.Should().Contain(s => s.Step == CoachStepNames.Drift && s.Drift != null && s.Drift.Samples.Count > 0, "live drift samples are published");
        coachEvents.Select(s => s.Progress).Should().BeInAscendingOrder();
        h.Coach.GetHistory(10).Should().ContainSingle(r => r.Id == report.Id).Which.Drift!.Samples.Should().BeEmpty("the history has no raw samples");
        h.AssertRestoredAndGuiding();

        // apply the suggestion: host settings change and the finding/trial is marked applied
        h.Coach.ApplyActions(["trial:B"], out var error).Should().BeTrue(error);
        var b = status.Trials.Single(t => t.Id == CoachTrialIds.B);
        foreach (var c in b.Settings)
        {
            CoachSettingsMap.GetValue(h.Guider.BaseSettings, c.Name).Should().Be(c.Value);
        }

        h.Coach.Status.Trials.Single(t => t.Id == CoachTrialIds.B).Applied.Should().BeTrue();
        h.Coach.GetHistory(1).Single().Trials.Single(t => t.Id == CoachTrialIds.B).Applied.Should().BeTrue();
        h.Coach.ApplyActions(["nope"], out _).Should().BeFalse();
    }

    [Test]
    public async Task Cancel_restores_settings_output_and_guiding()
    {
        // 0.5 s exposures: the camera check recommends a longer one, applied temporarily for the drift step
        var h = new Harness(SimulatorScenario.GoodMount, s => s with { ExposureMs = 500 });
        bool overlaySeen = false;
        h.OnEvent = e =>
        {
            if (e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 30 } })
            {
                overlaySeen |= h.Guider.Settings.ExposureMs != 500;
                h.Coach.Cancel();
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions
        {
            Steps = [CoachStepNames.CameraCheck, CoachStepNames.Drift],
            ExposureSeconds = [1, 2],
            Gains = [0],
            FramesPerCombination = 3,
        });

        status.Phase.Should().Be(CoachPhases.Cancelled);
        status.MessageCode.Should().BeNull();
        status.Steps.Single(s => s.Name == CoachStepNames.CameraCheck).State.Should().Be(CoachStepStates.Done);
        overlaySeen.Should().BeTrue("the recommended exposure was used during the drift step");
        h.AfterSession!.Settings.ExposureMs.Should().Be(500);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task External_start_guiding_interrupts_the_session()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        Task<SettleResult>? external = null;
        h.OnEvent = e =>
        {
            if (external is null && e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 20 } })
            {
                external = h.Guider.StartGuidingAsync(Settle);
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift, CoachStepNames.Trials] });

        status.Phase.Should().Be(CoachPhases.Cancelled);
        status.MessageCode.Should().Be(CoachCodes.Interrupted);
        status.MessageParameters["reason"].Should().Be(CoachInterruptReasons.Guiding);
        status.Findings.Should().ContainSingle(f => f.Code == CoachCodes.Interrupted).Which.Parameters["reason"].Should().Be(CoachInterruptReasons.Guiding);
        external.Should().NotBeNull();
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task External_dither_and_stop_interrupt_with_their_reason()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        bool dithered = false;
        h.OnEvent = e =>
        {
            if (!dithered && e is CoachStatusEvent { Status: { Step: CoachStepNames.Drift, Drift.ElapsedSeconds: > 20 } })
            {
                dithered = true;
                _ = h.Guider.DitherAsync(3, false, Settle);
            }
        };
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift] });
        status.MessageCode.Should().Be(CoachCodes.Interrupted);
        status.Findings.Single(f => f.Code == CoachCodes.Interrupted).Parameters["reason"].Should().Be(CoachInterruptReasons.Dither);
        h.Events.OfType<GuidingDitheredEvent>().Should().ContainSingle("the dither runs once the measurement has been removed");
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Star_lost_fails_the_step_and_guiding_resumes()
    {
        // clouds while the drift step runs; the step gives up after 30 s without the star
        var scenario = SimulatorScenario.GoodMount with
        {
            Sky = SimulatorScenario.GoodMount.Sky with { Transparency = [new TransparencyWindow(700, 790, 0.0, 2.0)] },
        };
        var h = new Harness(scenario);
        var status = await h.RunFromGuiding(new CoachOptions { Steps = [CoachStepNames.Drift], DriftSeconds = 900 });

        status.Phase.Should().Be(CoachPhases.Failed, "the only step failed");
        status.MessageCode.Should().Be(CoachCodes.StarLost);
        status.Steps.Single().State.Should().Be(CoachStepStates.Failed);
        status.Steps.Single().MessageCode.Should().Be(CoachCodes.StarLost);
        status.Drift!.Samples.Should().NotBeEmpty("partial results stay visible");
        status.Findings.Should().ContainSingle(f => f.Id == "coach.starLost:Drift" && f.Severity == CoachSeverities.Problem);
        h.AssertRestoredAndGuiding();
    }

    [Test]
    public async Task Calibration_failure_fails_the_guided_steps_and_restores()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        h.Sim.Mount.NotResponding = true;
        var status = await h.RunFromLooping(new CoachOptions
        {
            Steps = [CoachStepNames.CameraCheck, CoachStepNames.Drift, CoachStepNames.MountResponse],
            ExposureSeconds = [1],
            Gains = [0],
            FramesPerCombination = 3,
        });

        status.Phase.Should().Be(CoachPhases.Complete, "the camera check succeeded");
        status.Steps.Single(s => s.Name == CoachStepNames.CameraCheck).State.Should().Be(CoachStepStates.Done);
        status.Steps.Single(s => s.Name == CoachStepNames.Drift).MessageCode.Should().Be(CoachCodes.CalibrationFailed);
        status.Steps.Single(s => s.Name == CoachStepNames.MountResponse).MessageCode.Should().Be(CoachCodes.CalibrationFailed);
        h.Events.OfType<CalibrationFailedEvent>().Should().ContainSingle("a failed calibration is not repeated for every step");
        status.Findings.Should().Contain(f => f.Id == "coach.calibrationFailed:Drift");
        status.Report.Should().NotBeNull();
        h.AssertRestoredAndGuiding(expectGuiding: false);
        h.AfterSession!.State.Should().BeOneOf(GuiderState.Selected, GuiderState.Looping);
    }

    [Test]
    public async Task No_calibration_allowed_fails_with_noCalibration_and_rejections_carry_codes()
    {
        var h = new Harness(SimulatorScenario.GoodMount);
        var status = await h.RunFromLooping(new CoachOptions { Steps = [CoachStepNames.Drift], AllowCalibration = false });
        status.Phase.Should().Be(CoachPhases.Failed);
        status.MessageCode.Should().Be(CoachCodes.NoCalibration);
        h.Events.OfType<StartCalibrationEvent>().Should().BeEmpty();

        h.Sim.Camera.IsConnected = false;
        var rejected = h.Coach.Start();
        rejected.Accepted.Should().BeFalse();
        rejected.MessageCode.Should().Be(CoachCodes.NotConnected);
        rejected.Status.SessionId.Should().Be(status.SessionId, "a rejection leaves the last session's status untouched");
        h.Coach.Status.MessageCode.Should().Be(CoachCodes.NoCalibration);
        h.Coach.Status.GainMax.Should().Be(100, "the gain range is always reported");
    }

    /// <summary>Wires simulator, guider, coach and an event recorder; runs sessions in virtual time.</summary>
    private sealed class Harness
    {
        private readonly object gate = new();
        private readonly List<GuiderEvent> events = [];
        private readonly TaskCompletionSource<CoachStatus> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly GuiderSettings initialSettings;
        private CancellationToken deadline;
        private int pulsedStepsAfter;
        private int framesAfter;
        private DateTimeOffset? finishedAt;

        public Harness(SimulatorScenario scenario, Func<GuiderSettings, GuiderSettings>? configure = null)
        {
            Clock = new VirtualClock();
            Sim = new Simulator(scenario, Clock);
            var settings = new GuiderSettings { FocalLengthMm = scenario.Camera.FocalLengthMm, ExposureMs = 2000 };
            initialSettings = configure?.Invoke(settings) ?? settings;
            Guider = new Guider(Sim.Camera, Sim.Mount, Sim.Mount, Clock, initialSettings, ditherSeed: 5) { AutoStartLoop = false };
            Host = new SimpleCoachHost(Guider) { ImagingScale = 1.0, ProfileName = "Test" };
            Coach = new GuidingCoach(Guider, Host);
            Guider.EventRaised += (_, e) =>
            {
                lock (gate)
                {
                    events.Add(e);
                }

                if (e is CoachStatusEvent { Status.Phase: not (CoachPhases.Running or CoachPhases.Idle) } done && Coach.Status.SessionId == done.Status.SessionId)
                {
                    finishedAt ??= e.Timestamp;
                    finished.TrySetResult(done.Status);
                }

                if (finishedAt is { } at && e is GuideStepEvent { CoachMeasurement: false } s && s.Timestamp > at && s.RaDuration + s.DecDuration > 0)
                {
                    Interlocked.Increment(ref pulsedStepsAfter);
                }

                if (finishedAt is { } at2 && e is FrameReadyEvent && e.Timestamp > at2)
                {
                    Interlocked.Increment(ref framesAfter);
                }

                OnEvent?.Invoke(e);
            };
        }

        public VirtualClock Clock { get; }

        public Simulator Sim { get; }

        public Guider Guider { get; }

        public SimpleCoachHost Host { get; }

        public GuidingCoach Coach { get; }

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

        public bool WasGuidingBefore { get; private set; }

        /// <summary>Starts guiding, runs the coach once settled, waits for the session and a few guided frames afterwards.</summary>
        public async Task<CoachStatus> RunFromGuiding(CoachOptions options, TimeSpan? virtualBudget = null, Action<Harness>? atStart = null)
        {
            WasGuidingBefore = true;
            bool started = false;
            var previous = OnEvent;
            OnEvent = e =>
            {
                previous?.Invoke(e);
                if (!started && e is SettleDoneEvent)
                {
                    started = true;
                    atStart?.Invoke(this);
                    Coach.Start(options).Accepted.Should().BeTrue();
                }
            };

            _ = Guider.StartGuidingAsync(Settle);
            return await Run(virtualBudget ?? TimeSpan.FromMinutes(60), expectGuiding: true);
        }

        /// <summary>Starts looping with a selected star (no guiding), then runs the coach.</summary>
        public async Task<CoachStatus> RunFromLooping(CoachOptions options, TimeSpan? virtualBudget = null)
        {
            bool started = false;
            var previous = OnEvent;
            OnEvent = e =>
            {
                previous?.Invoke(e);
                if (!started && e is StarSelectedEvent)
                {
                    started = true;
                    Coach.Start(options).Accepted.Should().BeTrue();
                }
            };

            Guider.AutoSelectStar();
            return await Run(virtualBudget ?? TimeSpan.FromMinutes(60), expectGuiding: false);
        }

        private async Task<CoachStatus> Run(TimeSpan budget, bool expectGuiding)
        {
            deadline = Clock.CancelAt(Clock.Elapsed + budget);
            Coach.LoopToken = deadline;
            Guider.StartLooping(deadline);
            var done = await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromMinutes(4)));
            done.Should().BeSameAs(finished.Task, "the session must finish in reasonable wall time");
            var status = await finished.Task;
            (await Task.WhenAny(Coach.Completion, Task.Delay(TimeSpan.FromMinutes(1)))).Should().BeSameAs(Coach.Completion);

            // let the guider run a little after the session (guiding resumed?), then stop
            var until = DateTime.UtcNow.AddSeconds(60);
            while (Guider.IsLoopRunning && DateTime.UtcNow < until &&
                   (expectGuiding ? Volatile.Read(ref pulsedStepsAfter) < 5 : Volatile.Read(ref framesAfter) < 10))
            {
                await Task.Delay(5);
            }

            AfterSession = new SessionEnd(Guider.State, Guider.HasCoachOverlay, Guider.HasCoachHook, Guider.GuidingOutputEnabled, Guider.Settings,
                Volatile.Read(ref pulsedStepsAfter));
            await Guider.StopCaptureAsync();
            return status;
        }

        public SessionEnd? AfterSession { get; private set; }

        public double Seconds(DateTimeOffset t) => (t - Clock.Epoch).TotalSeconds;

        /// <summary>Temporary settings removed, guiding output on, host settings unchanged and guiding resumed.</summary>
        public void AssertRestoredAndGuiding(bool expectGuiding = true)
        {
            var a = AfterSession!;
            a.Overlay.Should().BeFalse("temporary settings are removed");
            a.Hook.Should().BeFalse("no measurement hook remains");
            a.OutputEnabled.Should().BeTrue("guiding output is re-enabled");
            a.Settings.Should().Be(Guider.BaseSettings, "the effective settings are the host settings again");
            if (expectGuiding)
            {
                a.State.Should().BeOneOf(GuiderState.Guiding, GuiderState.LostLock, GuiderState.Reacquiring);
                a.PulsedStepsAfter.Should().BeGreaterThanOrEqualTo(5, "guiding with corrections continues after the session");
            }
        }
    }

    private sealed record SessionEnd(GuiderState State, bool Overlay, bool Hook, bool OutputEnabled, GuiderSettings Settings, int PulsedStepsAfter);
}
