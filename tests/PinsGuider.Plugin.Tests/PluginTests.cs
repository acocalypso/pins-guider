// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Moq;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using PinsGuider.Engine.Algorithms;
using PinsGuider.Engine.Core;
using PinsGuider.Plugin;
using PinsGuider.Plugin.Hardware;

namespace PinsGuider.Plugin.Tests;

[TestFixture]
public class FitsReaderTests
{
    private static byte[] Fits(int bitpix, int w, int h, Func<int, int, double> value, params string[] extraCards)
    {
        var cards = new List<string>
        {
            "SIMPLE  =                    T / conforms",
            $"BITPIX  = {bitpix,20}",
            "NAXIS   =                    2",
            $"NAXIS1  = {w,20} / width",
            $"NAXIS2  = {h,20}",
        };
        if (bitpix == 16)
        {
            cards.Add("BZERO   =                32768");
            cards.Add("BSCALE  =                    1");
        }

        cards.AddRange(extraCards);
        cards.Add("END");
        var header = new StringBuilder();
        foreach (var c in cards)
        {
            header.Append(c.PadRight(80));
        }

        while (header.Length % 2880 != 0)
        {
            header.Append(' ');
        }

        int bytes = Math.Abs(bitpix) / 8;
        var data = new byte[((w * h * bytes) + 2879) / 2880 * 2880];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                double v = value(x, y);
                if (bitpix == 8)
                {
                    data[i] = (byte)v;
                }
                else
                {
                    BinaryPrimitives.WriteInt16BigEndian(data.AsSpan(i * 2), (short)(v - 32768));
                }
            }
        }

        return Encoding.ASCII.GetBytes(header.ToString()).Concat(data).ToArray();
    }

    [Test]
    public void Reads_16_bit_with_bzero()
    {
        var bytes = Fits(16, 7, 5, (x, y) => x * 1000 + y * 7 + 3, "OBJECT  = 'guide / star'        / a comment");
        var f = FitsReader.Read(bytes, ".fits");
        f.Width.Should().Be(7);
        f.Height.Should().Be(5);
        f[0, 0].Should().Be(3);
        f[6, 4].Should().Be(6 * 1000 + 4 * 7 + 3);
        f.BitsPerPixel.Should().Be(16);
    }

    [Test]
    public void Reads_8_bit()
    {
        var f = FitsReader.Read(Fits(8, 4, 3, (x, y) => x + 10 * y), ".fits");
        f[3, 2].Should().Be(23);
        f.BitsPerPixel.Should().Be(8);
    }

    [Test]
    public void Reads_zlib_compressed_blob()
    {
        var raw = Fits(16, 16, 16, (x, y) => 40000 + x - y);
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }

        var f = FitsReader.Read(ms.ToArray(), ".fits.z");
        f[15, 0].Should().Be(40015);
        f[0, 15].Should().Be(39985);
    }

    [Test]
    public void Multi_hdu_roundtrip_keeps_frames_and_keywords()
    {
        var a = new GuideFrame(5, 4);
        var b = new GuideFrame(5, 4);
        for (int i = 0; i < 20; i++)
        {
            a.Pixels[i] = (ushort)(i * 100);
            b.Pixels[i] = (ushort)(65535 - i);
        }

        using var ms = new MemoryStream();
        FitsReader.Write(ms, [(a, new Dictionary<string, string> { ["EXPOSURE"] = "0.5" }), (b, new Dictionary<string, string> { ["EXPOSURE"] = "2" })]);
        var hdus = FitsReader.ReadAll(ms.ToArray());
        hdus.Should().HaveCount(2);
        hdus[0].Frame.Pixels.Should().Equal(a.Pixels);
        hdus[1].Frame.Pixels.Should().Equal(b.Pixels);
        hdus[1].Header["EXPOSURE"].Should().Be("2");
        hdus[1].Header["XTENSION"].Should().Be("IMAGE");
    }

    [Test]
    public void Reads_the_real_phd2_dark_library_if_present()
    {
        const string path = "/src/../phd2-dark-sample/PHD2_dark_lib_2.fit";
        var candidates = new[] { path, Environment.GetEnvironmentVariable("PHD2_DARK_LIB") ?? string.Empty }.Where(File.Exists).ToList();
        if (candidates.Count == 0)
        {
            Assert.Ignore("no PHD2 dark library sample available");
        }

        var hdus = FitsReader.ReadAll(File.ReadAllBytes(candidates[0]));
        hdus.Should().HaveCount(8);
        hdus.Should().OnlyContain(h => h.Frame.Width == 1280 && h.Frame.Height == 960);
        hdus.Select(h => h.Header["EXPOSURE"]).Should().Contain("2.");
    }

    [Test]
    public void Rejects_truncated_data()
    {
        var bytes = Fits(16, 100, 100, (_, _) => 1);
        FluentActions.Invoking(() => FitsReader.Read(bytes.Take(4000).ToArray(), ".fits")).Should().Throw<InvalidDataException>();
    }
}

[TestFixture]
public class NativeGuiderOptionsTests
{
    private delegate bool TryGetString(Guid id, string key, out string value);

    private static (NativeGuiderOptions Options, Mock<IGuiderSettings> Guider) Create()
    {
        var guider = new Mock<IGuiderSettings>();
        var profile = new Mock<IProfile>();
        var store = new Dictionary<string, string>();
        var plugin = new Mock<IPluginSettings>();
        plugin.Setup(p => p.SetValue(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()))
            .Callback<Guid, string, string>((_, k, v) => store[k] = v);
        plugin.Setup(p => p.TryGetValue(It.IsAny<Guid>(), It.IsAny<string>(), out It.Ref<string>.IsAny))
            .Returns(new TryGetString((Guid _, string k, out string v) => store.TryGetValue(k, out v!)));
        profile.SetupGet(p => p.PluginSettings).Returns(plugin.Object);
        profile.SetupGet(p => p.GuiderSettings).Returns(guider.Object);
        profile.SetupGet(p => p.Id).Returns(Guid.NewGuid());
        var service = new Mock<IProfileService>();
        service.SetupGet(s => s.ActiveProfile).Returns(profile.Object);
        return (new NativeGuiderOptions(service.Object, NativeGuiderPlugin.PluginGuid), guider);
    }

    [Test]
    public void Defaults_without_phd2_settings()
    {
        var (o, _) = Create();
        o.GuideCameraDriver.Should().Be("indi_asi_ccd");
        o.GetDouble("ExposureSeconds").Should().Be(2);
        var s = o.ToEngineSettings();
        s.RaAlgorithm.Kind.Should().Be(GuideAlgorithmKind.Hysteresis);
        s.DecAlgorithm.Kind.Should().Be(GuideAlgorithmKind.ResistSwitch);
        s.AutoCalibrationStep.Should().BeTrue();
        s.MaxRaDurationMs.Should().Be(2500);
        s.Gain.Should().BeNull();
    }

    [Test]
    public void Falls_back_to_the_profiles_phd2_settings()
    {
        var (o, g) = Create();
        g.SetupGet(x => x.PHD2FocalLength).Returns(248);
        g.SetupGet(x => x.PHD2ExposureMs).Returns(1500);
        g.SetupGet(x => x.PHD2CameraGain).Returns(48);
        g.SetupGet(x => x.PHD2GuideAlgorithmRA).Returns("Lowpass2");
        g.SetupGet(x => x.PHD2RALowpass2Aggressiveness).Returns(65);
        g.SetupGet(x => x.PHD2DecGuideMode).Returns("North");
        g.SetupGet(x => x.PHD2MaxRADuration).Returns(3000);

        var s = o.ToEngineSettings();
        s.FocalLengthMm.Should().Be(248);
        s.ExposureMs.Should().Be(1500);
        s.Gain.Should().Be(48);
        s.RaAlgorithm.Kind.Should().Be(GuideAlgorithmKind.Lowpass2);
        s.RaAlgorithm.Parameters!["aggressiveness"].Should().Be(65);
        s.DecGuideMode.Should().Be(DecGuideMode.North);
        s.MaxRaDurationMs.Should().Be(3000);
    }

    [Test]
    public void Explicit_setting_wins_over_phd2_fallback_and_is_validated()
    {
        var (o, g) = Create();
        g.SetupGet(x => x.PHD2FocalLength).Returns(248);
        o.TrySet("FocalLengthMm", "180", out _).Should().BeTrue();
        o.FocalLengthMm.Should().Be(180);
        o.TrySet("FocalLengthMm", "-5", out var err).Should().BeFalse();
        err.Should().Contain("between");
        o.TrySet("DecGuideMode", "south", out _).Should().BeTrue();
        o.GetString("DecGuideMode").Should().Be("South");
        o.TrySet("DecGuideMode", "sideways", out _).Should().BeFalse();
        o.TrySet("ExposureSeconds", "2,5", out _).Should().BeTrue();
        o.GetDouble("ExposureSeconds").Should().Be(2.5);
        o.TrySet("Nope", "1", out _).Should().BeFalse();
    }

    [Test]
    public void Describe_lists_all_settings_with_basic_flags()
    {
        var (o, _) = Create();
        var d = o.Describe();
        d.Should().HaveCount(NativeGuiderOptions.Definitions.Count);
        d.Should().Contain(x => x.Name == "GuideCameraDevice" && x.Basic && x.RequiresReconnect);
        d.Single(x => x.Name == "DecGuideMode").Options.Should().Equal("Auto", "North", "South", "Off");
    }
}

[TestFixture]
public class MountAdapterTests
{
    [Test]
    public async Task Mount_pulse_maps_directions_and_waits_for_completion()
    {
        var info = new TelescopeInfo { Connected = true };
        var mediator = new Mock<ITelescopeMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(info);
        var calls = new List<(GuideDirections, int)>();
        mediator.Setup(m => m.PulseGuide(It.IsAny<GuideDirections>(), It.IsAny<int>())).Callback<GuideDirections, int>((d, ms) => calls.Add((d, ms)));
        var output = new MountPulseOutput(mediator.Object);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await output.PulseAsync(GuideDirection.West, 300, CancellationToken.None);
        await output.PulseAsync(GuideDirection.North, 100, CancellationToken.None);
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(390);
        calls.Should().Equal((GuideDirections.guideWest, 300), (GuideDirections.guideNorth, 100));
    }

    [Test]
    public async Task Mount_pulse_refuses_when_parked_or_disconnected()
    {
        var mediator = new Mock<ITelescopeMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = true, AtPark = true });
        var output = new MountPulseOutput(mediator.Object);
        await FluentActions.Awaiting(() => output.PulseAsync(GuideDirection.East, 100, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo { Connected = false });
        await FluentActions.Awaiting(() => output.PulseAsync(GuideDirection.East, 100, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        mediator.Verify(m => m.PulseGuide(It.IsAny<GuideDirections>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public void Mount_state_maps_telescope_info()
    {
        var mediator = new Mock<ITelescopeMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo
        {
            Connected = true,
            Declination = 41.2,
            RightAscension = 5.5,
            SideOfPier = NINA.Core.Enum.PierSide.pierWest,
            Slewing = true,
            TrackingEnabled = true,
            GuideRateRightAscensionArcsecPerSec = 7.5205,
            GuideRateDeclinationArcsecPerSec = double.NaN,
        });
        var s = new NinaMountState(mediator.Object).GetSnapshot();
        s.IsConnected.Should().BeTrue();
        s.DeclinationDeg.Should().Be(41.2);
        s.PierSide.Should().Be(Engine.Core.PierSide.West);
        s.IsSlewing.Should().BeTrue();
        s.GuideRateRa.Should().BeApproximately(0.5, 1e-3);
        s.GuideRateDec.Should().BeNull();
    }
}
