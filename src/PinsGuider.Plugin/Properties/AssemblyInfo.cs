// SPDX-License-Identifier: MPL-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: Guid("3e519099-8e1e-46ee-bed2-f775abd90619")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyTitle("PINS Native Guider")]
[assembly: AssemblyDescription("Native autoguider for PINS: PHD2-level guiding without PHD2.")]
[assembly: AssemblyCompany("André Duffeck")]
[assembly: AssemblyProduct("PINS Native Guider")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]
[assembly: AssemblyMetadata("License", "MPL-2.0")]
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
[assembly: AssemblyMetadata("Repository", "https://github.com/aduffeck/pins-guider")]
[assembly: AssemblyMetadata("Tags", "Guiding,Guider,Autoguider")]
[assembly: AssemblyMetadata("LongDescription", @"Native multi-star autoguider with PHD2-compatible star detection, calibration and guide algorithms.
Select 'PINS Native Guider' in the guider chooser; PHD2 stays available.")]
[assembly: ComVisible(false)]
[assembly: InternalsVisibleTo("PinsGuider.Plugin.Tests")]
