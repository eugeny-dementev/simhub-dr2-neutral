using GameReaderCommon;
using SimHub.Plugins;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;

[assembly: TargetFramework(
    ".NETFramework,Version=v4.8",
    FrameworkDisplayName = ".NET Framework 4.8")]

namespace DR2NeutralPlugin
{
    [PluginDescription("Self-contained sequential shifter calibration and telemetry-confirmed Neutral selection for DiRT Rally 2.0")]
    [PluginAuthor("DR2Neutral contributors")]
    [PluginName("DR2 Neutral Selector")]
    public sealed class DR2Neutral : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string PluginVersion = "1.0.0";

        private const string SettingsKey = "GeneralSettings";
        private const int KeyHoldMs = 35;
        private const int GearChangeTimeoutMs = 1000;
        private const int PostShiftSettleMs = 40;
        private const int TelemetryFreshMs = 1500;
        private const int TelemetryPollMs = 10;
        private const int MaxNeutralPulses = 10;
        private const int ShutdownWaitMs = 1000;
        private const int MaximumStatusLength = 240;

        private readonly SemaphoreSlim _keyGate = new SemaphoreSlim(1, 1);
        private readonly object _settingsGate = new object();
        private readonly object _settingsCommitGate = new object();
        private readonly object _calibrationGate = new object();

        private CancellationTokenSource _shutdown;
        private CancellationTokenSource _calibrationCancellation;
        private Task _neutralTask;
        private Task _inputMonitorTask;
        private Task _calibrationTask;
        private ControllerInputManager _controllerInputManager;
        private DR2NeutralSettings _settings = new DR2NeutralSettings();

        private volatile string _currentGear = "";
        private volatile string _gameName = "";
        private volatile string _lastResult = "Idle";
        private volatile string _inputStatus = "Starting";
        private volatile string _calibrationStatus = "Not running";
        private volatile bool _gameRunning;
        private volatile bool _telemetryIsDirtRally2;

        private long _lastTelemetryUtcTicks;
        private long _settingsRevision;
        private int _busy;
        private int _singleShiftPending;
        private int _controllerConnected;
        private int _calibrationActive;

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon
        {
            get { return null; }
        }

        public string LeftMenuTitle
        {
            get { return "DR2 Neutral"; }
        }

        public string CurrentGear
        {
            get { return _currentGear; }
        }

        public string GameName
        {
            get { return _gameName; }
        }

        public string LastResult
        {
            get { return _lastResult; }
        }

        public string InputStatus
        {
            get { return _inputStatus; }
        }

        public string CalibrationStatus
        {
            get { return _calibrationStatus; }
        }

        public bool Ready
        {
            get { return IsTelemetryReady(); }
        }

        public bool Busy
        {
            get { return Volatile.Read(ref _busy) != 0; }
        }

        public bool ControllerConnected
        {
            get { return Volatile.Read(ref _controllerConnected) != 0; }
        }

        public bool CalibrationActive
        {
            get { return Volatile.Read(ref _calibrationActive) != 0; }
        }

        public double TelemetryAgeMs
        {
            get { return GetTelemetryAgeMs(); }
        }

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            _shutdown = new CancellationTokenSource();

            DR2NeutralSettings loaded = this.ReadCommonSettings<DR2NeutralSettings>(
                SettingsKey,
                () => new DR2NeutralSettings());
            if (loaded == null)
            {
                loaded = new DR2NeutralSettings();
            }

            string settingsWarning = loaded.NormalizeAndValidate();
            lock (_settingsGate)
            {
                _settings = loaded;
            }

            if (!string.IsNullOrEmpty(settingsWarning))
            {
                SimHub.Logging.Current.Warn("[DR2Neutral] " + settingsWarning);
                TrySaveSettings(loaded);
            }

            AttachPublicPropertiesAndActions();

            _controllerInputManager = new ControllerInputManager(
                GetSettingsSnapshot,
                GetSettingsRevision,
                () => CalibrationActive,
                HandleControllerAction,
                SetInputStatus);

            CancellationToken token = _shutdown.Token;
            _inputMonitorTask = Task.Run(async () =>
            {
                try
                {
                    await _controllerInputManager.RunAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    SetInputStatus("Stopped", false);
                }
                catch (Exception ex)
                {
                    SetInputStatus("Input monitor failed: " + BoundStatus(ex.Message), false);
                    SimHub.Logging.Current.Error(
                        "[DR2Neutral] Controller input monitor failed: " + ex);
                }
            });

            SimHub.Logging.Current.Info(
                "[DR2Neutral] Loaded v" + PluginVersion +
                ". Actions: DR2Neutral.GearUp, DR2Neutral.GearDown, " +
                "DR2Neutral.SelectNeutral");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            string newGameName = pluginManager == null
                ? ""
                : (pluginManager.GameName ?? "");

            if (!string.Equals(_gameName, newGameName, StringComparison.Ordinal))
            {
                _gameName = newGameName;
                _telemetryIsDirtRally2 = LooksLikeDirtRally2(newGameName);
            }

            _gameRunning = data.GameRunning;

            if (data.NewData == null)
            {
                _currentGear = "";
                return;
            }

            _currentGear = NormalizeGear(
                Convert.ToString(data.NewData.Gear, CultureInfo.InvariantCulture));
            Interlocked.Exchange(ref _lastTelemetryUtcTicks, DateTime.UtcNow.Ticks);
        }

        public void End(PluginManager pluginManager)
        {
            CancellationTokenSource shutdown = _shutdown;
            if (shutdown != null)
            {
                shutdown.Cancel();
            }

            CancelCalibration();
            WaitForTask(_calibrationTask, ShutdownWaitMs);
            WaitForTask(_neutralTask, ShutdownWaitMs);
            WaitForTask(_inputMonitorTask, ShutdownWaitMs);
            TrySaveSettings(GetSettingsSnapshot());

            SimHub.Logging.Current.Info("[DR2Neutral] Stopped.");
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new DR2NeutralSettingsControl(this);
        }

        public DR2NeutralSettings GetSettingsSnapshot()
        {
            lock (_settingsGate)
            {
                return _settings.Clone();
            }
        }

        public bool TrySetBuiltInControllerEnabled(bool enabled, out string error)
        {
            return TryMutateSettings(
                settings => settings.BuiltInControllerEnabled = enabled,
                out error);
        }

        public bool TrySetVirtualKeys(int gearUpVirtualKey, int gearDownVirtualKey, out string error)
        {
            if (!DR2NeutralSettings.IsFunctionKey(gearUpVirtualKey) ||
                !DR2NeutralSettings.IsFunctionKey(gearDownVirtualKey))
            {
                error = "Select function keys from F1 through F12.";
                return false;
            }

            if (gearUpVirtualKey == gearDownVirtualKey)
            {
                error = "Gear Up and Gear Down must use different keys.";
                return false;
            }

            return TryMutateSettings(
                settings =>
                {
                    settings.GearUpVirtualKey = gearUpVirtualKey;
                    settings.GearDownVirtualKey = gearDownVirtualKey;
                },
                out error);
        }

        public bool TryClearCalibration(out string error)
        {
            if (CalibrationActive)
            {
                error = "Cancel the active calibration before clearing it.";
                return false;
            }

            bool saved = TryMutateSettings(
                settings => settings.ClearControllerCalibration(),
                out error);
            if (saved)
            {
                SetCalibrationStatus("Calibration cleared");
                _lastResult = "Controller calibration cleared";
            }

            return saved;
        }

        public Task StartCalibrationAsync()
        {
            lock (_calibrationGate)
            {
                if (_calibrationTask != null && !_calibrationTask.IsCompleted)
                {
                    return _calibrationTask;
                }

                CancellationTokenSource shutdown = _shutdown;
                if (shutdown == null || shutdown.IsCancellationRequested)
                {
                    return Task.FromResult(0);
                }

                _calibrationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    shutdown.Token);
                Volatile.Write(ref _calibrationActive, 1);
                Interlocked.Increment(ref _settingsRevision);
                SetCalibrationStatus("Starting calibration...");

                CancellationToken token = _calibrationCancellation.Token;
                _calibrationTask = RunCalibrationAsync(token);
                return _calibrationTask;
            }
        }

        public void CancelCalibration()
        {
            lock (_calibrationGate)
            {
                if (_calibrationCancellation != null &&
                    !_calibrationCancellation.IsCancellationRequested)
                {
                    _calibrationCancellation.Cancel();
                }
            }
        }

        public static string FormatVirtualKey(int virtualKey)
        {
            return DR2NeutralSettings.IsFunctionKey(virtualKey)
                ? "F" + (virtualKey - 0x6F).ToString(CultureInfo.InvariantCulture)
                : "Unknown";
        }

        internal long GetSettingsRevision()
        {
            return Interlocked.Read(ref _settingsRevision);
        }

        internal static string NormalizeGear(string gear)
        {
            if (string.IsNullOrWhiteSpace(gear))
            {
                return "";
            }

            string value = gear.Trim().ToUpperInvariant();

            if (value == "NEUTRAL" || value == "0")
            {
                return "N";
            }

            if (value == "REVERSE" || value == "REV" || value == "-1")
            {
                return "R";
            }

            return value;
        }

        internal static bool LooksLikeDirtRally2(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            StringBuilder normalized = new StringBuilder(value.Length);

            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    normalized.Append(char.ToLowerInvariant(c));
                }
            }

            return normalized.ToString().Contains("dirtrally2");
        }

        internal static int GetNeutralVirtualKey(
            string gear,
            int gearUpVirtualKey,
            int gearDownVirtualKey)
        {
            string normalized = NormalizeGear(gear);
            if (normalized == "N")
            {
                return 0;
            }

            if (normalized == "R")
            {
                return gearUpVirtualKey;
            }

            int forwardGear;
            return int.TryParse(
                       normalized,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out forwardGear) &&
                   forwardGear >= 1 &&
                   forwardGear <= 9
                ? gearDownVirtualKey
                : -1;
        }

        private void AttachPublicPropertiesAndActions()
        {
            this.AttachDelegate("CurrentGear", () => CurrentGear);
            this.AttachDelegate("GameName", () => GameName);
            this.AttachDelegate("Busy", () => Busy);
            this.AttachDelegate("Ready", () => Ready);
            this.AttachDelegate("TelemetryAgeMs", () => TelemetryAgeMs);
            this.AttachDelegate("LastResult", () => LastResult);
            this.AttachDelegate("InputStatus", () => InputStatus);
            this.AttachDelegate("ControllerConnected", () => ControllerConnected);
            this.AttachDelegate("Calibrated", () => GetSettingsSnapshot().IsCalibrated);
            this.AttachDelegate("ControllerName", () => GetSettingsSnapshot().ControllerName);
            this.AttachDelegate("CalibrationActive", () => CalibrationActive);
            this.AttachDelegate("CalibrationStatus", () => CalibrationStatus);

            this.AddAction("GearUp", (a, b) =>
            {
                DR2NeutralSettings settings = GetSettingsSnapshot();
                QueueSingleShift((ushort)settings.GearUpVirtualKey, "Gear Up");
            });

            this.AddAction("GearDown", (a, b) =>
            {
                DR2NeutralSettings settings = GetSettingsSnapshot();
                QueueSingleShift((ushort)settings.GearDownVirtualKey, "Gear Down");
            });

            this.AddAction("SelectNeutral", (a, b) => QueueNeutralSelection());
        }

        private async Task RunCalibrationAsync(CancellationToken token)
        {
            try
            {
                ControllerCalibrationResult result =
                    await ControllerInputManager.CalibrateAsync(
                            SetCalibrationStatus,
                            token)
                        .ConfigureAwait(false);

                if (!result.Success)
                {
                    SetCalibrationStatus(result.Message);
                    _lastResult = result.Message;
                    return;
                }

                string error;
                bool saved = TryMutateSettings(
                    settings =>
                    {
                        settings.ControllerInstanceGuid = result.ControllerInstanceGuid;
                        settings.ControllerProductGuid = result.ControllerProductGuid;
                        settings.ControllerName = result.ControllerName;
                        settings.GearUpButton = result.GearUpButton;
                        settings.GearDownButton = result.GearDownButton;
                        settings.SelectNeutralButton = result.SelectNeutralButton;
                    },
                    out error);

                if (!saved)
                {
                    SetCalibrationStatus("Calibration was not saved: " + error);
                    _lastResult = "Calibration was not saved: " + error;
                    return;
                }

                SetCalibrationStatus(
                    "Calibration complete: " + result.ControllerName);
                _lastResult = "Controller calibration complete";
            }
            catch (OperationCanceledException)
            {
                SetCalibrationStatus("Calibration cancelled; previous assignments kept");
                _lastResult = "Controller calibration cancelled";
            }
            catch (Exception ex)
            {
                string message = "Calibration failed: " + BoundStatus(ex.Message);
                SetCalibrationStatus(message);
                _lastResult = message;
                SimHub.Logging.Current.Error("[DR2Neutral] " + message + " " + ex);
            }
            finally
            {
                Volatile.Write(ref _calibrationActive, 0);
                Interlocked.Increment(ref _settingsRevision);

                lock (_calibrationGate)
                {
                    if (_calibrationCancellation != null)
                    {
                        _calibrationCancellation.Dispose();
                        _calibrationCancellation = null;
                    }
                }
            }
        }

        private void HandleControllerAction(ControllerActionKind action)
        {
            if (action == ControllerActionKind.RejectedSimultaneous)
            {
                _lastResult = "Controller input ignored: multiple mapped controls pressed together";
                return;
            }

            DR2NeutralSettings settings = GetSettingsSnapshot();
            switch (action)
            {
                case ControllerActionKind.GearUp:
                    QueueSingleShift((ushort)settings.GearUpVirtualKey, "Gear Up");
                    return;
                case ControllerActionKind.GearDown:
                    QueueSingleShift((ushort)settings.GearDownVirtualKey, "Gear Down");
                    return;
                case ControllerActionKind.SelectNeutral:
                    QueueNeutralSelection();
                    return;
                case ControllerActionKind.None:
                    return;
                default:
                    _lastResult = "Controller input ignored: unsupported action";
                    return;
            }
        }

        private void QueueSingleShift(ushort virtualKey, string label)
        {
            CancellationTokenSource shutdown = _shutdown;
            if (shutdown == null || shutdown.IsCancellationRequested)
            {
                return;
            }

            if (CalibrationActive)
            {
                _lastResult = label + " ignored: calibration active";
                return;
            }

            if (Busy)
            {
                _lastResult = label + " ignored: Neutral sequence active";
                return;
            }

            if (!ForegroundIsDirtRally2())
            {
                _lastResult = label + " ignored: DiRT Rally 2.0 is not foreground";
                return;
            }

            if (Interlocked.CompareExchange(ref _singleShiftPending, 1, 0) != 0)
            {
                _lastResult = label + " ignored: another shift is in progress";
                return;
            }

            if (Busy || CalibrationActive)
            {
                Volatile.Write(ref _singleShiftPending, 0);
                _lastResult = label + " ignored: another operation started";
                return;
            }

            CancellationToken token = shutdown.Token;
            Task.Run(async () =>
            {
                try
                {
                    await PulseKeyAsync(virtualKey, token).ConfigureAwait(false);
                    _lastResult = label + " sent";
                }
                catch (OperationCanceledException)
                {
                    _lastResult = label + " cancelled";
                }
                catch (Exception ex)
                {
                    _lastResult = label + " failed: " + BoundStatus(ex.Message);
                    SimHub.Logging.Current.Error(
                        "[DR2Neutral] " + label + " failed: " + ex);
                }
                finally
                {
                    Volatile.Write(ref _singleShiftPending, 0);
                }
            });
        }

        private void QueueNeutralSelection()
        {
            CancellationTokenSource shutdown = _shutdown;
            if (shutdown == null || shutdown.IsCancellationRequested)
            {
                return;
            }

            if (CalibrationActive)
            {
                _lastResult = "Neutral ignored: calibration active";
                return;
            }

            string reason;
            if (!CanSelectNeutral(out reason))
            {
                _lastResult = "Neutral ignored: " + reason;
                return;
            }

            if (NormalizeGear(_currentGear) == "N")
            {
                _lastResult = "Already Neutral";
                return;
            }

            if (Volatile.Read(ref _singleShiftPending) != 0)
            {
                _lastResult = "Neutral ignored: shift action in progress";
                return;
            }

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                _lastResult = "Neutral ignored: sequence already active";
                return;
            }

            if (Volatile.Read(ref _singleShiftPending) != 0 || CalibrationActive)
            {
                Volatile.Write(ref _busy, 0);
                _lastResult = "Neutral ignored: another operation started";
                return;
            }

            DR2NeutralSettings settings = GetSettingsSnapshot();
            ushort gearUpKey = (ushort)settings.GearUpVirtualKey;
            ushort gearDownKey = (ushort)settings.GearDownVirtualKey;
            CancellationToken token = shutdown.Token;

            _neutralTask = Task.Run(async () =>
            {
                try
                {
                    await SelectNeutralAsync(gearUpKey, gearDownKey, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _lastResult = "Neutral sequence cancelled";
                }
                catch (Exception ex)
                {
                    _lastResult = "Neutral failed: " + BoundStatus(ex.Message);
                    SimHub.Logging.Current.Error(
                        "[DR2Neutral] Neutral sequence failed: " + ex);
                }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                }
            });
        }

        private async Task SelectNeutralAsync(
            ushort gearUpVirtualKey,
            ushort gearDownVirtualKey,
            CancellationToken token)
        {
            string startGear = NormalizeGear(_currentGear);
            _lastResult = "Selecting Neutral from " + startGear;

            for (int pulse = 1; pulse <= MaxNeutralPulses; pulse++)
            {
                token.ThrowIfCancellationRequested();

                string reason;
                if (!CanSelectNeutral(out reason))
                {
                    _lastResult = "Neutral aborted: " + reason;
                    return;
                }

                string before = NormalizeGear(_currentGear);
                int key = GetNeutralVirtualKey(
                    before,
                    gearUpVirtualKey,
                    gearDownVirtualKey);

                if (key == 0)
                {
                    _lastResult = "Neutral selected from " + startGear +
                                  " using " + (pulse - 1) + " pulse(s)";
                    return;
                }

                if (key < 0)
                {
                    _lastResult = "Neutral aborted: unsupported gear '" + before + "'";
                    return;
                }

                bool changed = await PulseAndWaitForGearChangeAsync(
                        (ushort)key,
                        before,
                        token)
                    .ConfigureAwait(false);

                if (!changed)
                {
                    _lastResult = "Neutral aborted: gear stayed at " + before +
                                  " for " + GearChangeTimeoutMs + " ms";
                    return;
                }

                await Task.Delay(PostShiftSettleMs, token).ConfigureAwait(false);
            }

            _lastResult = "Neutral aborted: exceeded " +
                          MaxNeutralPulses + " shift pulses";
        }

        private async Task<bool> PulseAndWaitForGearChangeAsync(
            ushort virtualKey,
            string before,
            CancellationToken token)
        {
            await PulseKeyAsync(virtualKey, token).ConfigureAwait(false);

            Stopwatch timeout = Stopwatch.StartNew();
            while (timeout.ElapsedMilliseconds < GearChangeTimeoutMs)
            {
                token.ThrowIfCancellationRequested();

                if (!ForegroundIsDirtRally2())
                {
                    return false;
                }

                string now = NormalizeGear(_currentGear);
                if (IsKnownGear(now) &&
                    !string.Equals(now, before, StringComparison.Ordinal))
                {
                    return true;
                }

                await Task.Delay(TelemetryPollMs, token).ConfigureAwait(false);
            }

            return false;
        }

        private async Task PulseKeyAsync(ushort virtualKey, CancellationToken token)
        {
            await _keyGate.WaitAsync(token).ConfigureAwait(false);
            bool keyIsDown = false;

            try
            {
                if (!ForegroundIsDirtRally2())
                {
                    throw new InvalidOperationException(
                        "DiRT Rally 2.0 lost foreground focus before key injection.");
                }

                SendKeyState(virtualKey, false);
                keyIsDown = true;
                await Task.Delay(KeyHoldMs, token).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (keyIsDown)
                    {
                        SendKeyState(virtualKey, true);
                    }
                }
                finally
                {
                    _keyGate.Release();
                }
            }
        }

        private bool CanSelectNeutral(out string reason)
        {
            if (!ForegroundIsDirtRally2())
            {
                reason = "DiRT Rally 2.0 is not foreground";
                return false;
            }

            if (!_telemetryIsDirtRally2)
            {
                reason = "SimHub is not receiving DiRT Rally 2.0 telemetry";
                return false;
            }

            if (!_gameRunning)
            {
                reason = "game telemetry is not running";
                return false;
            }

            double age = GetTelemetryAgeMs();
            if (age < 0 || age > TelemetryFreshMs)
            {
                reason = "telemetry is stale (" +
                         Math.Round(age, 0).ToString(CultureInfo.InvariantCulture) +
                         " ms)";
                return false;
            }

            string gear = NormalizeGear(_currentGear);
            if (!IsKnownGear(gear))
            {
                reason = "current gear is unavailable ('" + gear + "')";
                return false;
            }

            reason = "";
            return true;
        }

        private bool IsTelemetryReady()
        {
            double age = GetTelemetryAgeMs();
            return _telemetryIsDirtRally2 &&
                   _gameRunning &&
                   age >= 0 &&
                   age <= TelemetryFreshMs &&
                   IsKnownGear(NormalizeGear(_currentGear));
        }

        private double GetTelemetryAgeMs()
        {
            long lastTicks = Interlocked.Read(ref _lastTelemetryUtcTicks);
            if (lastTicks == 0)
            {
                return -1;
            }

            long elapsedTicks = DateTime.UtcNow.Ticks - lastTicks;
            if (elapsedTicks < 0)
            {
                elapsedTicks = 0;
            }

            return elapsedTicks / (double)TimeSpan.TicksPerMillisecond;
        }

        private static bool IsKnownGear(string gear)
        {
            if (gear == "N" || gear == "R")
            {
                return true;
            }

            int number;
            return int.TryParse(
                       gear,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out number) &&
                   number > 0 &&
                   number <= 9;
        }

        private bool TryMutateSettings(
            Action<DR2NeutralSettings> mutation,
            out string error)
        {
            if (mutation == null)
            {
                error = "Settings update is invalid.";
                return false;
            }

            lock (_settingsCommitGate)
            {
                DR2NeutralSettings candidate = GetSettingsSnapshot();
                mutation(candidate);
                string warning = candidate.NormalizeAndValidate();

                if (!string.IsNullOrEmpty(warning))
                {
                    error = warning;
                    return false;
                }

                try
                {
                    this.SaveCommonSettings(SettingsKey, candidate);
                }
                catch (Exception ex)
                {
                    error = "Could not save settings: " + BoundStatus(ex.Message);
                    return false;
                }

                lock (_settingsGate)
                {
                    _settings = candidate;
                    Interlocked.Increment(ref _settingsRevision);
                }
            }

            error = "";
            return true;
        }

        private void TrySaveSettings(DR2NeutralSettings settings)
        {
            try
            {
                this.SaveCommonSettings(SettingsKey, settings);
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error(
                    "[DR2Neutral] Settings save failed: " + ex);
            }
        }

        private void SetInputStatus(string status, bool connected)
        {
            string bounded = BoundStatus(status);
            string previous = _inputStatus;
            _inputStatus = bounded;
            Volatile.Write(ref _controllerConnected, connected ? 1 : 0);

            if (!string.Equals(previous, bounded, StringComparison.Ordinal))
            {
                SimHub.Logging.Current.Info("[DR2Neutral] Input: " + bounded);
            }
        }

        private void SetCalibrationStatus(string status)
        {
            _calibrationStatus = BoundStatus(status);
        }

        private static string BoundStatus(string status)
        {
            string value = string.IsNullOrWhiteSpace(status)
                ? "Unknown"
                : status.Trim();
            return value.Length <= MaximumStatusLength
                ? value
                : value.Substring(0, MaximumStatusLength);
        }

        private static void WaitForTask(Task task, int timeoutMilliseconds)
        {
            if (task == null)
            {
                return;
            }

            try
            {
                task.Wait(timeoutMilliseconds);
            }
            catch
            {
                // Shutdown is best effort and remains bounded.
            }
        }

        private static bool ForegroundIsDirtRally2()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                uint processId;
                GetWindowThreadProcessId(window, out processId);

                if (processId != 0)
                {
                    using (Process process = Process.GetProcessById((int)processId))
                    {
                        if (LooksLikeDirtRally2(process.ProcessName))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Fall through to the bounded window-title check.
            }

            StringBuilder title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            return LooksLikeDirtRally2(title.ToString());
        }

        private static void SendKeyState(ushort virtualKey, bool keyUp)
        {
            ushort scanCode = (ushort)MapVirtualKey(virtualKey, 0);
            uint flags = keyUp ? KeyEventFKeyUp : 0;
            KEYBDINPUT keyboardInput;

            if (scanCode != 0)
            {
                flags |= KeyEventFScanCode;
                keyboardInput = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                };
            }
            else
            {
                keyboardInput = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                };
            }

            INPUT[] inputs =
            {
                new INPUT
                {
                    type = InputKeyboard,
                    U = new InputUnion { ki = keyboardInput }
                }
            };

            uint sent = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != 1)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "SendInput did not inject the keyboard event. Run SimHub and " +
                    "DiRT Rally 2.0 at the same privilege level.");
            }
        }

        private const uint InputKeyboard = 1;
        private const uint KeyEventFKeyUp = 0x0002;
        private const uint KeyEventFScanCode = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint nInputs,
            INPUT[] pInputs,
            int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr hWnd,
            out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowText(
            IntPtr hWnd,
            StringBuilder text,
            int maxCount);
    }
}
