// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.INDI;
using PinsGuider.Engine.Core;
using NinaPierSide = NINA.Core.Enum.PierSide;
using PierSide = PinsGuider.Engine.Core.PierSide;

namespace PinsGuider.Plugin.Hardware;

/// <summary>Mount state from NINA's telescope mediator.</summary>
internal sealed class NinaMountState(ITelescopeMediator telescope, Func<bool>? pauseWhenSlewing = null, Func<bool>? pauseWhenTrackingOff = null,
    bool mountOptional = false) : IMountState
{
    public const double SiderealArcsecPerSec = 15.041;

    public MountSnapshot GetSnapshot()
    {
        var info = telescope.GetInfo();
        if (info is null || !info.Connected)
        {
            // ST4 guiding works without a mount connection; pier side/declination are then unknown
            return mountOptional
                ? new MountSnapshot { IsConnected = true, IsTracking = true, PierSide = PierSide.Unknown }
                : new MountSnapshot { IsConnected = false };
        }

        static double? Rate(double arcsecPerSec) => double.IsNaN(arcsecPerSec) || arcsecPerSec <= 0 ? null : arcsecPerSec / SiderealArcsecPerSec;

        return new MountSnapshot
        {
            IsConnected = true,
            DeclinationDeg = double.IsNaN(info.Declination) ? null : info.Declination,
            RightAscensionHours = double.IsNaN(info.RightAscension) ? null : info.RightAscension,
            PierSide = info.SideOfPier switch
            {
                NinaPierSide.pierEast => PierSide.East,
                NinaPierSide.pierWest => PierSide.West,
                _ => PierSide.Unknown,
            },
            IsSlewing = info.Slewing && (pauseWhenSlewing?.Invoke() ?? true),
            IsParked = info.AtPark,
            IsHoming = false,
            IsTracking = info.TrackingEnabled || !(pauseWhenTrackingOff?.Invoke() ?? true),
            GuideRateRa = Rate(info.GuideRateRightAscensionArcsecPerSec),
            GuideRateDec = Rate(info.GuideRateDeclinationArcsecPerSec),
        };
    }
}

/// <summary>
/// Pulse guiding through the connected NINA mount (any driver type). NINA's PulseGuide returns immediately for INDI
/// mounts, so completion is awaited here: the nominal duration, then the driver's IsPulseGuiding flag.
/// </summary>
internal sealed class MountPulseOutput(ITelescopeMediator telescope) : IPulseOutput
{
    public string Name => "Mount";

    public bool IsConnected => telescope.GetInfo()?.Connected == true;

    public bool SupportsSimultaneousPulses => false;

    public async Task PulseAsync(GuideDirection direction, int durationMs, CancellationToken ct)
    {
        if (durationMs <= 0)
        {
            return;
        }

        var info = telescope.GetInfo();
        if (info is null || !info.Connected)
        {
            throw new InvalidOperationException("mount not connected");
        }

        if (info.AtPark)
        {
            throw new InvalidOperationException("mount is parked");
        }

        var dir = direction switch
        {
            GuideDirection.North => GuideDirections.guideNorth,
            GuideDirection.South => GuideDirections.guideSouth,
            GuideDirection.East => GuideDirections.guideEast,
            _ => GuideDirections.guideWest,
        };

        var started = DateTime.UtcNow;
        await Task.Run(() => telescope.PulseGuide(dir, durationMs), ct).ConfigureAwait(false);

        // wait for the pulse to complete: nominal duration, then the driver's busy flag (bounded)
        var remaining = TimeSpan.FromMilliseconds(durationMs) - (DateTime.UtcNow - started);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ct).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(1000, durationMs / 2));
        var device = telescope.GetDevice() as ITelescope;
        while (device is not null && DateTime.UtcNow < deadline)
        {
            bool busy;
            try
            {
                busy = device.IsPulseGuiding;
            }
            catch
            {
                break;
            }

            if (!busy)
            {
                break;
            }

            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Pulse guiding through the guide camera's ST4 port (INDI TELESCOPE_TIMED_GUIDE_* on the camera device).</summary>
internal sealed class CameraSt4PulseOutput(Func<string?> cameraDevice) : IPulseOutput
{
    public string Name => "Camera ST4";

    public bool IsConnected => cameraDevice() is not null;

    public bool SupportsSimultaneousPulses => false;

    public async Task PulseAsync(GuideDirection direction, int durationMs, CancellationToken ct)
    {
        if (durationMs <= 0)
        {
            return;
        }

        string device = cameraDevice() ?? throw new InvalidOperationException("guide camera not connected");
        bool ns = direction is GuideDirection.North or GuideDirection.South;
        string property = ns ? "TELESCOPE_TIMED_GUIDE_NS" : "TELESCOPE_TIMED_GUIDE_WE";
        var elements = ns
            ? new Dictionary<string, object> { ["TIMED_GUIDE_N"] = direction == GuideDirection.North ? durationMs : 0, ["TIMED_GUIDE_S"] = direction == GuideDirection.South ? durationMs : 0 }
            : new Dictionary<string, object> { ["TIMED_GUIDE_W"] = direction == GuideDirection.West ? durationMs : 0, ["TIMED_GUIDE_E"] = direction == GuideDirection.East ? durationMs : 0 };
        if (!INDIClient.Instance.SetProperty(device, property, elements, out var error))
        {
            throw new InvalidOperationException($"ST4 pulse failed: {error}");
        }

        await Task.Delay(durationMs + 20, ct).ConfigureAwait(false);
        Logger.Trace($"NativeGuider: ST4 {direction} {durationMs} ms");
    }
}
