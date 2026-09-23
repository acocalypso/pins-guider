// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using PinsGuider.Engine.Coach;
using PinsGuider.Engine.Guiding;

namespace PinsGuider.Plugin;

/// <summary>
/// Guiding Coach host of the plugin: imaging scale and profile from NINA, applied changes go through the plugin
/// settings (persisted in the profile), reports are JSON files in ~/.local/share/NINA/NativeGuider/Coach.
/// </summary>
internal sealed class NativeCoachHost(
    IProfileService profileService,
    NativeGuiderOptions options,
    NativeCoachHost.SettingSetter setSetting,
    Func<SettleParams> settle,
    Func<bool> isBusy,
    string? reportDirectory = null) : ICoachHost
{
    public delegate bool SettingSetter(string name, string value, out string error);

    public static string ReportDirectory => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NativeGuider", "Coach");

    private readonly FileCoachReportStore store = new(reportDirectory ?? ReportDirectory);

    /// <summary>Imaging camera scale from the profile's camera pixel size and telescope focal length.</summary>
    public double? ImagingScale
    {
        get
        {
            var p = profileService.ActiveProfile;
            double pixel = p.CameraSettings.PixelSize;
            double focal = p.TelescopeSettings.FocalLength;
            return pixel > 0 && focal > 0 && !double.IsNaN(pixel) && !double.IsNaN(focal) ? 206.265 * pixel / focal : null;
        }
    }

    public string? ProfileName => profileService.ActiveProfile?.Name;

    public SettleParams? TrialSettle => settle();

    public bool IsBusy => isBusy();

    public string? GetSettingValue(string name) => NativeGuiderOptions.Find(name) is null ? null : options.GetString(name);

    public bool ApplySettings(IReadOnlyList<CoachSettingChange> changes, out string? error)
    {
        foreach (var c in changes)
        {
            if (NativeGuiderOptions.Find(c.Name) is null)
            {
                error = $"unknown setting {c.Name}";
                return false;
            }
        }

        foreach (var c in changes)
        {
            if (!setSetting(c.Name, c.Value, out var e))
            {
                error = $"{c.Name}: {e}";
                return false;
            }

            Logger.Info($"NativeGuider: coach applied {c.Name} = {c.Value} (was {c.CurrentValue ?? "?"})");
        }

        error = null;
        return true;
    }

    public void SaveReport(CoachReport report)
    {
        try
        {
            store.Save(report);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    public IReadOnlyList<CoachReport> LoadReports(int max)
    {
        try
        {
            string? profile = ProfileName;
            return store.Load(int.MaxValue)
                .Where(r => profile is null || r.ProfileName is null || r.ProfileName == profile)
                .Take(max)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return [];
        }
    }
}
