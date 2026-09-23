// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using NINA.Astrometry;
using NINA.Core.Interfaces;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using PinsGuider.Engine.Calibration;
using PinsGuider.Engine.Core;
using PinsGuider.Engine.Guiding;
using PinsGuider.Engine.Logging;
using PinsGuider.Engine.Simulation;
using PinsGuider.Engine.Stats;
using PinsGuider.Plugin.Hardware;

namespace PinsGuider.Plugin;

/// <summary>
/// NINA guider device hosting the native guiding engine. Implements <see cref="IGuider"/> for the sequencer/ninaAPI and
/// <see cref="IAdvancedGuider"/> for rich UIs (Touch-N-Stars). Public primitive properties double as settings that
/// ninaAPI's generic guider get-settings/set-setting endpoints can read and write.
/// </summary>
public sealed class NativeGuider : BaseINPC, IAdvancedGuider
{
    public const string DeviceId = "PinsNativeGuider";
    private const int MaxSteps = 2000;
    private const int MaxAlerts = 300;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IProfileService profileService;
    private readonly ITelescopeMediator telescopeMediator;
    private readonly ICameraMediator cameraMediator;
    private readonly NativeGuiderOptions options;
    private readonly object sync = new();
    private readonly LinkedList<AdvancedGuideStep> steps = new();
    private readonly LinkedList<AdvancedGuiderAlert> alerts = new();
    private readonly SemaphoreSlim connectLock = new(1, 1);

    private Guider? guider;
    private IndiGuideCamera? indiCamera;
    private Simulator? simulator;
    private IMountState? mountState;
    private GuidingLog? guideLog;
    private StreamWriter? guideLogWriter;
    private GuideLogBridge? guideLogBridge;
    private bool connected;
    private double pixelScale = 1.0;
    private GuideFrame? latestFrame;
    private IReadOnlyList<StarInfo> latestStars = [];
    private GuidePoint latestLock = GuidePoint.Invalid;
    private GuideStepEvent? lastStep;
    private string calibrationStep = string.Empty;
    private double calibrationProgress;
    private string settleStatus = string.Empty;
    private AdvancedGuiderAlert? lastError;
    private IProgress<ApplicationStatus>? progress;
    private TaskCompletionSource<bool>? autoSelectTcs;
    private ICameraSource? cameraSource;
    private string darkLibrary = string.Empty;
    private volatile bool buildingDarks;

    public NativeGuider(IProfileService profileService, ITelescopeMediator telescopeMediator, ICameraMediator cameraMediator, Guid pluginId)
    {
        this.profileService = profileService;
        this.telescopeMediator = telescopeMediator;
        this.cameraMediator = cameraMediator;
        options = new NativeGuiderOptions(profileService, pluginId);
        profileService.ProfileChanged += (_, _) => ApplySettingsIfConnected();
    }

    public event EventHandler<IGuideStep>? GuideEvent;

    public event EventHandler<AdvancedGuiderEventArgs>? AdvancedGuiderEvent;

    #region IDevice

    public bool HasSetupDialog => false;

    public string Id => DeviceId;

    public string Name => "PINS Native Guider";

    public string DisplayName => Name;

    public string Category => "Guiders";

    public bool Connected
    {
        get => connected;
        private set
        {
            connected = value;
            RaisePropertyChanged();
        }
    }

    public string Description => "Native multi-star autoguider (PHD2-compatible algorithms) running inside PINS.";

    public string DriverInfo => "PINS Native Guider";

    public string DriverVersion => typeof(NativeGuider).Assembly.GetName().Version?.ToString() ?? "0.1";

    public IList<string> SupportedActions => [];

    public string Action(string actionName, string actionParameters) => string.Empty;

    public string SendCommandString(string command, bool raw = true) => string.Empty;

    public bool SendCommandBool(string command, bool raw = true) => false;

    public void SendCommandBlind(string command, bool raw = true)
    {
    }

    public void SetupDialog()
    {
    }

    public async Task<bool> Connect(CancellationToken token)
    {
        await connectLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (Connected)
            {
                return true;
            }

            await ConnectCoreAsync(token).ConfigureAwait(false);
            Connected = true;
            RaisePropertyChanged(nameof(State));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            Notification.ShowError($"Native guider: {ex.Message}");
            await CleanupAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            connectLock.Release();
        }
    }

    public void Disconnect()
    {
        try
        {
            Task.Run(CleanupAsync).Wait(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }

        Connected = false;
        RaisePropertyChanged(nameof(State));
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        var settings = options.ToEngineSettings();
        ICameraSource camera;
        IPulseOutput output;
        if (options.IsSimulator)
        {
            simulator = new Simulator(SimulatorScenario.GoodMount, SystemClock.Instance);
            camera = simulator.Camera;
            output = simulator.Mount;
            mountState = simulator.Mount;
            settings = settings with
            {
                FocalLengthMm = SimulatorScenario.GoodMount.Camera.FocalLengthMm,
                PixelSizeUm = SimulatorScenario.GoodMount.Camera.PixelSizeUm,
            };
            Logger.Info("NativeGuider: using the built-in simulator");
        }
        else
        {
            indiCamera = new IndiGuideCamera(options.GuideCameraDriver, options.GuideCameraDevice, MainCameraName);
            await indiCamera.ConnectAsync(ct).ConfigureAwait(false);
            camera = indiCamera;
            var cam = indiCamera;
            output = options.UseCameraSt4 ? new CameraSt4PulseOutput(() => cam.DeviceId) : new MountPulseOutput(telescopeMediator);
            mountState = new NinaMountState(telescopeMediator, () => options.GetBool("PauseWhenSlewing"), () => options.GetBool("PauseWhenTrackingOff"));
            if (settings.FocalLengthMm <= 0)
            {
                Notification.ShowWarning("Native guider: guide focal length is not set — arcsec values and calibration step use defaults. Set FocalLengthMm.");
            }
        }

        var g = new Guider(camera, output, mountState, SystemClock.Instance, settings);
        g.EventRaised += OnEngineEvent;
        guider = g;
        cameraSource = camera;
        PixelScale = g.PixelScale;
        LoadDarks();

        if (options.ReuseCalibration && LoadStoredCalibration() is { } cal)
        {
            g.LoadCalibration(cal);
            Logger.Info("NativeGuider: restored calibration from a previous session");
        }

        if (options.SaveGuideLog)
        {
            OpenGuideLog(camera);
        }
    }

    private async Task CleanupAsync()
    {
        var g = guider;
        guider = null;
        if (g is not null)
        {
            try
            {
                await g.StopCaptureAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Debug($"NativeGuider: stop capture: {ex.Message}");
            }

            g.EventRaised -= OnEngineEvent;
        }

        CloseGuideLog();
        var cam = indiCamera;
        indiCamera = null;
        if (cam is not null)
        {
            await cam.DisconnectAsync().ConfigureAwait(false);
            cam.Dispose();
        }

        simulator = null;
        mountState = null;
        cameraSource = null;
        darkLibrary = string.Empty;
        autoSelectTcs?.TrySetResult(false);
    }

    #endregion

    #region IGuider

    public double PixelScale
    {
        get => pixelScale;
        set
        {
            pixelScale = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>PHD2-compatible state name (Stopped, Looping, Selected, Calibrating, Guiding, LostLock, Paused).</summary>
    public string State => guider?.State.ToPhd2AppState() ?? "Stopped";

    public bool CanClearCalibration => true;

    public bool CanSetShiftRate => false;

    public bool ShiftEnabled => false;

    public bool CanGetLockPosition => true;

    public SiderealShiftTrackingRate ShiftRate => SiderealShiftTrackingRate.Disabled;

    public async Task<bool> AutoSelectGuideStar()
    {
        var g = guider;
        if (g is null)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        autoSelectTcs = tcs;
        g.AutoSelectStar();
        var timeout = TimeSpan.FromSeconds(g.Settings.ExposureMs / 1000.0 * (g.Settings.AutoSelectAttempts + 1) + 20);
        try
        {
            return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async Task<bool> StartGuiding(bool forceCalibration, IProgress<ApplicationStatus> progress, CancellationToken ct)
    {
        var g = guider;
        if (g is null || !Connected)
        {
            Notification.ShowError("Native guider is not connected");
            return false;
        }

        if (buildingDarks)
        {
            Notification.ShowError("Native guider: a dark library is being built — wait until it finishes");
            return false;
        }

        if (!options.IsSimulator && !options.UseCameraSt4)
        {
            var info = telescopeMediator.GetInfo();
            if (info is null || !info.Connected)
            {
                Notification.ShowError("Native guider: connect the mount before starting guiding");
                return false;
            }

            if (!info.CanPulseGuide)
            {
                Notification.ShowError("Native guider: the mount driver does not support pulse guiding (use the camera ST4 guide output instead)");
                return false;
            }
        }

        this.progress = progress;
        try
        {
            var result = await g.StartGuidingAsync(SettleFromProfile(), forceCalibration, ct).ConfigureAwait(false);
            if (result.Success)
            {
                return true;
            }

            if (g.State is GuiderState.Guiding && result.Code == GuideErrorCode.SettleTimeout)
            {
                Notification.ShowWarning("Native guider: guiding started but did not settle within the timeout");
                return true;
            }

            Notification.ShowError($"Native guider: guiding could not be started — {result.Error}");
            return false;
        }
        catch (OperationCanceledException)
        {
            await g.StopCaptureAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            this.progress = null;
            progress?.Report(new ApplicationStatus { Source = Name, Status = string.Empty });
        }
    }

    public async Task<bool> StopGuiding(CancellationToken ct)
    {
        var g = guider;
        if (g is null)
        {
            return false;
        }

        await g.StopCaptureAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> Dither(IProgress<ApplicationStatus> progress, CancellationToken ct)
    {
        var gs = profileService.ActiveProfile.GuiderSettings;
        return await DitherCoreAsync(gs.DitherPixels, gs.DitherRAOnly, progress, ct).ConfigureAwait(false);
    }

    public Task<bool> ClearCalibration(CancellationToken ct)
    {
        guider?.ClearCalibration();
        try
        {
            var store = LoadStore();
            if (CalibrationKeyNow() is { } key && store.Remove(key))
            {
                SaveStore(store);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }

        return Task.FromResult(true);
    }

    public Task<bool> SetShiftRate(SiderealShiftTrackingRate shiftTrackingRate, CancellationToken ct) => Task.FromResult(false);

    public Task<bool> StopShifting(CancellationToken ct) => Task.FromResult(false);

    public Task<LockPosition> GetLockPosition()
    {
        var p = guider?.LockPosition ?? GuidePoint.Invalid;
        return Task.FromResult(p.IsValid ? new LockPosition((float)p.X, (float)p.Y) : null!);
    }

    #endregion

    #region IAdvancedGuider

    public AdvancedGuiderStatus GetStatus()
    {
        var g = guider;
        lock (sync)
        {
            var stats = g?.Statistics;
            var lockPos = g?.LockPosition ?? GuidePoint.Invalid;
            var primary = latestStars.FirstOrDefault(s => s.IsPrimary);
            return new AdvancedGuiderStatus
            {
                State = g?.State.ToString() ?? "Stopped",
                Connected = Connected,
                IsSettling = g?.IsSettling ?? false,
                IsCalibrated = g?.Calibration is not null,
                CameraName = indiCamera?.Name ?? (simulator is not null ? "Simulator" : options.GuideCameraDevice),
                ExposureSeconds = (g?.Settings.ExposureMs ?? options.ToEngineSettings().ExposureMs) / 1000.0,
                PixelScale = PixelScale,
                FrameNumber = latestFrame?.FrameNumber ?? 0,
                LastProcessingMs = lastStep?.ProcessingMs ?? 0,
                LockX = lockPos.IsValid ? lockPos.X : null,
                LockY = lockPos.IsValid ? lockPos.Y : null,
                PrimaryStar = primary is null ? null : ToDto(primary),
                StarsUsed = lastStep?.StarsUsed ?? (primary is null ? 0 : 1),
                StarCount = latestStars.Count,
                CalibrationStep = calibrationStep,
                CalibrationProgress = calibrationProgress,
                WindowStats = stats is null ? null : ToDto(stats.Window, stats),
                SessionStats = stats is null ? null : ToDto(stats.Session, stats),
                LastError = lastError,
                SettleStatus = settleStatus,
                DarkLibrary = darkLibrary,
            };
        }
    }

    public IReadOnlyList<AdvancedGuideStep> GetRecentSteps(int maxCount)
    {
        lock (sync)
        {
            return steps.Skip(Math.Max(0, steps.Count - Math.Max(0, maxCount))).ToList();
        }
    }

    public IReadOnlyList<AdvancedGuiderAlert> GetRecentAlerts(int maxCount)
    {
        lock (sync)
        {
            return alerts.Skip(Math.Max(0, alerts.Count - Math.Max(0, maxCount))).ToList();
        }
    }

    public AdvancedGuiderFrame GetLatestFrame()
    {
        lock (sync)
        {
            var f = latestFrame;
            if (f is null)
            {
                return null!;
            }

            return new AdvancedGuiderFrame
            {
                FrameNumber = f.FrameNumber,
                Timestamp = (f.StartTime == default ? DateTimeOffset.UtcNow : f.StartTime).UtcDateTime,
                Width = f.Width,
                Height = f.Height,
                BitDepth = f.BitsPerPixel,
                Pixels = f.Pixels,
                LockX = latestLock.IsValid ? latestLock.X : null,
                LockY = latestLock.IsValid ? latestLock.Y : null,
                Stars = latestStars.Select(ToDto).ToList(),
            };
        }
    }

    public AdvancedGuiderCalibration GetCalibration()
    {
        var cal = guider?.Calibration;
        if (cal is null)
        {
            return null!;
        }

        double scale = PixelScale;
        return new AdvancedGuiderCalibration
        {
            Timestamp = cal.Timestamp.UtcDateTime,
            RaAngleDeg = cal.XAngle * 180.0 / Math.PI,
            DecAngleDeg = cal.YAngle * 180.0 / Math.PI,
            RaRatePxPerSec = cal.XRate * 1000.0,
            DecRatePxPerSec = cal.YRate * 1000.0,
            RaRateArcsecPerSec = cal.XRate * 1000.0 * scale,
            DecRateArcsecPerSec = cal.YRate * 1000.0 * scale,
            OrthogonalityErrorDeg = cal.YRate > 0 ? MountTransform.OrthogonalityErrorDegrees(cal.XAngle, cal.YAngle) : 0,
            DeclinationDeg = cal.Declination is { } d ? d * 180.0 / Math.PI : null,
            PierSide = cal.PierSide.ToString(),
            Binning = cal.Binning,
            RaSteps = cal.RaStepCount,
            DecSteps = cal.DecStepCount,
            LastIssue = cal.LastIssue.ToString(),
            DecFlipRequired = options.GetBool("DecFlipRequired"),
        };
    }

    public IReadOnlyList<AdvancedGuiderSetting> GetSettings() => options.Describe();

    public bool TrySetSetting(string name, string value, out string error)
    {
        if (!options.TrySet(name, value, out error))
        {
            return false;
        }

        var def = NativeGuiderOptions.Find(name);
        ApplySettingsIfConnected();
        if (def?.Name is "UseDarkLibrary" or "Binning")
        {
            LoadDarks();
        }
        if (def?.RequiresReconnect == true && Connected)
        {
            error = "saved; reconnect the guider to apply";
        }

        RaisePropertyChanged(def?.Name ?? name);
        return true;
    }

    public async Task<IReadOnlyList<string>> GetAvailableGuideCameras(CancellationToken ct)
    {
        if (options.IsSimulator)
        {
            return ["Simulator"];
        }

        try
        {
            return await IndiGuideCamera.ListDevicesAsync(options.GuideCameraDriver, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return [];
        }
    }

    public Task<bool> StartLooping(CancellationToken ct)
    {
        var g = guider;
        if (g is null)
        {
            return Task.FromResult(false);
        }

        g.StartLooping();
        return Task.FromResult(true);
    }

    public async Task<bool> StopLooping(CancellationToken ct) => await StopGuiding(ct).ConfigureAwait(false);

    public Task<bool> SetPaused(bool paused, CancellationToken ct)
    {
        var g = guider;
        if (g is null)
        {
            return Task.FromResult(false);
        }

        if (paused)
        {
            g.Pause();
        }
        else
        {
            g.Resume();
        }

        return Task.FromResult(true);
    }

    public Task<bool> DitherBy(double pixels, bool raOnly, CancellationToken ct) => DitherCoreAsync(pixels, raOnly, null, ct);

    /// <summary>PHD2's standard dark exposure steps (seconds).</summary>
    private static readonly double[] DarkExposures = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5, 6, 7, 8, 9, 10, 15, 20, 30];

    public async Task<bool> BuildDarkLibrary(double minExposureSeconds, double maxExposureSeconds, int framesPerExposure, CancellationToken ct)
    {
        var g = guider;
        var cam = cameraSource;
        if (g is null || cam is null || simulator is not null)
        {
            return false;
        }

        if (g.State.IsCapturing() || buildingDarks)
        {
            Notification.ShowError("Native guider: stop guiding/looping before building a dark library");
            return false;
        }

        var exposures = DarkExposures.Where(e => e >= minExposureSeconds - 1e-9 && e <= maxExposureSeconds + 1e-9).ToList();
        if (exposures.Count == 0)
        {
            exposures.Add(Math.Max(0.01, minExposureSeconds));
        }

        framesPerExposure = Math.Clamp(framesPerExposure, 1, 50);
        buildingDarks = true;
        int total = exposures.Count * framesPerExposure;
        int index = 0;
        try
        {
            var s = g.Settings;
            var masters = new List<GuideFrame>();
            foreach (var e in exposures)
            {
                var builder = new Engine.Imaging.MasterDarkBuilder();
                for (int i = 0; i < framesPerExposure; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Raise("darks", DateTimeOffset.UtcNow, new { status = "capturing", exposure = e, frame = i + 1, framesPerExposure, index = ++index, total });
                    var frame = await cam.CaptureAsync(new CaptureRequest(e * 1000.0, s.Binning, default, s.Gain, s.Offset), ct).ConfigureAwait(false);
                    builder.Add(frame);
                }

                var master = builder.Build();
                master.ExposureMs = e * 1000.0;
                master.Binning = s.Binning;
                masters.Add(master);
            }

            var path = DarkLibraryFiles.NativePath(cam.Name, cam.SensorWidth, cam.SensorHeight, s.Binning);
            DarkLibraryFiles.Save(path, masters, s.Gain);
            LoadDarks();
            Raise("darks", DateTimeOffset.UtcNow, new { status = "done", total, message = darkLibrary });
            Notification.ShowSuccess($"Native guider: dark library built ({masters.Count} exposures)");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            Raise("darks", DateTimeOffset.UtcNow, new { status = "failed", message = ex.Message });
            Notification.ShowError($"Native guider: dark library failed — {ex.Message}");
            return false;
        }
        finally
        {
            buildingDarks = false;
        }
    }

    private void LoadDarks()
    {
        var g = guider;
        var cam = cameraSource;
        if (g is null || cam is null)
        {
            return;
        }

        if (!options.UseDarkLibrary || simulator is not null)
        {
            g.Preprocessor.Darks = null;
            darkLibrary = string.Empty;
            return;
        }

        try
        {
            var loaded = DarkLibraryFiles.Load(cam.Name, cam.SensorWidth, cam.SensorHeight, g.Settings.Binning);
            g.Preprocessor.Darks = loaded?.Library;
            darkLibrary = loaded is { } l ? $"{Path.GetFileName(l.Source)} ({l.Library.Count} darks)" : string.Empty;
            if (loaded is null)
            {
                Logger.Info("NativeGuider: no dark library found for the guide camera");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"NativeGuider: dark library not loaded: {ex.Message}");
        }
    }

    #endregion

    #region settings as properties (ninaAPI guider get-settings / set-setting)

    public string GuideCameraDriver { get => options.GuideCameraDriver; set => Set(nameof(GuideCameraDriver), value); }

    public string GuideCameraDevice { get => options.GuideCameraDevice; set => Set(nameof(GuideCameraDevice), value); }

    public double FocalLengthMm { get => options.FocalLengthMm; set => Set(nameof(FocalLengthMm), value.ToString(Inv)); }

    public double ExposureSeconds { get => options.GetDouble(nameof(ExposureSeconds)); set => Set(nameof(ExposureSeconds), value.ToString(Inv)); }

    public int Gain { get => options.GetInt(nameof(Gain)); set => Set(nameof(Gain), value.ToString(Inv)); }

    public int Binning { get => options.GetInt(nameof(Binning)); set => Set(nameof(Binning), value.ToString(Inv)); }

    public string PulseOutput { get => options.GetString(nameof(PulseOutput)); set => Set(nameof(PulseOutput), value); }

    public string DecGuideMode { get => options.GetString(nameof(DecGuideMode)); set => Set(nameof(DecGuideMode), value); }

    public bool MultiStar { get => options.GetBool(nameof(MultiStar)); set => Set(nameof(MultiStar), value ? "true" : "false"); }

    public int MaxStars { get => options.GetInt(nameof(MaxStars)); set => Set(nameof(MaxStars), value.ToString(Inv)); }

    public double MinSnr { get => options.GetDouble(nameof(MinSnr)); set => Set(nameof(MinSnr), value.ToString(Inv)); }

    public double RaAggression { get => options.GetDouble(nameof(RaAggression)); set => Set(nameof(RaAggression), value.ToString(Inv)); }

    public double DecAggression { get => options.GetDouble(nameof(DecAggression)); set => Set(nameof(DecAggression), value.ToString(Inv)); }

    public int CalibrationStepMs { get => options.GetInt(nameof(CalibrationStepMs)); set => Set(nameof(CalibrationStepMs), value.ToString(Inv)); }

    public bool DecFlipRequired { get => options.GetBool(nameof(DecFlipRequired)); set => Set(nameof(DecFlipRequired), value ? "true" : "false"); }

    /// <summary>Write-mostly generic setter: "Name=Value" for any native guider setting (see GetSettings()).</summary>
    public string NativeSetting
    {
        get => string.Empty;
        set
        {
            int eq = value?.IndexOf('=') ?? -1;
            if (eq > 0)
            {
                Set(value![..eq].Trim(), value[(eq + 1)..].Trim());
            }
        }
    }

    private void Set(string name, string value)
    {
        if (!TrySetSetting(name, value, out var error) && !string.IsNullOrEmpty(error))
        {
            Notification.ShowWarning($"Native guider: {error}");
            throw new ArgumentException(error);
        }
    }

    private void ApplySettingsIfConnected()
    {
        var g = guider;
        if (g is null)
        {
            return;
        }

        try
        {
            var s = options.ToEngineSettings();
            if (simulator is not null)
            {
                s = s with { FocalLengthMm = SimulatorScenario.GoodMount.Camera.FocalLengthMm, PixelSizeUm = SimulatorScenario.GoodMount.Camera.PixelSizeUm };
            }

            g.UpdateSettings(s);
            PixelScale = s.PixelScale(indiCamera?.PixelSizeUm ?? simulator?.Camera.PixelSizeUm ?? 0);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    #endregion

    #region engine events

    private void OnEngineEvent(object? sender, GuiderEvent e)
    {
        try
        {
            switch (e)
            {
                case GuideStepEvent s:
                    OnGuideStep(s);
                    break;
                case AppStateEvent st:
                    if (st.State is not GuiderState.Calibrating)
                    {
                        calibrationStep = string.Empty;
                        calibrationProgress = 0;
                    }

                    RaisePropertyChanged(nameof(State));
                    Raise("state", e.Timestamp, GetStatus());
                    break;
                case AlertEvent a:
                    OnAlert(a);
                    break;
                case StarSelectedEvent:
                    autoSelectTcs?.TrySetResult(true);
                    break;
                case StartCalibrationEvent:
                    calibrationStep = "Starting calibration";
                    calibrationProgress = 0;
                    Report("Calibrating");
                    break;
                case CalibratingEvent c:
                    calibrationStep = string.IsNullOrEmpty(c.State) ? $"{c.Direction} step {c.Step}" : c.State;
                    calibrationProgress = c.Progress;
                    Report($"Calibrating: {calibrationStep}");
                    Raise("calibration", e.Timestamp, new { step = calibrationStep, progress = c.Progress, direction = c.Direction, dx = c.Dx, dy = c.Dy, distance = c.Distance });
                    break;
                case CalibrationCompleteEvent:
                    calibrationStep = "Calibration complete";
                    calibrationProgress = 1;
                    Raise("calibration", e.Timestamp, GetCalibration());
                    break;
                case CalibrationFailedEvent cf:
                    calibrationStep = $"Calibration failed: {cf.Reason}";
                    Raise("calibration", e.Timestamp, new { step = calibrationStep, failed = true, reason = cf.Reason });
                    break;
                case CalibrationUpdatedEvent cu:
                    PersistCalibration(cu);
                    break;
                case SettleBeginEvent:
                    settleStatus = "Settling";
                    Report("Settling");
                    Raise("settle", e.Timestamp, new { status = "begin" });
                    break;
                case SettlingEvent se:
                    settleStatus = $"Settling: {se.Distance:F2} px, {se.Time:F0}/{se.SettleTime:F0} s";
                    Report(settleStatus);
                    Raise("settle", e.Timestamp, new { status = "settling", distance = se.Distance, time = se.Time, settleTime = se.SettleTime, starLocked = se.StarLocked });
                    break;
                case SettleDoneEvent sd:
                    settleStatus = sd.Succeeded ? "Settled" : $"Settle failed: {sd.Error}";
                    Raise("settle", e.Timestamp, new { status = sd.Succeeded ? "done" : "failed", error = sd.Error, totalFrames = sd.TotalFrames, droppedFrames = sd.DroppedFrames });
                    break;
                case GuidingDitheredEvent gd:
                    Raise("dither", e.Timestamp, new { dx = gd.Dx, dy = gd.Dy });
                    break;
                case StarLostEvent sl:
                    Raise("starlost", e.Timestamp, new { frame = sl.Frame, snr = sl.Snr, mass = sl.StarMass, status = sl.Status });
                    break;
                case FrameReadyEvent fr:
                    lock (sync)
                    {
                        latestFrame = fr.Frame;
                        latestStars = fr.Stars;
                        latestLock = fr.LockPosition;
                    }

                    Raise("frame", e.Timestamp, new { frameNumber = fr.Frame.FrameNumber, width = fr.Frame.Width, height = fr.Frame.Height, stars = fr.Stars.Count });
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private void OnGuideStep(GuideStepEvent s)
    {
        double scale = s.PixelScale > 0 ? s.PixelScale : PixelScale;
        var dto = new AdvancedGuideStep
        {
            Frame = s.Frame,
            Timestamp = s.Timestamp.UtcDateTime,
            Time = s.Time,
            Dx = s.Dx,
            Dy = s.Dy,
            RaDistanceRaw = s.RaDistanceRaw,
            DecDistanceRaw = s.DecDistanceRaw,
            RaArcsec = s.RaDistanceRaw * scale,
            DecArcsec = s.DecDistanceRaw * scale,
            RaDuration = s.RaDuration,
            RaDirection = s.RaDirection?.ToString() ?? string.Empty,
            DecDuration = s.DecDuration,
            DecDirection = s.DecDirection?.ToString() ?? string.Empty,
            RaLimited = s.RaLimited,
            DecLimited = s.DecLimited,
            Snr = s.Snr,
            StarMass = s.StarMass,
            Hfd = s.Hfd,
            StarsUsed = s.StarsUsed,
            IsSettling = s.IsSettling,
            IsRecenterMove = s.IsRecenterMove,
            PrimaryEstimated = s.PrimaryEstimated,
            AvgDist = s.AvgDist,
        };
        lock (sync)
        {
            lastStep = s;
            steps.AddLast(dto);
            while (steps.Count > MaxSteps)
            {
                steps.RemoveFirst();
            }
        }

        // NINA/ninaAPI guide graph (PHD2 conventions: px; RA duration negative for East, Dec negative for South)
        var step = new NativeGuideStep
        {
            Frame = s.Frame,
            Time = s.Time,
            RADistanceRaw = s.RaDistanceRaw,
            DECDistanceRaw = s.DecDistanceRaw,
            RADuration = s.RaDirection == GuideDirection.East ? -s.RaDuration : s.RaDuration,
            DECDuration = s.DecDirection == GuideDirection.South ? -s.DecDuration : s.DecDuration,
            Event = "GuideStep",
            TimeStamp = s.Timestamp.ToUnixTimeMilliseconds().ToString(Inv),
            Host = Environment.MachineName,
            Inst = 1,
        };
        try
        {
            GuideEvent?.Invoke(this, step);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }

        Raise("step", s.Timestamp, dto);
        if (guider?.Statistics is { } stats)
        {
            Raise("stats", s.Timestamp, new { window = ToDto(stats.Window, stats), session = ToDto(stats.Session, stats) });
        }
    }

    private void OnAlert(AlertEvent a)
    {
        var info = GuideErrorCatalog.Get(a.Code);
        var dto = new AdvancedGuiderAlert
        {
            Timestamp = a.Timestamp.UtcDateTime,
            Code = (int)a.Code,
            CodeName = a.Code.ToString(),
            Severity = a.Severity.ToString(),
            Title = info.Title,
            Explanation = info.Explanation,
            Fix = info.Fix,
            Detail = a.Detail ?? string.Empty,
        };
        lock (sync)
        {
            alerts.AddLast(dto);
            while (alerts.Count > MaxAlerts)
            {
                alerts.RemoveFirst();
            }

            if (a.Severity != GuideErrorSeverity.Info)
            {
                lastError = dto;
            }
        }

        string text = $"Native guider: {info.Title}{(string.IsNullOrEmpty(a.Detail) ? string.Empty : $" ({a.Detail})")}. {info.Fix}";
        switch (a.Severity)
        {
            case GuideErrorSeverity.Critical:
                Logger.Error(text);
                Notification.ShowError(text);
                break;
            case GuideErrorSeverity.Warning:
                Logger.Warning(text);
                if (a.Code is GuideErrorCode.StarLost or GuideErrorCode.CalibrationNotOrthogonal or GuideErrorCode.CalibrationFewSteps
                    or GuideErrorCode.CalibrationRateRatio or GuideErrorCode.DecFlipCorrected or GuideErrorCode.PulseLimitReached
                    or GuideErrorCode.NoStarFound or GuideErrorCode.SettleTimeout)
                {
                    Notification.ShowWarning(text);
                }

                break;
            default:
                Logger.Info(text);
                break;
        }

        if (a.Code == GuideErrorCode.NoStarFound)
        {
            autoSelectTcs?.TrySetResult(false);
        }

        Raise("alert", a.Timestamp, dto);
    }

    private void Raise(string type, DateTimeOffset ts, object? payload)
    {
        var h = AdvancedGuiderEvent;
        if (h is null)
        {
            return;
        }

        try
        {
            h(this, new AdvancedGuiderEventArgs { Type = type, Timestamp = ts.UtcDateTime, Payload = payload! });
        }
        catch (Exception ex)
        {
            Logger.Debug($"NativeGuider: event subscriber failed: {ex.Message}");
        }
    }

    private void Report(string status)
    {
        try
        {
            progress?.Report(new ApplicationStatus { Source = Name, Status = status });
        }
        catch
        {
            // ignore
        }
    }

    #endregion

    #region helpers

    private async Task<bool> DitherCoreAsync(double pixels, bool raOnly, IProgress<ApplicationStatus>? progress, CancellationToken ct)
    {
        var g = guider;
        if (g is null || g.State is not (GuiderState.Guiding or GuiderState.LostLock or GuiderState.Reacquiring))
        {
            Logger.Warning("NativeGuider: dither requested while not guiding");
            return false;
        }

        this.progress = progress;
        try
        {
            var r = await g.DitherAsync(pixels, raOnly, SettleFromProfile(), ct).ConfigureAwait(false);
            if (r.Success)
            {
                return true;
            }

            if (r.Code == GuideErrorCode.SettleTimeout && g.State == GuiderState.Guiding)
            {
                return true;
            }

            Logger.Warning($"NativeGuider: dither failed: {r.Error}");
            return false;
        }
        finally
        {
            this.progress = null;
        }
    }

    private SettleParams SettleFromProfile()
    {
        var gs = profileService.ActiveProfile.GuiderSettings;
        double pixels = gs.SettlePixels > 0 ? gs.SettlePixels : 1.5;
        double time = gs.SettleTime > 0 ? gs.SettleTime : 10;
        double timeout = gs.SettleTimeout > 0 ? gs.SettleTimeout : 40;
        return new SettleParams(pixels, time, timeout);
    }

    private string? MainCameraName()
    {
        try
        {
            var info = cameraMediator.GetInfo();
            return info is { Connected: true } ? info.DeviceId : null;
        }
        catch
        {
            return null;
        }
    }

    private static string StorePath => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NativeGuider", "calibrations.json");

    private static CalibrationStore LoadStore()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                return CalibrationStore.FromJson(File.ReadAllText(StorePath));
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"NativeGuider: could not read calibrations: {ex.Message}");
        }

        return new CalibrationStore();
    }

    private static void SaveStore(CalibrationStore store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var tmp = StorePath + ".tmp";
        File.WriteAllText(tmp, store.ToJson());
        File.Move(tmp, StorePath, overwrite: true);
    }

    private CalibrationKey? CalibrationKeyNow()
    {
        var g = guider;
        string profileId = profileService.ActiveProfile.Id.ToString();
        string cameraName = indiCamera?.Name ?? (simulator is not null ? "Simulator" : options.GuideCameraDevice);
        string mountName = simulator is not null ? "Simulator" : options.UseCameraSt4 ? "ST4" : telescopeMediator.GetInfo()?.Name ?? "Mount";
        if (string.IsNullOrEmpty(cameraName))
        {
            return null;
        }

        var s = g?.Settings ?? options.ToEngineSettings();
        return new CalibrationKey(profileId, cameraName, mountName, s.Binning, s.FocalLengthMm);
    }

    private CalibrationData? LoadStoredCalibration()
    {
        try
        {
            var key = CalibrationKeyNow();
            var g = guider;
            if (key is null || g is null)
            {
                return null;
            }

            var optics = new GuideOptics(g.Settings.FocalLengthMm, g.Settings.PixelSizeUm > 0 ? g.Settings.PixelSizeUm : indiCamera?.PixelSizeUm ?? 0, g.Settings.Binning);
            var lookup = LoadStore().Lookup(key, optics);
            if (lookup.Validity == CalibrationValidity.Valid && lookup.Calibration is { } cal)
            {
                return cal;
            }

            if (lookup.Validity != CalibrationValidity.NotFound)
            {
                Logger.Info($"NativeGuider: stored calibration not used ({lookup.Validity})");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"NativeGuider: could not restore calibration: {ex.Message}");
        }

        return null;
    }

    private void PersistCalibration(CalibrationUpdatedEvent cu)
    {
        try
        {
            if (options.GetBool("DecFlipRequired") != cu.DecFlipRequired)
            {
                options.TrySet("DecFlipRequired", cu.DecFlipRequired ? "true" : "false", out _);
            }

            if (!options.ReuseCalibration || CalibrationKeyNow() is not { } key)
            {
                return;
            }

            var store = LoadStore();
            store.Set(key, cu.Calibration);
            SaveStore(store);
        }
        catch (Exception ex)
        {
            Logger.Warning($"NativeGuider: could not save calibration: {ex.Message}");
        }
    }

    private void OpenGuideLog(ICameraSource camera)
    {
        try
        {
            var dir = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "NativeGuider", "Logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, GuidingLog.FileName(DateTime.Now));
            guideLogWriter = new StreamWriter(path, append: true) { AutoFlush = true };
            guideLog = new GuidingLog(guideLogWriter, SystemClock.Instance, new GuidingLogOptions { AppVersion = "PINS Native Guider " + DriverVersion });
            guideLog.EnableLogging();
            var ms = mountState!;
            guideLogBridge = new GuideLogBridge(guider!, guideLog, ms, () => new GuideLogContext
            {
                EquipmentProfile = profileService.ActiveProfile.Name,
                CameraName = camera.Name,
                MountName = telescopeMediator.GetInfo()?.Name ?? string.Empty,
                SensorWidth = camera.SensorWidth,
                SensorHeight = camera.SensorHeight,
                PixelSizeUm = camera.PixelSizeUm,
            });
            Logger.Info($"NativeGuider: guide log {path}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"NativeGuider: could not open guide log: {ex.Message}");
            CloseGuideLog();
        }
    }

    private void CloseGuideLog()
    {
        try
        {
            guideLogBridge?.Dispose();
            guideLog?.Close();
            guideLogWriter?.Dispose();
        }
        catch
        {
            // ignore
        }

        guideLogBridge = null;
        guideLog = null;
        guideLogWriter = null;
    }

    private static AdvancedGuideStar ToDto(StarInfo s) => new()
    {
        X = s.X,
        Y = s.Y,
        Snr = s.Snr,
        Mass = s.Mass,
        Hfd = s.Hfd,
        IsPrimary = s.IsPrimary,
        Used = s.Used,
        Weight = s.Weight,
        RejectReason = s.RejectReason!,
    };

    private static AdvancedGuiderStats ToDto(GuidingStatsBlock b, GuidingStatsSnapshot snap) => new()
    {
        Frames = b.IncludedFrames,
        RmsRaArcsec = b.RmsRaArcsec,
        RmsDecArcsec = b.RmsDecArcsec,
        RmsTotalArcsec = b.RmsTotalArcsec,
        RmsRaPx = b.RmsRaPx,
        RmsDecPx = b.RmsDecPx,
        RmsTotalPx = b.RmsTotalPx,
        PeakRaArcsec = b.PeakRaArcsec,
        PeakDecArcsec = b.PeakDecArcsec,
        DriftRaArcsecPerMin = b.RaDriftArcsecPerMin,
        DriftDecArcsecPerMin = b.DecDriftArcsecPerMin,
        PolarAlignmentErrorArcmin = b.PolarAlignmentErrorArcmin,
        OscillationIndex = b.OscillationIndex,
        RaDutyPercent = b.RaDutyPercent,
        DecDutyPercent = b.DecDutyPercent,
        SnrMin = b.SnrMin,
        SnrAvg = b.SnrAvg,
        SnrLast = b.SnrLast,
        AvgStarCount = b.StarCountAvg,
        StarLostCount = snap.StarLostCount,
        ElapsedSeconds = snap.Elapsed.TotalSeconds,
    };

    #endregion
}

/// <summary>NINA guide step (PHD2 conventions) for GuiderVM / ninaAPI graphs.</summary>
public sealed class NativeGuideStep : IGuideStep
{
    public double Frame { get; set; }

    public double Time { get; set; }

    public double RADistanceRaw { get; set; }

    public double DECDistanceRaw { get; set; }

    public double RADuration { get; set; }

    public double DECDuration { get; set; }

    public string Event { get; set; } = "GuideStep";

    public string TimeStamp { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Inst { get; set; }

    public IGuideStep Clone() => (NativeGuideStep)MemberwiseClone();
}
