using SharpDX.DirectInput;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DR2NeutralPlugin
{
    internal sealed class ControllerCalibrationResult
    {
        public bool Success { get; private set; }
        public string Message { get; private set; }
        public string ControllerInstanceGuid { get; private set; }
        public string ControllerProductGuid { get; private set; }
        public string ControllerName { get; private set; }
        public int GearUpButton { get; private set; }
        public int GearDownButton { get; private set; }
        public int SelectNeutralButton { get; private set; }

        private ControllerCalibrationResult()
        {
        }

        public static ControllerCalibrationResult Completed(
            DeviceInstance device,
            int gearUpButton,
            int gearDownButton,
            int selectNeutralButton)
        {
            return new ControllerCalibrationResult
            {
                Success = true,
                Message = "Calibration complete.",
                ControllerInstanceGuid = device.InstanceGuid.ToString("D"),
                ControllerProductGuid = device.ProductGuid.ToString("D"),
                ControllerName = ControllerInputManager.GetDeviceName(device),
                GearUpButton = gearUpButton,
                GearDownButton = gearDownButton,
                SelectNeutralButton = selectNeutralButton
            };
        }

        public static ControllerCalibrationResult Failed(string message)
        {
            return new ControllerCalibrationResult
            {
                Success = false,
                Message = message ?? "Calibration failed.",
                ControllerInstanceGuid = "",
                ControllerProductGuid = "",
                ControllerName = "",
                GearUpButton = DR2NeutralSettings.UnassignedButton,
                GearDownButton = DR2NeutralSettings.UnassignedButton,
                SelectNeutralButton = DR2NeutralSettings.UnassignedButton
            };
        }
    }

    internal sealed class ControllerInputManager
    {
        internal const int PollIntervalMs = 10;
        internal const int StableStateMs = 250;
        internal const int StableStateTimeoutMs = 10000;
        internal const int CaptureStepTimeoutMs = 60000;
        internal const int ReconnectDelayMs = 2000;
        internal const int MaximumControllers = 32;

        private readonly Func<DR2NeutralSettings> _settingsProvider;
        private readonly Func<long> _settingsRevisionProvider;
        private readonly Func<bool> _calibrationActiveProvider;
        private readonly Action<ControllerActionKind> _actionSink;
        private readonly Action<string, bool> _statusSink;

        public ControllerInputManager(
            Func<DR2NeutralSettings> settingsProvider,
            Func<long> settingsRevisionProvider,
            Func<bool> calibrationActiveProvider,
            Action<ControllerActionKind> actionSink,
            Action<string, bool> statusSink)
        {
            if (settingsProvider == null)
            {
                throw new ArgumentNullException("settingsProvider");
            }
            if (settingsRevisionProvider == null)
            {
                throw new ArgumentNullException("settingsRevisionProvider");
            }
            if (calibrationActiveProvider == null)
            {
                throw new ArgumentNullException("calibrationActiveProvider");
            }
            if (actionSink == null)
            {
                throw new ArgumentNullException("actionSink");
            }
            if (statusSink == null)
            {
                throw new ArgumentNullException("statusSink");
            }

            _settingsProvider = settingsProvider;
            _settingsRevisionProvider = settingsRevisionProvider;
            _calibrationActiveProvider = calibrationActiveProvider;
            _actionSink = actionSink;
            _statusSink = statusSink;
        }

        public async Task RunAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                if (_calibrationActiveProvider())
                {
                    _statusSink("Calibration in progress", false);
                    await Task.Delay(StableStateMs, token).ConfigureAwait(false);
                    continue;
                }

                DR2NeutralSettings settings = _settingsProvider();
                if (!settings.BuiltInControllerEnabled)
                {
                    _statusSink("Built-in controller input disabled", false);
                    await Task.Delay(StableStateMs, token).ConfigureAwait(false);
                    continue;
                }

                if (!settings.IsCalibrated)
                {
                    _statusSink("Not calibrated", false);
                    await Task.Delay(StableStateMs, token).ConfigureAwait(false);
                    continue;
                }

                long revision = _settingsRevisionProvider();

                try
                {
                    await RunControllerSessionAsync(settings, revision, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _statusSink(
                        "Controller unavailable; retrying: " + BoundError(ex),
                        false);
                    await Task.Delay(ReconnectDelayMs, token).ConfigureAwait(false);
                }
            }
        }

        public static async Task<ControllerCalibrationResult> CalibrateAsync(
            Action<string> progress,
            CancellationToken token)
        {
            Action<string> report = progress ?? delegate { };

            try
            {
                using (DirectInput directInput = new DirectInput())
                {
                    List<OpenJoystick> devices = OpenCaptureDevices(directInput);
                    try
                    {
                        if (devices.Count == 0)
                        {
                            return ControllerCalibrationResult.Failed(
                                "No usable DirectInput controller was found.");
                        }

                        report("Release all controller buttons and the shifter.");
                        await WaitForAllButtonsReleasedAsync(
                                devices,
                                StableStateTimeoutMs,
                                token)
                            .ConfigureAwait(false);

                        List<CapturedButton> captured = new List<CapturedButton>(3);

                        CapturedButton gearUp = await CaptureButtonAsync(
                                devices,
                                null,
                                captured,
                                "Pull the sequential lever toward you once.",
                                report,
                                token)
                            .ConfigureAwait(false);
                        captured.Add(gearUp);

                        CapturedButton gearDown = await CaptureButtonAsync(
                                devices,
                                gearUp.Device,
                                captured,
                                "Push the sequential lever away from you once.",
                                report,
                                token)
                            .ConfigureAwait(false);
                        captured.Add(gearDown);

                        CapturedButton selectNeutral = await CaptureButtonAsync(
                                devices,
                                gearUp.Device,
                                captured,
                                "Pull the reverse/neutral trigger once.",
                                report,
                                token)
                            .ConfigureAwait(false);
                        captured.Add(selectNeutral);

                        report("Saving calibration...");
                        return ControllerCalibrationResult.Completed(
                            gearUp.Device.Instance,
                            gearUp.ButtonIndex,
                            gearDown.ButtonIndex,
                            selectNeutral.ButtonIndex);
                    }
                    finally
                    {
                        foreach (OpenJoystick device in devices)
                        {
                            device.Dispose();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                return ControllerCalibrationResult.Failed(ex.Message);
            }
            catch (Exception ex)
            {
                return ControllerCalibrationResult.Failed(
                    "Calibration failed: " + BoundError(ex));
            }
        }

        private async Task RunControllerSessionAsync(
            DR2NeutralSettings settings,
            long revision,
            CancellationToken token)
        {
            Guid instanceGuid;
            if (!Guid.TryParse(settings.ControllerInstanceGuid, out instanceGuid))
            {
                throw new InvalidOperationException("Saved controller identity is invalid.");
            }

            using (DirectInput directInput = new DirectInput())
            {
                DeviceInstance instance = FindSavedDevice(directInput, settings, instanceGuid);
                if (instance == null)
                {
                    throw new InvalidOperationException(
                        "'" + settings.ControllerName + "' is disconnected.");
                }

                using (Joystick joystick = new Joystick(directInput, instance.InstanceGuid))
                {
                    joystick.Properties.BufferSize = DR2NeutralSettings.MaximumButtonCount;
                    joystick.Acquire();

                    bool[] initialButtons = ReadButtons(joystick);
                    MappedButtonEdgeDetector detector = new MappedButtonEdgeDetector(
                        settings,
                        initialButtons);

                    _statusSink("Connected: " + GetDeviceName(instance), true);

                    while (_settingsRevisionProvider() == revision &&
                           !_calibrationActiveProvider())
                    {
                        token.ThrowIfCancellationRequested();

                        ControllerActionKind action = detector.Process(ReadButtons(joystick));
                        if (action != ControllerActionKind.None)
                        {
                            _actionSink(action);
                        }

                        await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
                    }
                }
            }
        }

        private static List<OpenJoystick> OpenCaptureDevices(DirectInput directInput)
        {
            List<DeviceInstance> instances = GetAttachedDevices(directInput)
                .OrderBy(GetDeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.InstanceGuid)
                .Take(MaximumControllers)
                .ToList();

            List<OpenJoystick> opened = new List<OpenJoystick>(instances.Count);
            foreach (DeviceInstance instance in instances)
            {
                Joystick joystick = null;
                try
                {
                    joystick = new Joystick(directInput, instance.InstanceGuid);
                    joystick.Properties.BufferSize = DR2NeutralSettings.MaximumButtonCount;
                    joystick.Acquire();
                    bool[] buttons = ReadButtons(joystick);
                    opened.Add(new OpenJoystick(instance, joystick, buttons));
                    joystick = null;
                }
                catch
                {
                    if (joystick != null)
                    {
                        joystick.Dispose();
                    }
                }
            }

            return opened;
        }

        private static async Task<CapturedButton> CaptureButtonAsync(
            IList<OpenJoystick> devices,
            OpenJoystick requiredDevice,
            IList<CapturedButton> existing,
            string prompt,
            Action<string> progress,
            CancellationToken token)
        {
            progress(prompt);
            Stopwatch timeout = Stopwatch.StartNew();

            while (timeout.ElapsedMilliseconds < CaptureStepTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                List<CapturedButton> rising = ReadRisingEdges(devices);

                if (rising.Count > 1)
                {
                    progress("Multiple buttons changed together. Release everything and try that step again.");
                    await WaitForAllButtonsReleasedAsync(
                            devices,
                            RemainingMilliseconds(timeout, CaptureStepTimeoutMs),
                            token)
                        .ConfigureAwait(false);
                    progress(prompt);
                    continue;
                }

                if (rising.Count == 1)
                {
                    CapturedButton candidate = rising[0];

                    if (requiredDevice != null && candidate.Device != requiredDevice)
                    {
                        progress(
                            "Use the same controller ('" +
                            GetDeviceName(requiredDevice.Instance) +
                            "') for all three controls.");
                        await WaitForAllButtonsReleasedAsync(
                                devices,
                                RemainingMilliseconds(timeout, CaptureStepTimeoutMs),
                                token)
                            .ConfigureAwait(false);
                        progress(prompt);
                        continue;
                    }

                    if (existing.Any(item =>
                            item.Device == candidate.Device &&
                            item.ButtonIndex == candidate.ButtonIndex))
                    {
                        progress(
                            "That button is already assigned. Release it and use a different control.");
                        await WaitForAllButtonsReleasedAsync(
                                devices,
                                RemainingMilliseconds(timeout, CaptureStepTimeoutMs),
                                token)
                            .ConfigureAwait(false);
                        progress(prompt);
                        continue;
                    }

                    progress(
                        "Captured " +
                        FormatButton(candidate.ButtonIndex) +
                        " on " +
                        GetDeviceName(candidate.Device.Instance) +
                        ". Release it.");

                    await WaitForButtonReleaseAsync(
                            candidate,
                            timeout,
                            token)
                        .ConfigureAwait(false);
                    return candidate;
                }

                await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for the prompted controller input.");
        }

        private static async Task WaitForButtonReleaseAsync(
            CapturedButton captured,
            Stopwatch stepTimeout,
            CancellationToken token)
        {
            Stopwatch stable = Stopwatch.StartNew();

            while (stepTimeout.ElapsedMilliseconds < CaptureStepTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                bool[] buttons = ReadButtons(captured.Device.Joystick);
                captured.Device.SetPrevious(buttons);

                if (buttons[captured.ButtonIndex])
                {
                    stable.Restart();
                }
                else if (stable.ElapsedMilliseconds >= StableStateMs)
                {
                    return;
                }

                await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for the captured control to be released.");
        }

        private static async Task WaitForAllButtonsReleasedAsync(
            IList<OpenJoystick> devices,
            int timeoutMilliseconds,
            CancellationToken token)
        {
            if (timeoutMilliseconds <= 0)
            {
                throw new TimeoutException("Timed out waiting for stable controller input.");
            }

            Stopwatch timeout = Stopwatch.StartNew();
            Stopwatch stable = Stopwatch.StartNew();

            while (timeout.ElapsedMilliseconds < timeoutMilliseconds)
            {
                token.ThrowIfCancellationRequested();
                bool anyPressed = false;

                foreach (OpenJoystick device in devices)
                {
                    bool[] current = ReadButtons(device.Joystick);
                    if (current.Any(pressed => pressed))
                    {
                        anyPressed = true;
                    }
                    device.SetPrevious(current);
                }

                if (anyPressed)
                {
                    stable.Restart();
                }
                else if (stable.ElapsedMilliseconds >= StableStateMs)
                {
                    return;
                }

                await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for all controller buttons to be released.");
        }

        private static List<CapturedButton> ReadRisingEdges(IList<OpenJoystick> devices)
        {
            List<CapturedButton> rising = new List<CapturedButton>();

            foreach (OpenJoystick device in devices)
            {
                bool[] current = ReadButtons(device.Joystick);
                bool[] previous = device.PreviousButtons;

                for (int button = 0; button < current.Length; button++)
                {
                    if (current[button] && !previous[button])
                    {
                        rising.Add(new CapturedButton(device, button));
                    }
                }

                device.SetPrevious(current);
            }

            return rising;
        }

        private static DeviceInstance FindSavedDevice(
            DirectInput directInput,
            DR2NeutralSettings settings,
            Guid instanceGuid)
        {
            List<DeviceInstance> devices = GetAttachedDevices(directInput)
                .Take(MaximumControllers)
                .ToList();

            DeviceInstance exact = devices.FirstOrDefault(
                item => item.InstanceGuid == instanceGuid);
            if (exact != null)
            {
                return exact;
            }

            Guid productGuid;
            if (!Guid.TryParse(settings.ControllerProductGuid, out productGuid))
            {
                return null;
            }

            List<DeviceInstance> fallback = devices.Where(item =>
                    item.ProductGuid == productGuid &&
                    string.Equals(
                        GetDeviceName(item),
                        settings.ControllerName,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            return fallback.Count == 1 ? fallback[0] : null;
        }

        private static IEnumerable<DeviceInstance> GetAttachedDevices(DirectInput directInput)
        {
            return directInput.GetDevices(
                DeviceClass.GameControl,
                DeviceEnumerationFlags.AttachedOnly);
        }

        private static bool[] ReadButtons(Joystick joystick)
        {
            joystick.Poll();
            bool[] buttons = joystick.GetCurrentState().Buttons;

            if (buttons == null ||
                buttons.Length == 0 ||
                buttons.Length > DR2NeutralSettings.MaximumButtonCount)
            {
                throw new InvalidOperationException(
                    "Controller reported an invalid button array length.");
            }

            return buttons;
        }

        internal static string GetDeviceName(DeviceInstance instance)
        {
            string instanceName = (instance.InstanceName ?? "").Trim();
            string productName = (instance.ProductName ?? "").Trim();
            string name = !string.IsNullOrEmpty(instanceName)
                ? instanceName
                : productName;
            return string.IsNullOrEmpty(name) ? "DirectInput controller" : BoundText(name, 128);
        }

        internal static string FormatButton(int zeroBasedButtonIndex)
        {
            return "Button " + (zeroBasedButtonIndex + 1) +
                   " (index " + zeroBasedButtonIndex + ")";
        }

        private static int RemainingMilliseconds(Stopwatch timeout, int maximumMilliseconds)
        {
            long remaining = maximumMilliseconds - timeout.ElapsedMilliseconds;
            return remaining <= 0 ? 0 : (int)Math.Min(remaining, int.MaxValue);
        }

        private static string BoundError(Exception ex)
        {
            return BoundText(ex == null ? "unknown error" : ex.Message, 180);
        }

        private static string BoundText(string value, int maximumLength)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
            return text.Length <= maximumLength
                ? text
                : text.Substring(0, maximumLength);
        }

        private sealed class OpenJoystick : IDisposable
        {
            public DeviceInstance Instance { get; private set; }
            public Joystick Joystick { get; private set; }
            public bool[] PreviousButtons { get; private set; }

            public OpenJoystick(
                DeviceInstance instance,
                Joystick joystick,
                bool[] previousButtons)
            {
                Instance = instance;
                Joystick = joystick;
                PreviousButtons = previousButtons;
            }

            public void SetPrevious(bool[] buttons)
            {
                PreviousButtons = buttons;
            }

            public void Dispose()
            {
                Joystick.Dispose();
            }
        }

        private sealed class CapturedButton
        {
            public OpenJoystick Device { get; private set; }
            public int ButtonIndex { get; private set; }

            public CapturedButton(OpenJoystick device, int buttonIndex)
            {
                Device = device;
                ButtonIndex = buttonIndex;
            }
        }
    }
}
