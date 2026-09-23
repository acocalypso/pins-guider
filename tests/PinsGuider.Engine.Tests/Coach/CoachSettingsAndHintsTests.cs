// SPDX-License-Identifier: MPL-2.0

using FluentAssertions;
using NUnit.Framework;
using PinsGuider.Engine.Algorithms;
using PinsGuider.Engine.Coach;
using PinsGuider.Engine.Core;
using PinsGuider.Engine.Guiding;
using PinsGuider.Engine.Stats;

namespace PinsGuider.Engine.Tests.Coach;

[TestFixture]
public class CoachSettingsAndHintsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 22, 0, 0, TimeSpan.Zero);

    [Test]
    public void Settings_map_reads_plugin_values_with_plugin_conventions()
    {
        var s = new GuiderSettings
        {
            ExposureMs = 2500,
            Gain = null,
            RaAlgorithm = new AlgorithmSettings(GuideAlgorithmKind.Hysteresis, new Dictionary<string, double> { ["aggression"] = 0.65 }),
            Backlash = new BacklashSettings { Enabled = true, PulseMs = 450 },
        };

        CoachSettingsMap.GetValue(s, CoachSettingNames.ExposureSeconds).Should().Be("2.5");
        CoachSettingsMap.GetValue(s, CoachSettingNames.Gain).Should().Be("-1");
        CoachSettingsMap.GetValue(s, CoachSettingNames.RaAggression).Should().Be("0.65");
        CoachSettingsMap.GetValue(s, CoachSettingNames.RaHysteresis).Should().Be("0.1", "the algorithm default is reported when not set");
        CoachSettingsMap.GetValue(s, CoachSettingNames.RaMinMove).Should().Be("0", "0 = automatic");
        CoachSettingsMap.GetValue(s, CoachSettingNames.DecAggression).Should().Be("1", "ResistSwitch default");
        CoachSettingsMap.GetValue(s, CoachSettingNames.DecAlgorithm).Should().Be("ResistSwitch");
        CoachSettingsMap.GetValue(s, CoachSettingNames.DecGuideMode).Should().Be("Auto");
        CoachSettingsMap.GetValue(s, CoachSettingNames.BacklashCompensation).Should().Be("true");
        CoachSettingsMap.GetValue(s, CoachSettingNames.BacklashPulseMs).Should().Be("450");
        CoachSettingsMap.GetValue(s, "Nope").Should().BeNull();
        CoachSettingNames.All.Should().OnlyContain(n => CoachSettingsMap.GetValue(s, n) != null);
    }

    [Test]
    public void Settings_map_applies_changes_and_round_trips()
    {
        var s = new GuiderSettings();
        var changed = CoachSettingsMap.Apply(s,
        [
            new(CoachSettingNames.ExposureSeconds, "3"),
            new(CoachSettingNames.Gain, "120"),
            new(CoachSettingNames.RaAggression, "0.8"),
            new(CoachSettingNames.RaMinMove, "0.35"),
            new(CoachSettingNames.DecMinMove, "0.4"),
            new(CoachSettingNames.DecGuideMode, "South"),
            new(CoachSettingNames.BacklashCompensation, "true"),
            new(CoachSettingNames.BacklashPulseMs, "600"),
            new(CoachSettingNames.MaxRaDurationMs, "3000"),
            new(CoachSettingNames.MultiStar, "false"),
            new(CoachSettingNames.DecAlgorithm, "Lowpass2"),
        ]);

        changed.ExposureMs.Should().Be(3000);
        changed.Gain.Should().Be(120);
        changed.RaAlgorithm.Parameters!["aggression"].Should().Be(0.8);
        changed.RaAlgorithm.Parameters!["minMove"].Should().Be(0.35);
        changed.DecAlgorithm.Kind.Should().Be(GuideAlgorithmKind.Lowpass2);
        changed.DecAlgorithm.Parameters!["minMove"].Should().Be(0.4, "min-move survives an algorithm change");
        changed.DecGuideMode.Should().Be(DecGuideMode.South);
        changed.Backlash.Should().Be(new BacklashSettings { Enabled = true, PulseMs = 600 });
        changed.MaxRaDurationMs.Should().Be(3000);
        changed.MultiStar.MultiStarEnabled.Should().BeFalse();
        s.ExposureMs.Should().Be(2000, "the input is not modified");

        var auto = CoachSettingsMap.Apply(changed, [new(CoachSettingNames.RaMinMove, "0"), new(CoachSettingNames.Gain, "-1")]);
        auto.RaAlgorithm.Parameters!.ContainsKey("minMove").Should().BeFalse();
        auto.Gain.Should().BeNull();

        CoachSettingsMap.TryApply(s, [new("Bogus", "1")], out _, out var error).Should().BeFalse();
        error.Should().Contain("Bogus");
        CoachSettingsMap.TryApply(s, [new(CoachSettingNames.DecGuideMode, "Sideways")], out _, out _).Should().BeFalse();
    }

    [Test]
    public void Live_hints_flag_oscillation_rate_limited_and_dismissable()
    {
        var hints = new LiveHintAnalyser();
        var settings = new GuiderSettings();
        var stats = Stats(oscillation: 0.8, rmsRa: 0.9);
        var raised = new List<CoachFinding>();
        for (int i = 0; i < 40; i++)
        {
            raised.AddRange(hints.Analyse(Input(T0.AddSeconds(2 * i), stats, settings)));
        }

        raised.Should().ContainSingle(h => h.Code == CoachCodes.HintRaOscillation, "hints are raised once per 10 minutes");
        var hint = raised.Single(h => h.Code == CoachCodes.HintRaOscillation);
        hint.Step.Should().Be(CoachStepNames.Live);
        hint.Parameters["index"].Should().Be(0.8);
        hint.Changes.Should().ContainSingle().Which.Should().Be(new CoachSettingChange(CoachSettingNames.RaAggression, "0.6", "0.7"));
        hint.ExpiresAt.Should().Be(hint.Timestamp.AddMinutes(10));
        hints.GetActive(T0.AddMinutes(2)).Should().ContainSingle(h => h.Id == hint.Id);
        hints.GetActive(T0.AddMinutes(12)).Should().BeEmpty("hints expire");

        // raised again after 10 minutes, unless dismissed
        hints.Analyse(Input(T0.AddMinutes(11), stats, settings)).Should().Contain(h => h.Id == hint.Id);
        hints.Dismiss(hint.Id).Should().BeTrue();
        hints.GetActive(T0.AddMinutes(11)).Should().BeEmpty();
        hints.Analyse(Input(T0.AddMinutes(30), stats, settings)).Should().NotContain(h => h.Id == hint.Id);
        hints.ResetSession();
        for (int i = 0; i < 40; i++)
        {
            raised.AddRange(hints.Analyse(Input(T0.AddMinutes(40).AddSeconds(2 * i), stats, settings)));
        }

        raised.Count(h => h.Id == hint.Id).Should().Be(2, "a new guiding session forgets dismissals");
    }

    [Test]
    public void Live_hints_detect_one_sided_dec_limited_pulses_low_snr_and_seeing_floor()
    {
        var hints = new LiveHintAnalyser { SeeingFloorArcsec = 0.7 };
        var settings = new GuiderSettings { MaxDecDurationMs = 2000 };
        var stats = Stats(oscillation: 0.4, rmsRa: 0.5, rmsTotal: 0.75, decDrift: 1.5, pae: 6.2);
        var raised = new List<CoachFinding>();
        for (int i = 0; i < 40; i++)
        {
            raised.AddRange(hints.Analyse(Input(T0.AddSeconds(2 * i), stats, settings, decMs: 300, decDir: GuideDirection.South, decLimited: i % 5 == 0,
                snr: i < 30 ? 40 : 12)));
        }

        var dec = raised.Single(h => h.Code == CoachCodes.HintDecDrift);
        dec.Parameters["arcmin"].Should().Be(6.2);
        dec.Changes.Should().ContainSingle().Which.Value.Should().Be("South");
        var limited = raised.Single(h => h.Code == CoachCodes.HintPulseLimited);
        limited.Id.Should().Be("hint.pulseLimited:Dec");
        limited.Parameters["axis"].Should().Be("Dec");
        limited.Changes.Should().ContainSingle().Which.Should().Be(new CoachSettingChange(CoachSettingNames.MaxDecDurationMs, "3000", "2000"));
        raised.Should().Contain(h => h.Code == CoachCodes.HintLowSnr, "SNR dropped from 40 to 12");
        raised.Should().Contain(h => h.Code == CoachCodes.HintSeeingBound && h.Severity == CoachSeverities.Good);
        raised.Should().NotContain(h => h.Code == CoachCodes.HintRaOscillation || h.Code == CoachCodes.HintRaSluggish);
    }

    private static LiveHintInput Input(DateTimeOffset t, GuidingStatsSnapshot stats, GuiderSettings s, int decMs = 0, GuideDirection? decDir = null,
        bool decLimited = false, double snr = 40) => new()
    {
        Time = t,
        Stats = stats,
        Settings = s,
        RaDurationMs = 100,
        DecDurationMs = decMs,
        DecDirection = decDir,
        DecLimited = decLimited,
        Snr = snr,
    };

    private static GuidingStatsSnapshot Stats(double oscillation, double rmsRa, double rmsTotal = 1.0, double? decDrift = null, double? pae = null)
    {
        var block = new GuidingStatsBlock
        {
            Frames = 60,
            IncludedFrames = 60,
            OscillationIndex = oscillation,
            RmsRaArcsec = rmsRa,
            RmsTotalArcsec = rmsTotal,
            DecDriftArcsecPerMin = decDrift,
            PolarAlignmentErrorArcmin = pae,
        };
        return new GuidingStatsSnapshot { Window = block, Session = block };
    }
}
