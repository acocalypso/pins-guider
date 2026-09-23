// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using PinsGuider.Engine.Calibration;
using PinsGuider.Engine.Coach;
using PinsGuider.Engine.Core;
using PinsGuider.Engine.Guiding;

namespace PinsGuider.Engine.Tests.Coach;

/// <summary>Unit tests of the coach measurement math on synthetic data with known truth.</summary>
[TestFixture]
public class CoachAnalysisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);

    private static List<DriftSample> Series(double seconds, double interval, Func<double, (double Ra, double Dec)> truth, double noisePx, int seed = 3,
        Func<int, (double Ra, double Dec)>? spikes = null)
    {
        var rng = new Random(seed);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var list = new List<DriftSample>();
        int i = 0;
        for (double t = 0; t <= seconds; t += interval, i++)
        {
            var (ra, dec) = truth(t);
            var (sr, sd) = spikes?.Invoke(i) ?? (0, 0);
            list.Add(new DriftSample(t, ra + noisePx * Gauss() + sr, dec + noisePx * Gauss() + sd, 50));
        }

        return list;
    }

    [Test]
    public void Seeing_is_the_high_frequency_rms_and_ignores_drift()
    {
        // 0.4 px white seeing per axis on top of steady drifts
        var samples = Series(240, 2.1, t => (0.02 * t, -0.01 * t), 0.4);
        var a = DriftAnalyzer.Analyze(samples, 2.0, pixelScale: 1.5, declinationDeg: 30)!;

        a.SeeingRaPx.Should().BeApproximately(0.4, 0.06);
        a.SeeingDecPx.Should().BeApproximately(0.4, 0.06);
        a.SeeingTotalArcsec.Should().BeApproximately(0.4 * Math.Sqrt(2) * 1.5, 0.12);
        a.RaDriftPxPerSec.Should().BeApproximately(0.02, 0.002);
        a.DecDriftPxPerSec.Should().BeApproximately(-0.01, 0.002);
        a.PeriodSeconds.Should().BeNull("a straight drift has no period");
        a.GustPercent.Should().BeLessThan(2);
    }

    [Test]
    public void Periodic_error_period_amplitude_and_rate_are_recovered()
    {
        // 4 px amplitude, 150 s period over 400 s (2.7 periods) plus drift and seeing
        double w = 2 * Math.PI / 150;
        var samples = Series(400, 2.1, t => (4 * Math.Sin(w * t + 0.3) + 0.01 * t, 0), 0.3);
        var a = DriftAnalyzer.Analyze(samples, 2.0, pixelScale: 2.0, declinationDeg: 0)!;

        a.PeriodSeconds.Should().BeApproximately(150, 5);
        a.PeriodicAmplitudeArcsec.Should().BeApproximately(8, 0.6);
        a.RaMaxRateArcsecPerSec.Should().BeApproximately(2 * (4 * w + 0.01), 0.1 * 2 * 4 * w);
        a.SeeingRaPx.Should().BeApproximately(0.3, 0.06, "the sinusoid does not leak into the seeing");
        a.DriftLimitingExposureSeconds.Should().BeApproximately(a.SeeingRaArcsec / a.RaMaxRateArcsecPerSec, 1e-9);

        // the published model reproduces the samples (relative to the first one) up to the seeing:
        // Ra(T) = offset + drift·T/60 + amplitude·sin(2πT/period + phase)
        double RaModel(double t) => a.PeriodicOffsetArcsec!.Value + a.RaDriftArcsecPerMin * t / 60
            + a.PeriodicAmplitudeArcsec * Math.Sin(2 * Math.PI * t / a.PeriodSeconds!.Value + a.PeriodicPhaseRad!.Value);
        double rms = Math.Sqrt(samples.Average(x => Math.Pow((x.RaPx - samples[0].RaPx) * 2.0 - RaModel(x.T), 2)));
        rms.Should().BeLessThan(1.2 * 0.3 * 2.0, "residuals are the 0.3 px seeing");
        a.RaDriftArcsecPerMin.Should().BeApproximately(0.01 * 60 * 2.0, 0.3);
    }

    [Test]
    public void No_period_is_reported_when_the_run_covers_less_than_1_2_periods()
    {
        double w = 2 * Math.PI / 480;
        var samples = Series(180, 2.1, t => (6 * Math.Sin(w * t), 0), 0.2);
        var a = DriftAnalyzer.Analyze(samples, 2.0, 1.0, 0)!;

        a.PeriodSeconds.Should().BeNull();
        a.PeriodicAmplitudeArcsec.Should().BeGreaterThan(0.5, "peak-to-peak based amplitude is still reported");
        a.RaMaxRateArcsecPerSec.Should().BeApproximately(6 * w, 0.35 * 6 * w);
        a.RaPeakToPeakArcsec.Should().BeGreaterThan(5);
    }

    [Test]
    public void Polar_alignment_error_from_dec_drift_and_assumed_declination()
    {
        // 0.5 px/min at 2″/px = 1″/min; at dec 60°: 3.8197 × 1 / 0.5 = 7.64′
        var samples = Series(180, 2.1, t => (0, 0.5 / 60 * t), 0.2);
        var a = DriftAnalyzer.Analyze(samples, 2.0, 2.0, 60)!;
        a.DecDriftArcsecPerMin.Should().BeApproximately(1.0, 0.1);
        a.PolarAlignmentErrorArcmin.Should().BeApproximately(7.64, 0.8);
        a.DeclinationAssumed.Should().BeFalse();

        var unknown = DriftAnalyzer.Analyze(samples, 2.0, 2.0, null)!;
        unknown.DeclinationAssumed.Should().BeTrue();
        unknown.PolarAlignmentErrorArcmin.Should().BeApproximately(3.82, 0.4, "dec 0 is assumed");
    }

    [Test]
    public void Gusts_are_counted_as_large_frame_to_frame_jumps()
    {
        // single-frame 4 px displacements on 5 of ~115 frames: each gives two jumps (into and out of the spike)
        var samples = Series(240, 2.1, _ => (0, 0), 0.2, spikes: i => i is 20 or 40 or 60 or 80 or 100 ? (4.0, 0) : (0, 0));
        var a = DriftAnalyzer.Analyze(samples, 2.0, 1.0, 0)!;
        a.GustPercent.Should().BeInRange(5, 10);
    }

    [Test]
    public void Min_move_follows_the_seeing_with_phd2_multipliers()
    {
        var samples = Series(240, 2.1, _ => (0, 0), 0.3);
        var a = DriftAnalyzer.Analyze(samples, 2.0, pixelScale: 1.0, declinationDeg: 0)!;
        double expectedDec = Math.Ceiling(a.SeeingDecPx * 1.28 / 0.05 - 1e-9) * 0.05;
        double expectedRa = Math.Max(0.1, Math.Ceiling(a.SeeingRaPx * 1.28 * 0.65 / 0.05 - 1e-9) * 0.05);
        a.MinMoveDecPx.Should().BeApproximately(expectedDec, 1e-9);
        a.MinMoveRaPx.Should().BeApproximately(expectedRa, 1e-9);
        a.MinMoveDecPx.Should().BeInRange(0.35, 0.45);

        // bad data (min-move above 1.25″) falls back to the smart default
        var wild = DriftAnalyzer.Analyze(Series(240, 2.1, _ => (0, 0), 3.0), 2.0, 1.0, 0, smartMinMovePx: 0.25)!;
        wild.MinMoveDecPx.Should().Be(0.25);
    }

    [Test]
    public void Camera_check_jitter_removes_linear_drift_and_selection_prefers_stars_then_short_exposure()
    {
        // positions on a line plus ±0.1 px alternating residuals
        var frames = Enumerable.Range(0, 6).Select(i => new CameraFrameMeasurement(i * 2.0, true, 100 + 0.5 * i + (i % 2 == 0 ? 0.1 : -0.1), 50 - 0.2 * i, 40, 2.5, false))
            .ToList();
        double jitter = CameraCheckAnalyzer.DetrendedJitter(frames);
        jitter.Should().BeLessThan(0.15).And.BeGreaterThan(0.08);

        var r = CameraCheckAnalyzer.Evaluate(new CameraCombination(2, 50), frames, stars: 6, pixelScale: 2.0);
        r.Feasible.Should().BeTrue();
        r.JitterArcsec.Should().BeApproximately(2 * r.JitterPx!.Value, 0.002);
        r.Gain.Should().Be(50);

        CameraCheckAnalyzer.Evaluate(new CameraCombination(2, 50), frames.Select(f => f with { Saturated = true }).ToList(), 6, 2.0).Reason.Should().Be("saturated");
        CameraCheckAnalyzer.Evaluate(new CameraCombination(2, 50), frames.Select(f => f with { Snr = 9 }).ToList(), 6, 2.0).Reason.Should().Be("lowSnr");
        CameraCheckAnalyzer.Evaluate(new CameraCombination(2, 50), frames, 0, 2.0).Reason.Should().Be("noStar");

        CoachCameraResult R(double exp, int gain, double jit, int stars, bool feasible = true) =>
            new() { ExposureSeconds = exp, Gain = gain, JitterArcsec = jit, Stars = stars, Feasible = feasible };
        var results = new[]
        {
            R(1, 0, 0.30, 4),
            R(2, 0, 0.21, 5),
            R(3, 0, 0.20, 5),
            R(2, 50, 0.215, 9),
            R(3, 100, 0.10, 12, feasible: false),
        };
        CameraCheckAnalyzer.SelectRecommended(results).Should().BeSameAs(results[3], "within 10 % of the best jitter more stars win");
        var tie = new[] { R(3, 0, 0.20, 5), R(2, 0, 0.21, 5) };
        CameraCheckAnalyzer.SelectRecommended(tie).Should().BeSameAs(tie[1], "equal stars: the shorter exposure");
        CameraCheckAnalyzer.SelectRecommended([R(1, 0, 0.3, 1, false)]).Should().BeNull();
    }

    [Test]
    public void Pulse_response_analysis_finds_min_pulse_asymmetry_and_rate()
    {
        CoachPulse P(string dir, int ms, double ratio) => new() { Direction = dir, DurationMs = ms, Ratio = ratio };
        var pulses = new List<CoachPulse>
        {
            P("West", 100, 0.05), P("East", 100, 0.0), P("West", 250, 0.3), P("East", 250, 0.2),
            P("West", 500, 0.7), P("East", 500, 0.55), P("West", 1000, 1.1), P("East", 1000, 0.7),
            P("North", 100, 0.9), P("South", 100, 1.1), P("North", 1000, 1.0), P("South", 1000, 1.0),
        };
        MountResponseAnalyzer.MinEffectivePulse(pulses, ra: true).Should().Be(500);
        MountResponseAnalyzer.MinEffectivePulse(pulses, ra: false).Should().Be(100);
        MountResponseAnalyzer.Asymmetry(pulses, "West", "East", 500)!.Value.Should().BeApproximately(0.9 / 0.625, 1e-9);
        MountResponseAnalyzer.RateRatio(pulses, ra: false, 500)!.Value.Should().BeApproximately(1.0, 1e-9);
        MountResponseAnalyzer.MinEffectivePulse([P("East", 1000, 0.2)], true).Should().BeNull();
    }

    [Test]
    public void Backlash_procedure_measures_the_dead_band_of_a_kinematic_mount_without_bias()
    {
        foreach (double backlashMs in new[] { 0.0, 450.0, 1500.0, 3200.0 })
        {
            var mount = new KinematicDecMount { BacklashMs = backlashMs, NoisePx = 0.05 };
            var result = Run(mount, new MountResponseProcedure(0, 0, lostTimeoutSec: 30));
            var r = result.Response;
            TestContext.Out.WriteLine($"true {backlashMs} ms -> {r.BacklashMs} ms ({r.BacklashState}), points {r.BacklashPoints.Count}");
            if (backlashMs == 0)
            {
                r.BacklashState.Should().Be(CoachBacklashStates.None);
                r.BacklashMs!.Value.Should().BeLessThan(50);
            }
            else
            {
                r.BacklashState.Should().Be(CoachBacklashStates.Measured);
                r.BacklashMs!.Value.Should().BeApproximately(backlashMs, 40, "the method has no systematic bias");
                double arcsec = backlashMs * mount.DecRate * mount.PixelScale;
                r.BacklashArcsec!.Value.Should().BeApproximately(arcsec, 0.05 * arcsec + 0.1);
            }

            r.BacklashPoints.Should().NotBeEmpty();
            r.MinEffectivePulseRaMs.Should().Be(100);
            r.MinEffectivePulseDecMs.Should().Be(100, "Dec response pulses are measured with the gear engaged");
            r.RateRatioRa!.Value.Should().BeApproximately(1.0, 0.05);
            r.RateRatioDec!.Value.Should().BeApproximately(1.0, 0.05);
            r.AsymmetryDec!.Value.Should().BeApproximately(1.0, 0.1);
            Math.Abs(mount.X).Should().BeLessThan(1.5, "the star is recentred");
            Math.Abs(mount.Y).Should().BeLessThan(1.5);
        }
    }

    [Test]
    public void Response_procedure_detects_stiction_asymmetry_and_corrects_dec_drift()
    {
        var mount = new KinematicDecMount { BacklashMs = 800, RaStictionMs = 200, EastEfficiency = 0.5, SouthEfficiency = 0.6, DecDriftPxPerSec = 0.03, NoisePx = 0.03 };
        var result = Run(mount, new MountResponseProcedure(0, mount.DecDriftPxPerSec, lostTimeoutSec: 30));
        var r = result.Response;
        r.MinEffectivePulseRaMs.Should().Be(1000, "200 ms stiction plus a half-speed East leaves 500 ms pulses below 50 %");
        r.AsymmetryRa!.Value.Should().BeApproximately(0.8 / 0.4, 0.2);
        // the dead band (800 ms at the North rate) takes 800 / 0.6 ms of the slower South pulses; the angle is the same
        r.BacklashMs!.Value.Should().BeApproximately(800 / 0.6, 70, "the known Dec drift is removed and the slower South rate is measured");
        r.BacklashArcsec!.Value.Should().BeApproximately(800 * mount.DecRate * mount.PixelScale, 0.4);
        r.AsymmetryDec!.Value.Should().BeApproximately(1 / 0.6, 0.15);
        result.StarLost.Should().BeFalse();
    }

    [Test]
    public void Report_grade_budget_and_ranking()
    {
        CoachReportBuilder.Grade(0.4, 1.0).Grade.Should().Be(CoachGrades.Excellent);
        CoachReportBuilder.Grade(0.6, 1.0).Grade.Should().Be(CoachGrades.Good);
        CoachReportBuilder.Grade(0.9, 1.0).Grade.Should().Be(CoachGrades.Fair);
        CoachReportBuilder.Grade(1.2, 1.0).Grade.Should().Be(CoachGrades.Poor);
        CoachReportBuilder.Grade(0.9, 2.0).Should().Be((CoachGrades.Excellent, 0.45));
        CoachReportBuilder.Grade(0.9, null).Should().Be((CoachGrades.Fair, 0.9), "no imaging scale: graded by arcsec");
        CoachReportBuilder.Grade(null, 1.0).Grade.Should().Be(CoachGrades.Unknown);

        var ts = T0.UtcDateTime;
        var findings = new List<CoachFinding>
        {
            CoachFindings.Create(CoachCodes.DriftPolarAlignment, CoachStepNames.Drift, CoachSeverities.Warning, ts, new() { ["arcmin"] = 7.0 }, 0.05),
            CoachFindings.Create(CoachCodes.ResponseDecBacklash, CoachStepNames.MountResponse, CoachSeverities.Warning, ts, new() { ["ms"] = 1500.0 }, 0.4),
            CoachFindings.Create(CoachCodes.ResponseRateMismatch, CoachStepNames.MountResponse, CoachSeverities.Warning, ts,
                new() { ["axis"] = "Dec", ["ratio"] = 0.6 }, qualifier: "Dec"),
            CoachFindings.Create(CoachCodes.DriftSeeing, CoachStepNames.Drift, CoachSeverities.Good, ts, new() { ["rmsArcsec"] = 0.5 }),
        };
        var (report, reportFindings) = CoachReportBuilder.Build(new CoachReportInput
        {
            Id = "r1",
            Timestamp = T0,
            PixelScale = 1.5,
            ImagingScale = 1.2,
            Findings = findings,
            Drift = new CoachDrift { SeeingTotalArcsec = 0.5 },
            Camera = new CoachCameraCheck { Recommended = new CoachCameraResult { Hfd = 2.355, Snr = 20 } },
            Trials =
            [
                new CoachTrial { Id = "A", Kind = CoachTrialKinds.Current, State = CoachTrialStates.Done, RmsTotalArcsec = 1.3, RmsRaArcsec = 1.0, RmsDecArcsec = 0.83 },
                new CoachTrial { Id = "B", Kind = CoachTrialKinds.Suggestion, State = CoachTrialStates.Done, RmsTotalArcsec = 1.1, RmsRaArcsec = 0.8, RmsDecArcsec = 0.75 },
            ],
        });

        report.GuidedSource.Should().Be(CoachGuidedSources.Trials);
        report.GuidedRmsArcsec.Should().Be(1.1);
        report.CentroidNoiseArcsec!.Value.Should().BeApproximately(Math.Sqrt(2) / 20 * 1.5, 1e-3);
        double mount = Math.Sqrt(1.1 * 1.1 - 0.25 - Math.Pow(Math.Sqrt(2) / 20 * 1.5, 2));
        report.MountArcsec!.Value.Should().BeApproximately(mount, 1e-3);
        report.Grade.Should().Be(CoachGrades.Fair, "1.1″ / 1.2″ per px = 0.92");
        report.GradeRatio!.Value.Should().BeApproximately(1.1 / 1.2, 1e-3);
        reportFindings.Should().ContainSingle(f => f.Code == CoachCodes.ReportMountLimited);
        report.Night.Should().NotBeNullOrEmpty();

        // rate mismatch impact estimated from the Dec RMS: |1 − 0.6| × 0.75 = 0.3
        report.Findings.Single(f => f.Code == CoachCodes.ResponseRateMismatch).ImpactArcsec.Should().BeApproximately(0.3, 1e-3);
        report.Actions.Should().Equal("response.decBacklash", "response.rateMismatch:Dec", "report.mountLimited", "drift.polarAlignment");
    }

    private static MountResponseResult Run(KinematicDecMount mount, MountResponseProcedure proc)
    {
        var transform = new MountTransform(0, Math.PI / 2, mount.RaRate, mount.DecRate);
        double t = 0;
        IReadOnlyList<PulseCommand> pulses = [];
        for (int i = 0; i < 1000 && !proc.Completion.IsCompleted; i++)
        {
            foreach (var p in pulses)
            {
                mount.Apply(p);
                t += p.DurationMs / 1000.0;
            }

            t += 2.0;
            mount.DriftTo(t);
            var (x, y) = mount.Observe();
            var frame = new CoachFrame
            {
                Time = T0.AddSeconds(t),
                StarFound = true,
                StarPosition = new GuidePoint(500 + x, 400 + y),
                MountOffset = new GuidePoint(x, y),
                MountPosition = new GuidePoint(x, y),
                Snr = 50,
                Transform = transform,
                FrameWidth = 1000,
                FrameHeight = 800,
                SearchRegion = 15,
                PixelScale = mount.PixelScale,
                ExposureMs = 2000,
                MaxRaDurationMs = 2500,
                MaxDecDurationMs = 2500,
            };
            pulses = proc.OnFrame(frame);
        }

        proc.Completion.IsCompleted.Should().BeTrue("the procedure terminates");
        return proc.Completion.Result;
    }

    /// <summary>Kinematic mount in mount-axis px: Dec backlash (dead band in pulse ms), RA stiction and East efficiency, Dec drift.</summary>
    private sealed class KinematicDecMount
    {
        private readonly Random rng = new(11);
        private double play; // motor position inside the dead band, 0..BacklashMs
        private double lastT;

        public double RaRate { get; } = 0.005;

        public double DecRate { get; } = 0.005;

        public double PixelScale { get; } = 1.5;

        public double BacklashMs { get; init; }

        public double RaStictionMs { get; init; }

        public double EastEfficiency { get; init; } = 1.0;

        public double SouthEfficiency { get; init; } = 1.0;

        public double DecDriftPxPerSec { get; init; }

        public double NoisePx { get; init; }

        public double X { get; private set; }

        public double Y { get; private set; }

        public void Apply(PulseCommand p)
        {
            switch (p.Direction)
            {
                case GuideDirection.East:
                    X += RaRate * EastEfficiency * Math.Max(0, p.DurationMs - RaStictionMs);
                    break;
                case GuideDirection.West:
                    X -= RaRate * Math.Max(0, p.DurationMs - RaStictionMs);
                    break;
                case GuideDirection.North:
                {
                    double take = Math.Min(p.DurationMs, BacklashMs - play);
                    play += take;
                    Y += DecRate * (p.DurationMs - take);
                    break;
                }

                default:
                {
                    // the motor turns slower South: the dead band takes longer to cross, the move is slower
                    double motorMs = p.DurationMs * SouthEfficiency;
                    double take = Math.Min(motorMs, play);
                    play -= take;
                    Y -= DecRate * (motorMs - take);
                    break;
                }
            }
        }

        public void DriftTo(double t)
        {
            Y += DecDriftPxPerSec * (t - lastT);
            lastT = t;
        }

        public (double X, double Y) Observe() => (X + NoisePx * Gauss(), Y + NoisePx * Gauss());

        private double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
    }
}
