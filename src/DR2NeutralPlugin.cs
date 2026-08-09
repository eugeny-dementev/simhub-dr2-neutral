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
        public const string PluginVersion = "1.1.0";

        private const string SettingsKey = "GeneralSettings";
        private const int KeyHoldMs = 35;
        private const int GearChangeTimeoutMs = 1000;
        private const int PostShiftSettleMs = 40;
        private const int TelemetryFreshMs = 1500;
        private const int TelemetryPollMs = 10;
        private const int MaxNeutralPulses = 10;
        private const int ShutdownWaitMs = 1000;
        private const int MaximumStatusLength = 240;

        private enum StructuredLogLevel
        {
            Info,
            Warning,
            Error
        }

        private struct NeutralConditionSnapshot
        {
            public bool DirtForeground;
            public bool TelemetryIsDirtRally2;
            public bool GameRunning;
            public double TelemetryAgeMs;
            public string CurrentGear;
            public bool GearKnown;
            public bool Allowed;
            public string ReasonCode;
            public string Reason;
        }

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
        private long _nextOperationId;
        private int _busy;
        private int _singleShiftPending;
        private int _controllerConnected;
        private int _calibrationActive;
        private int _structuredLoggingFailureReported;

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
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "plugin.loaded",
                LogField("version", PluginVersion),
                LogField("builtInControllerEnabled", loaded.BuiltInControllerEnabled),
                LogField("calibrated", loaded.IsCalibrated),
                LogField("controllerName", loaded.ControllerName),
                LogField("gearUpKey", FormatVirtualKey(loaded.GearUpVirtualKey)),
                LogField("gearDownKey", FormatVirtualKey(loaded.GearDownVirtualKey)));
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

            LogStructuredEvent(
                StructuredLogLevel.Info,
                "plugin.stopped",
                LogField("busy", Busy),
                LogField("shiftPending", Volatile.Read(ref _singleShiftPending) != 0),
                LogField("calibrationActive", CalibrationActive));
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
                SetLastResult(
                    StructuredLogLevel.Info,
                    "calibration.cleared",
                    "Controller calibration cleared");
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
                LogStructuredEvent(
                    StructuredLogLevel.Info,
                    "calibration.started");

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
                    LogStructuredEvent(
                        StructuredLogLevel.Info,
                        "calibration.cancel_requested");
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
                QueueSingleShift(
                    (ushort)settings.GearUpVirtualKey,
                    "Gear Up",
                    "simhub_action",
                    NextOperationId());
            });

            this.AddAction("GearDown", (a, b) =>
            {
                DR2NeutralSettings settings = GetSettingsSnapshot();
                QueueSingleShift(
                    (ushort)settings.GearDownVirtualKey,
                    "Gear Down",
                    "simhub_action",
                    NextOperationId());
            });

            this.AddAction("SelectNeutral", (a, b) =>
                QueueNeutralSelection("simhub_action", NextOperationId()));
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
                    SetLastResult(
                        StructuredLogLevel.Warning,
                        "calibration.not_completed",
                        result.Message);
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
                    SetLastResult(
                        StructuredLogLevel.Error,
                        "calibration.save_failed",
                        "Calibration was not saved: " + error,
                        LogField("controllerName", result.ControllerName));
                    return;
                }

                SetCalibrationStatus(
                    "Calibration complete: " + result.ControllerName);
                SetLastResult(
                    StructuredLogLevel.Info,
                    "calibration.completed",
                    "Controller calibration complete",
                    LogField("controllerName", result.ControllerName),
                    LogField("gearUpButton", result.GearUpButton),
                    LogField("gearDownButton", result.GearDownButton),
                    LogField("selectNeutralButton", result.SelectNeutralButton));
            }
            catch (OperationCanceledException)
            {
                SetCalibrationStatus("Calibration cancelled; previous assignments kept");
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "calibration.cancelled",
                    "Controller calibration cancelled");
            }
            catch (Exception ex)
            {
                string message = "Calibration failed: " + BoundStatus(ex.Message);
                SetCalibrationStatus(message);
                SetLastResult(
                    StructuredLogLevel.Error,
                    "calibration.failed",
                    message,
                    LogField("exceptionType", ex.GetType().FullName),
                    LogField("exceptionMessage", BoundStatus(ex.Message)));
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
            DR2NeutralSettings settings = GetSettingsSnapshot();
            long operationId = NextOperationId();

            if (action == ControllerActionKind.RejectedSimultaneous)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "input.rejected_simultaneous",
                    "Controller input ignored: multiple mapped controls pressed together",
                    LogField("operationId", operationId),
                    LogField("source", "built_in_controller"),
                    LogField("controllerName", settings.ControllerName),
                    LogField("gearUpButton", settings.GearUpButton),
                    LogField("gearDownButton", settings.GearDownButton),
                    LogField("selectNeutralButton", settings.SelectNeutralButton),
                    LogField("currentGear", NormalizeGear(_currentGear)));
                return;
            }

            int buttonIndex = GetMappedButtonIndex(settings, action);
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "input.button_pressed",
                LogField("operationId", operationId),
                LogField("source", "built_in_controller"),
                LogField("controllerName", settings.ControllerName),
                LogField("logicalAction", GetControllerActionName(action)),
                LogField("buttonIndex", buttonIndex),
                LogField("currentGear", NormalizeGear(_currentGear)),
                LogField("busy", Busy),
                LogField("shiftPending", Volatile.Read(ref _singleShiftPending) != 0));

            switch (action)
            {
                case ControllerActionKind.GearUp:
                    QueueSingleShift(
                        (ushort)settings.GearUpVirtualKey,
                        "Gear Up",
                        "built_in_controller",
                        operationId);
                    return;
                case ControllerActionKind.GearDown:
                    QueueSingleShift(
                        (ushort)settings.GearDownVirtualKey,
                        "Gear Down",
                        "built_in_controller",
                        operationId);
                    return;
                case ControllerActionKind.SelectNeutral:
                    QueueNeutralSelection("built_in_controller", operationId);
                    return;
                case ControllerActionKind.None:
                    return;
                default:
                    SetLastResult(
                        StructuredLogLevel.Warning,
                        "input.unsupported",
                        "Controller input ignored: unsupported action",
                        LogField("operationId", operationId),
                        LogField("source", "built_in_controller"),
                        LogField("logicalAction", Convert.ToInt32(action, CultureInfo.InvariantCulture)));
                    return;
            }
        }

        private void QueueSingleShift(
            ushort virtualKey,
            string label,
            string source,
            long operationId)
        {
            string beforeGear = NormalizeGear(_currentGear);
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "shift.requested",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("logicalAction", label),
                LogField("virtualKey", FormatVirtualKey(virtualKey)),
                LogField("currentGear", beforeGear));

            CancellationTokenSource shutdown = _shutdown;
            if (shutdown == null || shutdown.IsCancellationRequested)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "shift.rejected",
                    label + " ignored: plugin is stopping",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "plugin_stopping"));
                return;
            }

            bool calibrationActive = CalibrationActive;
            bool neutralBusy = Busy;
            bool dirtForeground = ForegroundIsDirtRally2();
            bool shiftPending = Volatile.Read(ref _singleShiftPending) != 0;
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "shift.conditions_checked",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("calibrationActive", calibrationActive),
                LogField("neutralBusy", neutralBusy),
                LogField("dirtForeground", dirtForeground),
                LogField("shiftPending", shiftPending),
                LogField("currentGear", beforeGear));

            if (calibrationActive)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "shift.rejected",
                    label + " ignored: calibration active",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "calibration_active"));
                return;
            }

            if (neutralBusy)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "shift.rejected",
                    label + " ignored: Neutral sequence active",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "neutral_sequence_active"));
                return;
            }

            if (!dirtForeground)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "shift.rejected",
                    label + " ignored: DiRT Rally 2.0 is not foreground",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "dirt_not_foreground"));
                return;
            }

            if (Interlocked.CompareExchange(ref _singleShiftPending, 1, 0) != 0)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "shift.rejected",
                    label + " ignored: another shift is in progress",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "shift_in_progress"));
                return;
            }

            if (Busy || CalibrationActive)
            {
                Volatile.Write(ref _singleShiftPending, 0);
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "shift.rejected",
                    label + " ignored: another operation started",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "operation_race"));
                return;
            }

            CancellationToken token = shutdown.Token;
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "shift.queued",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("logicalAction", label),
                LogField("virtualKey", FormatVirtualKey(virtualKey)),
                LogField("beforeGear", beforeGear));
            Task.Run(async () =>
            {
                Stopwatch operationTimer = Stopwatch.StartNew();
                try
                {
                    await PulseKeyAsync(
                            virtualKey,
                            operationId,
                            source,
                            label,
                            1,
                            beforeGear,
                            token)
                        .ConfigureAwait(false);
                    SetLastResult(
                        StructuredLogLevel.Info,
                        "shift.sent",
                        label + " sent",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("logicalAction", label),
                        LogField("virtualKey", FormatVirtualKey(virtualKey)),
                        LogField("beforeGear", beforeGear),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                }
                catch (OperationCanceledException)
                {
                    SetLastResult(
                        StructuredLogLevel.Warning,
                        "shift.cancelled",
                        label + " cancelled",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                }
                catch (Exception ex)
                {
                    SetLastResult(
                        StructuredLogLevel.Error,
                        "shift.failed",
                        label + " failed: " + BoundStatus(ex.Message),
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("exceptionType", ex.GetType().FullName),
                        LogField("exceptionMessage", BoundStatus(ex.Message)),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                    SimHub.Logging.Current.Error(
                        "[DR2Neutral] " + label + " failed: " + ex);
                }
                finally
                {
                    Volatile.Write(ref _singleShiftPending, 0);
                    LogStructuredEvent(
                        StructuredLogLevel.Info,
                        "shift.operation_released",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                }
            });
        }

        private void QueueNeutralSelection(string source, long operationId)
        {
            string requestedGear = NormalizeGear(_currentGear);
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "neutral.requested",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("currentGear", requestedGear),
                LogField("busy", Busy),
                LogField("shiftPending", Volatile.Read(ref _singleShiftPending) != 0),
                LogField("calibrationActive", CalibrationActive));

            CancellationTokenSource shutdown = _shutdown;
            if (shutdown == null || shutdown.IsCancellationRequested)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "neutral.rejected",
                    "Neutral ignored: plugin is stopping",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "plugin_stopping"));
                return;
            }

            if (CalibrationActive)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "neutral.rejected",
                    "Neutral ignored: calibration active",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "calibration_active"));
                return;
            }

            NeutralConditionSnapshot conditions = EvaluateNeutralConditions();
            LogNeutralConditions(operationId, source, "request", 0, conditions);
            if (!conditions.Allowed)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "neutral.rejected",
                    "Neutral ignored: " + conditions.Reason,
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", conditions.ReasonCode),
                    LogField("currentGear", conditions.CurrentGear),
                    LogField("telemetryAgeMs", conditions.TelemetryAgeMs));
                return;
            }

            if (NormalizeGear(_currentGear) == "N")
            {
                SetLastResult(
                    StructuredLogLevel.Info,
                    "neutral.already_selected",
                    "Already Neutral",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("currentGear", "N"));
                return;
            }

            if (Volatile.Read(ref _singleShiftPending) != 0)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "neutral.rejected",
                    "Neutral ignored: shift action in progress",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "shift_in_progress"));
                return;
            }

            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "neutral.rejected",
                    "Neutral ignored: sequence already active",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "sequence_already_active"));
                return;
            }

            if (Volatile.Read(ref _singleShiftPending) != 0 || CalibrationActive)
            {
                Volatile.Write(ref _busy, 0);
                SetLastResult(
                    StructuredLogLevel.Warning,
                    "neutral.rejected",
                    "Neutral ignored: another operation started",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("reason", "operation_race"));
                return;
            }

            DR2NeutralSettings settings = GetSettingsSnapshot();
            ushort gearUpKey = (ushort)settings.GearUpVirtualKey;
            ushort gearDownKey = (ushort)settings.GearDownVirtualKey;
            CancellationToken token = shutdown.Token;
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "neutral.queued",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("startGear", requestedGear),
                LogField("gearUpKey", FormatVirtualKey(gearUpKey)),
                LogField("gearDownKey", FormatVirtualKey(gearDownKey)));

            _neutralTask = Task.Run(async () =>
            {
                Stopwatch operationTimer = Stopwatch.StartNew();
                try
                {
                    await SelectNeutralAsync(
                            gearUpKey,
                            gearDownKey,
                            source,
                            operationId,
                            operationTimer,
                            token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    SetLastResult(
                        StructuredLogLevel.Warning,
                        "neutral.cancelled",
                        "Neutral sequence cancelled",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("currentGear", NormalizeGear(_currentGear)),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                }
                catch (Exception ex)
                {
                    SetLastResult(
                        StructuredLogLevel.Error,
                        "neutral.failed",
                        "Neutral failed: " + BoundStatus(ex.Message),
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("exceptionType", ex.GetType().FullName),
                        LogField("exceptionMessage", BoundStatus(ex.Message)),
                        LogField("currentGear", NormalizeGear(_currentGear)),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                    SimHub.Logging.Current.Error(
                        "[DR2Neutral] Neutral sequence failed: " + ex);
                }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                    LogStructuredEvent(
                        StructuredLogLevel.Info,
                        "neutral.operation_released",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("currentGear", NormalizeGear(_currentGear)),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                }
            });
        }

        private async Task SelectNeutralAsync(
            ushort gearUpVirtualKey,
            ushort gearDownVirtualKey,
            string source,
            long operationId,
            Stopwatch operationTimer,
            CancellationToken token)
        {
            string startGear = NormalizeGear(_currentGear);
            SetLastResult(
                StructuredLogLevel.Info,
                "neutral.started",
                "Selecting Neutral from " + startGear,
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("startGear", startGear),
                LogField("gearUpKey", FormatVirtualKey(gearUpVirtualKey)),
                LogField("gearDownKey", FormatVirtualKey(gearDownVirtualKey)));

            for (int pulse = 1; pulse <= MaxNeutralPulses; pulse++)
            {
                token.ThrowIfCancellationRequested();

                NeutralConditionSnapshot conditions = EvaluateNeutralConditions();
                LogNeutralConditions(operationId, source, "before_pulse", pulse, conditions);
                if (!conditions.Allowed)
                {
                    SetLastResult(
                        StructuredLogLevel.Warning,
                        "neutral.aborted",
                        "Neutral aborted: " + conditions.Reason,
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("pulse", pulse),
                        LogField("reason", conditions.ReasonCode),
                        LogField("currentGear", conditions.CurrentGear),
                        LogField("telemetryAgeMs", conditions.TelemetryAgeMs),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                    return;
                }

                string before = NormalizeGear(_currentGear);
                int key = GetNeutralVirtualKey(
                    before,
                    gearUpVirtualKey,
                    gearDownVirtualKey);

                LogStructuredEvent(
                    StructuredLogLevel.Info,
                    "neutral.route_selected",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("pulse", pulse),
                    LogField("beforeGear", before),
                    LogField("virtualKey", key > 0 ? FormatVirtualKey(key) : "none"),
                    LogField("route", key == 0 ? "already_neutral" : key < 0 ? "unsupported" : "shift"));

                if (key == 0)
                {
                    SetLastResult(
                        StructuredLogLevel.Info,
                        "neutral.succeeded",
                        "Neutral selected from " + startGear +
                        " using " + (pulse - 1) + " pulse(s)",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("startGear", startGear),
                        LogField("currentGear", before),
                        LogField("pulsesSent", pulse - 1),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                    return;
                }

                if (key < 0)
                {
                    SetLastResult(
                        StructuredLogLevel.Warning,
                        "neutral.aborted",
                        "Neutral aborted: unsupported gear '" + before + "'",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("pulse", pulse),
                        LogField("reason", "unsupported_gear"),
                        LogField("currentGear", before),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                    return;
                }

                bool changed = await PulseAndWaitForGearChangeAsync(
                        (ushort)key,
                        before,
                        source,
                        operationId,
                        pulse,
                        token)
                    .ConfigureAwait(false);

                if (!changed)
                {
                    SetLastResult(
                        StructuredLogLevel.Warning,
                        "neutral.aborted",
                        "Neutral aborted: gear stayed at " + before +
                        " for " + GearChangeTimeoutMs + " ms",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("pulse", pulse),
                        LogField("reason", "gear_change_unconfirmed"),
                        LogField("beforeGear", before),
                        LogField("currentGear", NormalizeGear(_currentGear)),
                        LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
                    return;
                }

                LogStructuredEvent(
                    StructuredLogLevel.Info,
                    "neutral.settle_started",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("pulse", pulse),
                    LogField("delayMs", PostShiftSettleMs),
                    LogField("currentGear", NormalizeGear(_currentGear)));
                await Task.Delay(PostShiftSettleMs, token).ConfigureAwait(false);
                LogStructuredEvent(
                    StructuredLogLevel.Info,
                    "neutral.settle_completed",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("pulse", pulse),
                    LogField("delayMs", PostShiftSettleMs),
                    LogField("currentGear", NormalizeGear(_currentGear)));
            }

            SetLastResult(
                StructuredLogLevel.Warning,
                "neutral.aborted",
                "Neutral aborted: exceeded " + MaxNeutralPulses + " shift pulses",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("reason", "pulse_limit_exceeded"),
                LogField("pulseLimit", MaxNeutralPulses),
                LogField("currentGear", NormalizeGear(_currentGear)),
                LogField("elapsedMs", operationTimer.Elapsed.TotalMilliseconds));
        }

        private async Task<bool> PulseAndWaitForGearChangeAsync(
            ushort virtualKey,
            string before,
            string source,
            long operationId,
            int pulseNumber,
            CancellationToken token)
        {
            await PulseKeyAsync(
                    virtualKey,
                    operationId,
                    source,
                    "Select Neutral",
                    pulseNumber,
                    before,
                    token)
                .ConfigureAwait(false);

            Stopwatch timeout = Stopwatch.StartNew();
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "gear_change.wait_started",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("pulse", pulseNumber),
                LogField("beforeGear", before),
                LogField("timeoutMs", GearChangeTimeoutMs),
                LogField("telemetryAgeMs", GetTelemetryAgeMs()));

            while (timeout.ElapsedMilliseconds < GearChangeTimeoutMs)
            {
                token.ThrowIfCancellationRequested();

                if (!ForegroundIsDirtRally2())
                {
                    LogStructuredEvent(
                        StructuredLogLevel.Warning,
                        "gear_change.focus_lost",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("pulse", pulseNumber),
                        LogField("beforeGear", before),
                        LogField("currentGear", NormalizeGear(_currentGear)),
                        LogField("elapsedMs", timeout.Elapsed.TotalMilliseconds));
                    return false;
                }

                string now = NormalizeGear(_currentGear);
                if (IsKnownGear(now) &&
                    !string.Equals(now, before, StringComparison.Ordinal))
                {
                    LogStructuredEvent(
                        StructuredLogLevel.Info,
                        "gear_change.confirmed",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("pulse", pulseNumber),
                        LogField("beforeGear", before),
                        LogField("afterGear", now),
                        LogField("elapsedMs", timeout.Elapsed.TotalMilliseconds),
                        LogField("telemetryAgeMs", GetTelemetryAgeMs()));
                    return true;
                }

                await Task.Delay(TelemetryPollMs, token).ConfigureAwait(false);
            }

            LogStructuredEvent(
                StructuredLogLevel.Warning,
                "gear_change.timeout",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("pulse", pulseNumber),
                LogField("beforeGear", before),
                LogField("currentGear", NormalizeGear(_currentGear)),
                LogField("elapsedMs", timeout.Elapsed.TotalMilliseconds),
                LogField("telemetryAgeMs", GetTelemetryAgeMs()));
            return false;
        }

        private async Task PulseKeyAsync(
            ushort virtualKey,
            long operationId,
            string source,
            string logicalAction,
            int pulseNumber,
            string beforeGear,
            CancellationToken token)
        {
            Stopwatch gateTimer = Stopwatch.StartNew();
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "pulse.key_gate_wait_started",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("logicalAction", logicalAction),
                LogField("pulse", pulseNumber),
                LogField("virtualKey", FormatVirtualKey(virtualKey)));
            await _keyGate.WaitAsync(token).ConfigureAwait(false);
            LogStructuredEvent(
                StructuredLogLevel.Info,
                "pulse.key_gate_acquired",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("logicalAction", logicalAction),
                LogField("pulse", pulseNumber),
                LogField("virtualKey", FormatVirtualKey(virtualKey)),
                LogField("waitElapsedMs", gateTimer.Elapsed.TotalMilliseconds));

            bool keyIsDown = false;
            Stopwatch holdTimer = null;

            try
            {
                bool dirtForeground = ForegroundIsDirtRally2();
                LogStructuredEvent(
                    dirtForeground ? StructuredLogLevel.Info : StructuredLogLevel.Warning,
                    "pulse.foreground_checked",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("logicalAction", logicalAction),
                    LogField("pulse", pulseNumber),
                    LogField("dirtForeground", dirtForeground),
                    LogField("currentGear", NormalizeGear(_currentGear)));

                if (!dirtForeground)
                {
                    throw new InvalidOperationException(
                        "DiRT Rally 2.0 lost foreground focus before key injection.");
                }

                try
                {
                    SendKeyState(virtualKey, false);
                }
                catch (Exception ex)
                {
                    LogStructuredEvent(
                        StructuredLogLevel.Error,
                        "pulse.key_down_failed",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("logicalAction", logicalAction),
                        LogField("pulse", pulseNumber),
                        LogField("virtualKey", FormatVirtualKey(virtualKey)),
                        LogField("exceptionType", ex.GetType().FullName),
                        LogField("exceptionMessage", BoundStatus(ex.Message)));
                    throw;
                }

                keyIsDown = true;
                holdTimer = Stopwatch.StartNew();
                LogStructuredEvent(
                    StructuredLogLevel.Info,
                    "pulse.key_down_sent",
                    LogField("operationId", operationId),
                    LogField("source", source),
                    LogField("logicalAction", logicalAction),
                    LogField("pulse", pulseNumber),
                    LogField("virtualKey", FormatVirtualKey(virtualKey)),
                    LogField("virtualKeyCode", virtualKey),
                    LogField("beforeGear", beforeGear));
                await Task.Delay(KeyHoldMs, token).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (keyIsDown)
                    {
                        try
                        {
                            SendKeyState(virtualKey, true);
                            LogStructuredEvent(
                                StructuredLogLevel.Info,
                                "pulse.key_up_sent",
                                LogField("operationId", operationId),
                                LogField("source", source),
                                LogField("logicalAction", logicalAction),
                                LogField("pulse", pulseNumber),
                                LogField("virtualKey", FormatVirtualKey(virtualKey)),
                                LogField("virtualKeyCode", virtualKey),
                                LogField(
                                    "holdElapsedMs",
                                    holdTimer == null
                                        ? 0
                                        : holdTimer.Elapsed.TotalMilliseconds));
                        }
                        catch (Exception ex)
                        {
                            LogStructuredEvent(
                                StructuredLogLevel.Error,
                                "pulse.key_up_failed",
                                LogField("operationId", operationId),
                                LogField("source", source),
                                LogField("logicalAction", logicalAction),
                                LogField("pulse", pulseNumber),
                                LogField("virtualKey", FormatVirtualKey(virtualKey)),
                                LogField("exceptionType", ex.GetType().FullName),
                                LogField("exceptionMessage", BoundStatus(ex.Message)));
                            throw;
                        }
                    }
                }
                finally
                {
                    _keyGate.Release();
                    LogStructuredEvent(
                        StructuredLogLevel.Info,
                        "pulse.key_gate_released",
                        LogField("operationId", operationId),
                        LogField("source", source),
                        LogField("logicalAction", logicalAction),
                        LogField("pulse", pulseNumber),
                        LogField("virtualKey", FormatVirtualKey(virtualKey)),
                        LogField("totalElapsedMs", gateTimer.Elapsed.TotalMilliseconds));
                }
            }
        }

        private NeutralConditionSnapshot EvaluateNeutralConditions()
        {
            NeutralConditionSnapshot conditions = new NeutralConditionSnapshot
            {
                DirtForeground = ForegroundIsDirtRally2(),
                TelemetryIsDirtRally2 = _telemetryIsDirtRally2,
                GameRunning = _gameRunning,
                TelemetryAgeMs = GetTelemetryAgeMs(),
                CurrentGear = NormalizeGear(_currentGear)
            };
            conditions.GearKnown = IsKnownGear(conditions.CurrentGear);

            if (!conditions.DirtForeground)
            {
                conditions.ReasonCode = "dirt_not_foreground";
                conditions.Reason = "DiRT Rally 2.0 is not foreground";
            }
            else if (!conditions.TelemetryIsDirtRally2)
            {
                conditions.ReasonCode = "telemetry_source_mismatch";
                conditions.Reason = "SimHub is not receiving DiRT Rally 2.0 telemetry";
            }
            else if (!conditions.GameRunning)
            {
                conditions.ReasonCode = "game_telemetry_not_running";
                conditions.Reason = "game telemetry is not running";
            }
            else if (conditions.TelemetryAgeMs < 0 ||
                     conditions.TelemetryAgeMs > TelemetryFreshMs)
            {
                conditions.ReasonCode = "telemetry_stale";
                conditions.Reason = "telemetry is stale (" +
                                    Math.Round(conditions.TelemetryAgeMs, 0)
                                        .ToString(CultureInfo.InvariantCulture) +
                                    " ms)";
            }
            else if (!conditions.GearKnown)
            {
                conditions.ReasonCode = "current_gear_unavailable";
                conditions.Reason =
                    "current gear is unavailable ('" + conditions.CurrentGear + "')";
            }
            else
            {
                conditions.Allowed = true;
                conditions.ReasonCode = "allowed";
                conditions.Reason = "";
            }

            return conditions;
        }

        private void LogNeutralConditions(
            long operationId,
            string source,
            string phase,
            int pulseNumber,
            NeutralConditionSnapshot conditions)
        {
            LogStructuredEvent(
                conditions.Allowed
                    ? StructuredLogLevel.Info
                    : StructuredLogLevel.Warning,
                "neutral.conditions_checked",
                LogField("operationId", operationId),
                LogField("source", source),
                LogField("phase", phase),
                LogField("pulse", pulseNumber),
                LogField("dirtForeground", conditions.DirtForeground),
                LogField("telemetryIsDirtRally2", conditions.TelemetryIsDirtRally2),
                LogField("gameRunning", conditions.GameRunning),
                LogField("telemetryAgeMs", conditions.TelemetryAgeMs),
                LogField("currentGear", conditions.CurrentGear),
                LogField("gearKnown", conditions.GearKnown),
                LogField("allowed", conditions.Allowed),
                LogField("reason", conditions.ReasonCode),
                LogField("busy", Busy),
                LogField("shiftPending", Volatile.Read(ref _singleShiftPending) != 0),
                LogField("calibrationActive", CalibrationActive));
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
                LogStructuredEvent(
                    StructuredLogLevel.Error,
                    "settings.save_failed",
                    LogField("exceptionType", ex.GetType().FullName),
                    LogField("exceptionMessage", BoundStatus(ex.Message)));
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
                LogStructuredEvent(
                    StructuredLogLevel.Info,
                    "input.status_changed",
                    LogField("previousStatus", previous),
                    LogField("status", bounded),
                    LogField("connected", connected));
            }
        }

        private void SetCalibrationStatus(string status)
        {
            string bounded = BoundStatus(status);
            string previous = _calibrationStatus;
            _calibrationStatus = bounded;

            if (!string.Equals(previous, bounded, StringComparison.Ordinal))
            {
                LogStructuredEvent(
                    StructuredLogLevel.Info,
                    "calibration.status_changed",
                    LogField("previousStatus", previous),
                    LogField("status", bounded),
                    LogField("active", CalibrationActive));
            }
        }

        private long NextOperationId()
        {
            return Interlocked.Increment(ref _nextOperationId);
        }

        private static StructuredLogField LogField(string name, object value)
        {
            return StructuredEventFormatter.Field(name, value);
        }

        private void SetLastResult(
            StructuredLogLevel level,
            string eventName,
            string result,
            params StructuredLogField[] fields)
        {
            string boundedResult = BoundStatus(result);
            _lastResult = boundedResult;

            int existingCount = fields == null ? 0 : fields.Length;
            StructuredLogField[] eventFields =
                new StructuredLogField[existingCount + 1];
            if (existingCount > 0)
            {
                Array.Copy(fields, eventFields, existingCount);
            }

            eventFields[existingCount] = LogField("result", boundedResult);
            LogStructuredEvent(level, eventName, eventFields);
        }

        private void LogStructuredEvent(
            StructuredLogLevel level,
            string eventName,
            params StructuredLogField[] fields)
        {
            try
            {
                string message = "[DR2Neutral.Event] " +
                                 StructuredEventFormatter.Format(
                                     DateTime.UtcNow,
                                     eventName,
                                     fields);

                switch (level)
                {
                    case StructuredLogLevel.Warning:
                        SimHub.Logging.Current.Warn(message);
                        break;
                    case StructuredLogLevel.Error:
                        SimHub.Logging.Current.Error(message);
                        break;
                    default:
                        SimHub.Logging.Current.Info(message);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(
                        ref _structuredLoggingFailureReported,
                        1) == 0)
                {
                    try
                    {
                        SimHub.Logging.Current.Error(
                            "[DR2Neutral] Structured event logging failed; " +
                            "further logger errors are suppressed: " +
                            BoundStatus(ex.Message));
                    }
                    catch
                    {
                        // Logging must never interfere with controller input or key-up safety.
                    }
                }
            }
        }

        private static int GetMappedButtonIndex(
            DR2NeutralSettings settings,
            ControllerActionKind action)
        {
            switch (action)
            {
                case ControllerActionKind.GearUp:
                    return settings.GearUpButton;
                case ControllerActionKind.GearDown:
                    return settings.GearDownButton;
                case ControllerActionKind.SelectNeutral:
                    return settings.SelectNeutralButton;
                default:
                    return DR2NeutralSettings.UnassignedButton;
            }
        }

        private static string GetControllerActionName(ControllerActionKind action)
        {
            switch (action)
            {
                case ControllerActionKind.GearUp:
                    return "gear_up";
                case ControllerActionKind.GearDown:
                    return "gear_down";
                case ControllerActionKind.SelectNeutral:
                    return "select_neutral";
                case ControllerActionKind.RejectedSimultaneous:
                    return "rejected_simultaneous";
                case ControllerActionKind.None:
                    return "none";
                default:
                    return "unsupported";
            }
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
