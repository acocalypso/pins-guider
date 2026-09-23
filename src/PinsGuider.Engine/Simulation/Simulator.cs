// SPDX-License-Identifier: MPL-2.0

using PinsGuider.Engine.Core;

namespace PinsGuider.Engine.Simulation;

/// <summary>
/// Closed-loop sky / mount / camera simulator wired from a <see cref="SimulatorScenario"/>. Hand
/// <see cref="Camera"/> (ICameraSource), <see cref="Mount"/> (IPulseOutput, IMountState) and
/// <see cref="Clock"/> (IClock) to the engine; use the truth accessors to evaluate guiding.
/// </summary>
public sealed class Simulator
{
    /// <summary>Creates the simulator. Without <paramref name="clock"/> a fresh <see cref="Simulation.VirtualClock"/> is used.</summary>
    public Simulator(SimulatorScenario scenario, IClock? clock = null)
    {
        Scenario = scenario;
        Clock = clock ?? new VirtualClock();
        Mount = new SimulatedMount(scenario.Mount, Clock);

        // The random field needs the camera geometry; build a geometry-only camera first.
        var stars = scenario.Sky.Stars;
        if (stars == null)
        {
            var geometry = new SimulatedCamera(scenario.Camera with { HotPixelCount = 0, ColdPixelCount = 0 }, new SimulatedSky(scenario.Sky, []), Mount, Clock);
            stars = SimulatedSky.GenerateRandomField(scenario.Sky, geometry.SensorToSky, scenario.Camera.SensorWidth, scenario.Camera.SensorHeight);
        }

        Sky = new SimulatedSky(scenario.Sky, stars);
        Camera = new SimulatedCamera(scenario.Camera, Sky, Mount, Clock);
    }

    public SimulatorScenario Scenario { get; }

    public IClock Clock { get; }

    /// <summary>The virtual clock, or null when the simulator runs on another clock.</summary>
    public VirtualClock? VirtualClock => Clock as VirtualClock;

    public SimulatedMount Mount { get; }

    public SimulatedSky Sky { get; }

    public SimulatedCamera Camera { get; }

    /// <summary>Current simulator time in seconds.</summary>
    public double Now => Clock.NowSeconds();

    /// <summary>True mount pointing error (arcsec on the sky) at time <paramref name="t"/>.</summary>
    public SkyOffset TruePointingError(double t) => Mount.GetPointingError(t);

    /// <summary>True mount pointing error now.</summary>
    public SkyOffset TruePointingError() => Mount.GetPointingError(Now);

    /// <summary>True (seeing-free) star positions in frame pixels at time <paramref name="t"/>.</summary>
    public IReadOnlyList<StarTruth> TrueStarPositions(double t, int binning = 1) => Camera.StarPositionsAt(t, binning);

    /// <summary>Truth of the last rendered frame.</summary>
    public FrameTruth? LastFrameTruth => Camera.LastFrameTruth;

    /// <summary>
    /// Index of the brightest star whose position stays at least <paramref name="marginPx"/> from the
    /// frame edges (seeing-free, at time <paramref name="t"/>); -1 when none.
    /// </summary>
    public int BrightestStarIndex(double t = 0, int marginPx = 30, int binning = 1)
    {
        var pos = TrueStarPositions(t, binning);
        int wb = Camera.SensorWidth / binning, hb = Camera.SensorHeight / binning;
        int best = -1;
        for (int i = 0; i < pos.Count; i++)
        {
            var p = pos[i];
            if (p.X < marginPx || p.Y < marginPx || p.X > wb - 1 - marginPx || p.Y > hb - 1 - marginPx) continue;
            if (best < 0 || Sky.Stars[i].Magnitude < Sky.Stars[best].Magnitude) best = i;
        }

        return best;
    }

    /// <summary>Convenience: <see cref="CaptureRequest"/> round trip on <see cref="Camera"/>.</summary>
    public Task<GuideFrame> CaptureAsync(double exposureMs, CancellationToken ct = default, int binning = 1) =>
        Camera.CaptureAsync(new CaptureRequest(exposureMs, binning), ct);
}
