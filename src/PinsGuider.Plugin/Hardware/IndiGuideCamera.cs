// SPDX-License-Identifier: MPL-2.0

using NINA.Core.Utility;
using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Enums;
using PinsGuider.Engine.Core;

namespace PinsGuider.Plugin.Hardware;

/// <summary>
/// Guide camera driven directly over INDI (independent of the imaging camera's view model). The driver is loaded
/// under its own "GuideCamera" category so it never evicts the main camera's driver. Frames arrive as FITS BLOBs and
/// are decoded in memory.
/// </summary>
internal sealed class IndiGuideCamera : ICameraSource, IDisposable
{
    /// <summary>INDIClient category key for the guide camera driver (separate from "Camera").</summary>
    public const string Category = "GuideCamera";

    private readonly string driver;
    private readonly string requestedDevice;
    private readonly Func<string?> mainCameraName;
    private readonly SemaphoreSlim gate = new(1, 1);
    private INDICamera? camera;
    private INDIDeviceInfo? info;

    public IndiGuideCamera(string driver, string deviceName, Func<string?> mainCameraName)
    {
        this.driver = driver;
        requestedDevice = deviceName ?? string.Empty;
        this.mainCameraName = mainCameraName;
    }

    public string Name => info?.Id ?? requestedDevice;

    public bool IsConnected => camera?.Connected == true;

    public int SensorWidth => camera?.CameraXSize ?? 0;

    public int SensorHeight => camera?.CameraYSize ?? 0;

    public double PixelSizeUm => camera is { } c && !double.IsNaN(c.PixelSizeX) ? c.PixelSizeX : 0;

    public int MaxBinning => camera?.MaxBinX ?? 1;

    public ushort MaxAdu => 0;

    public int BitsPerPixel => camera?.BitDepth ?? 16;

    /// <summary>INDI device name, used to address the camera's ST4 guide port.</summary>
    public string? DeviceId => info?.Id;

    /// <summary>Lists the CCD devices of <paramref name="driverName"/> (loads the driver under the guide camera category).</summary>
    public static async Task<IReadOnlyList<string>> ListDevicesAsync(string driverName, CancellationToken ct)
    {
        var devices = await INDIClient.Instance.GetDevices(DeviceInterface.CCD_INTERFACE, driverName, Category, ct).ConfigureAwait(false);
        return devices.Where(d => string.Equals(d.Driver, driverName, StringComparison.OrdinalIgnoreCase)).Select(d => d.Id).ToList();
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConnectCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        if (camera?.Connected == true)
        {
            return;
        }

        var devices = await INDIClient.Instance.GetDevices(DeviceInterface.CCD_INTERFACE, driver, Category, ct).ConfigureAwait(false);
        var candidates = devices.Where(d => string.Equals(d.Driver, driver, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            // some drivers report DRIVER_EXEC differently; fall back to all CCD devices
            candidates = devices.ToList();
        }

        INDIDeviceInfo? chosen;
        if (!string.IsNullOrWhiteSpace(requestedDevice))
        {
            chosen = candidates.FirstOrDefault(d => string.Equals(d.Id, requestedDevice, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(d => d.Id.Contains(requestedDevice, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                throw new GuideCameraException(
                    $"Guide camera '{requestedDevice}' not found for driver {driver}. Available: {string.Join(", ", candidates.Select(d => d.Id))}");
            }
        }
        else
        {
            string? main = mainCameraName();
            chosen = candidates.FirstOrDefault(d => main is null || !string.Equals(d.Id, main, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault();
            if (chosen is null)
            {
                throw new GuideCameraException($"No INDI camera found for driver {driver}. Is the guide camera plugged in and powered?");
            }
        }

        info = chosen;
        Logger.Info($"NativeGuider: connecting guide camera '{chosen.Id}' (driver {chosen.Driver})");

        // the INDIDevice constructor blocks while waiting for the CONNECTION property
        camera?.Dispose();
        camera = await Task.Run(() => new INDICamera(chosen), ct).ConfigureAwait(false);
        if (!await camera.Connect(ct).ConfigureAwait(false))
        {
            throw new GuideCameraException($"Could not connect guide camera '{chosen.Id}'");
        }

        INDIClient.Instance.EnableBLOB(chosen.Id);
        Logger.Info($"NativeGuider: guide camera '{chosen.Id}' connected: {SensorWidth}x{SensorHeight}, {PixelSizeUm} µm, {BitsPerPixel} bit");
    }

    public async Task<GuideFrame> CaptureAsync(CaptureRequest request, CancellationToken ct)
    {
        var cam = camera;
        if (cam is null || !cam.Connected)
        {
            throw new GuideCameraException("guide camera not connected");
        }

        double seconds = Math.Max(request.ExposureMs / 1000.0, 0.001);
        short bin = (short)Math.Max(1, request.Binning);
        bool sub = !request.Subframe.IsEmpty;
        var started = DateTimeOffset.UtcNow;
        cam.StartExposure(seconds, bin, bin, sub, request.Subframe.X, request.Subframe.Y, request.Subframe.Width, request.Subframe.Height,
            request.Gain ?? -1, request.Offset ?? -1);

        // generous timeout: exposure + download (USB 2 guide cameras, busy Pi) + INDI BLOB transfer
        var timeout = TimeSpan.FromSeconds(seconds + 15);
        try
        {
            await cam.WaitUntilExposureIsReady(ct).WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try
            {
                cam.AbortExposure();
            }
            catch
            {
                // ignore
            }

            throw new GuideCameraException($"guide exposure timed out after {timeout.TotalSeconds:F0} s");
        }
        catch (OperationCanceledException)
        {
            try
            {
                cam.AbortExposure();
            }
            catch
            {
                // ignore
            }

            throw;
        }

        var data = cam.GetLastBlobData();
        if (data is null || data.Length == 0)
        {
            throw new GuideCameraException("guide exposure returned no data");
        }

        var frame = FitsReader.Read(data, cam.GetLastBlobFormat());
        frame.ExposureMs = request.ExposureMs;
        frame.StartTime = started;
        frame.Binning = bin;
        if (frame.BitsPerPixel != 8)
        {
            frame.BitsPerPixel = Math.Clamp(cam.BitDepth, 8, 16);
        }

        return frame;
    }

    public Task AbortAsync()
    {
        try
        {
            camera?.AbortExposure();
        }
        catch (Exception ex)
        {
            Logger.Debug($"NativeGuider: abort failed: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public async Task ReconnectAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var old = camera;
            camera = null;
            if (old is not null)
            {
                try
                {
                    await old.DisconnectAsync().ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                old.Dispose();
            }

            await ConnectCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        var old = camera;
        camera = null;
        if (old is null)
        {
            return;
        }

        try
        {
            old.AbortExposure();
        }
        catch
        {
            // ignore
        }

        try
        {
            await old.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Debug($"NativeGuider: guide camera disconnect: {ex.Message}");
        }

        old.Dispose();
    }

    public void Dispose()
    {
        camera?.Dispose();
        camera = null;
        gate.Dispose();
    }
}
