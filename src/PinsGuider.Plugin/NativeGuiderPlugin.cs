// SPDX-License-Identifier: MPL-2.0

using System.ComponentModel.Composition;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;

namespace PinsGuider.Plugin;

/// <summary>Plugin manifest (metadata comes from the assembly attributes).</summary>
[Export(typeof(IPluginManifest))]
public sealed class NativeGuiderPlugin : PluginBase
{
    public static readonly Guid PluginGuid = Guid.Parse("3e519099-8e1e-46ee-bed2-f775abd90619");

    [ImportingConstructor]
    public NativeGuiderPlugin()
    {
    }
}

/// <summary>Adds the native guider to the guider chooser, next to PHD2.</summary>
[Export(typeof(IEquipmentProvider))]
public sealed class NativeGuiderProvider : IEquipmentProvider<IGuider>
{
    private static readonly object Gate = new();
    private static NativeGuider? instance;
    private readonly IProfileService profileService;
    private readonly ITelescopeMediator telescopeMediator;
    private readonly ICameraMediator cameraMediator;

    [ImportingConstructor]
    public NativeGuiderProvider(IProfileService profileService, ITelescopeMediator telescopeMediator, ICameraMediator cameraMediator)
    {
        this.profileService = profileService;
        this.telescopeMediator = telescopeMediator;
        this.cameraMediator = cameraMediator;
    }

    public string Name => "PINS Native Guider";

    /// <summary>Called on every chooser rescan: always returns the same device instance (keeps connection and state).</summary>
    public IList<IGuider> GetEquipment()
    {
        lock (Gate)
        {
            instance ??= new NativeGuider(profileService, telescopeMediator, cameraMediator, NativeGuiderPlugin.PluginGuid);
            return [instance];
        }
    }
}
