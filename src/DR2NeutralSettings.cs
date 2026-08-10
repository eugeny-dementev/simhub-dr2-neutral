using System;
using System.Collections.Generic;

namespace DR2NeutralPlugin
{
    public sealed class DR2NeutralSettings
    {
        public const int CurrentVersion = 2;
        public const int UnassignedButton = -1;
        public const int MaximumButtonCount = 128;
        public const int DefaultGearUpVirtualKey = 0x76;
        public const int DefaultGearDownVirtualKey = 0x77;
        public const int MinimumNeutralPulseIntervalMs = 50;
        public const int MaximumNeutralPulseIntervalMs = 200;
        public const int DefaultNeutralPulseIntervalMs = 200;

        public int Version { get; set; }
        public bool BuiltInControllerEnabled { get; set; }
        public int GearUpVirtualKey { get; set; }
        public int GearDownVirtualKey { get; set; }
        public int NeutralPulseIntervalMs { get; set; }
        public string ControllerInstanceGuid { get; set; }
        public string ControllerProductGuid { get; set; }
        public string ControllerName { get; set; }
        public int GearUpButton { get; set; }
        public int GearDownButton { get; set; }
        public int SelectNeutralButton { get; set; }

        public DR2NeutralSettings()
        {
            Version = CurrentVersion;
            BuiltInControllerEnabled = true;
            GearUpVirtualKey = DefaultGearUpVirtualKey;
            GearDownVirtualKey = DefaultGearDownVirtualKey;
            NeutralPulseIntervalMs = DefaultNeutralPulseIntervalMs;
            ControllerInstanceGuid = "";
            ControllerProductGuid = "";
            ControllerName = "";
            GearUpButton = UnassignedButton;
            GearDownButton = UnassignedButton;
            SelectNeutralButton = UnassignedButton;
        }

        public bool IsCalibrated
        {
            get
            {
                Guid instanceGuid;
                return Guid.TryParse(ControllerInstanceGuid, out instanceGuid) &&
                       IsButtonIndexValid(GearUpButton) &&
                       IsButtonIndexValid(GearDownButton) &&
                       IsButtonIndexValid(SelectNeutralButton) &&
                       GearUpButton != GearDownButton &&
                       GearUpButton != SelectNeutralButton &&
                       GearDownButton != SelectNeutralButton;
            }
        }

        public DR2NeutralSettings Clone()
        {
            return new DR2NeutralSettings
            {
                Version = Version,
                BuiltInControllerEnabled = BuiltInControllerEnabled,
                GearUpVirtualKey = GearUpVirtualKey,
                GearDownVirtualKey = GearDownVirtualKey,
                NeutralPulseIntervalMs = NeutralPulseIntervalMs,
                ControllerInstanceGuid = ControllerInstanceGuid ?? "",
                ControllerProductGuid = ControllerProductGuid ?? "",
                ControllerName = ControllerName ?? "",
                GearUpButton = GearUpButton,
                GearDownButton = GearDownButton,
                SelectNeutralButton = SelectNeutralButton
            };
        }

        public void ClearControllerCalibration()
        {
            ControllerInstanceGuid = "";
            ControllerProductGuid = "";
            ControllerName = "";
            GearUpButton = UnassignedButton;
            GearDownButton = UnassignedButton;
            SelectNeutralButton = UnassignedButton;
        }

        public string NormalizeAndValidate()
        {
            string warning = "";

            int sourceVersion = Version;
            if (sourceVersion == 1)
            {
                // Version 1 had a fixed 200 ms interval. Preserve that behavior
                // regardless of how the serializer initialized the new field.
                NeutralPulseIntervalMs = DefaultNeutralPulseIntervalMs;
                Version = CurrentVersion;
            }
            else if (sourceVersion != CurrentVersion)
            {
                Version = CurrentVersion;
                warning = "Unsupported settings version was reset.";
            }

            if (!IsNeutralPulseIntervalValid(NeutralPulseIntervalMs))
            {
                int corruptValue = NeutralPulseIntervalMs;
                NeutralPulseIntervalMs = Math.Max(
                    MinimumNeutralPulseIntervalMs,
                    Math.Min(MaximumNeutralPulseIntervalMs, corruptValue));
                warning = AppendWarning(
                    warning,
                    "Neutral pulse interval " + corruptValue +
                    " ms was bounded to " + NeutralPulseIntervalMs + " ms.");
            }

            if (!IsFunctionKey(GearUpVirtualKey) ||
                !IsFunctionKey(GearDownVirtualKey) ||
                GearUpVirtualKey == GearDownVirtualKey)
            {
                GearUpVirtualKey = DefaultGearUpVirtualKey;
                GearDownVirtualKey = DefaultGearDownVirtualKey;
                warning = AppendWarning(warning, "Invalid keyboard bindings were reset to F7/F8.");
            }

            ControllerInstanceGuid = BoundText(ControllerInstanceGuid, 64);
            ControllerProductGuid = BoundText(ControllerProductGuid, 64);
            ControllerName = BoundText(ControllerName, 128);

            bool hasAnyCalibrationValue =
                !string.IsNullOrEmpty(ControllerInstanceGuid) ||
                !string.IsNullOrEmpty(ControllerProductGuid) ||
                !string.IsNullOrEmpty(ControllerName) ||
                GearUpButton != UnassignedButton ||
                GearDownButton != UnassignedButton ||
                SelectNeutralButton != UnassignedButton;

            if (hasAnyCalibrationValue && !IsCalibrated)
            {
                ClearControllerCalibration();
                warning = AppendWarning(warning, "Incomplete or duplicate controller calibration was cleared.");
            }

            return warning;
        }

        public static bool IsButtonIndexValid(int buttonIndex)
        {
            return buttonIndex >= 0 && buttonIndex < MaximumButtonCount;
        }

        public static bool IsFunctionKey(int virtualKey)
        {
            return virtualKey >= 0x70 && virtualKey <= 0x7B;
        }

        public static bool IsNeutralPulseIntervalValid(int intervalMs)
        {
            return intervalMs >= MinimumNeutralPulseIntervalMs &&
                   intervalMs <= MaximumNeutralPulseIntervalMs;
        }

        private static string BoundText(string value, int maximumLength)
        {
            string result = (value ?? "").Trim();
            return result.Length <= maximumLength
                ? result
                : result.Substring(0, maximumLength);
        }

        private static string AppendWarning(string existing, string addition)
        {
            return string.IsNullOrEmpty(existing)
                ? addition
                : existing + " " + addition;
        }
    }

    internal enum ControllerActionKind
    {
        None = 0,
        GearUp = 1,
        GearDown = 2,
        SelectNeutral = 3,
        RejectedSimultaneous = 4
    }

    internal sealed class MappedButtonEdgeDetector
    {
        private readonly int _gearUpButton;
        private readonly int _gearDownButton;
        private readonly int _selectNeutralButton;
        private readonly bool[] _previousButtons;
        private bool _armed;

        public MappedButtonEdgeDetector(
            DR2NeutralSettings settings,
            bool[] initialButtons)
        {
            if (settings == null || !settings.IsCalibrated)
            {
                throw new ArgumentException("A complete calibration is required.", "settings");
            }

            if (initialButtons == null ||
                initialButtons.Length == 0 ||
                initialButtons.Length > DR2NeutralSettings.MaximumButtonCount)
            {
                throw new ArgumentException("Controller button state is invalid.", "initialButtons");
            }

            _gearUpButton = settings.GearUpButton;
            _gearDownButton = settings.GearDownButton;
            _selectNeutralButton = settings.SelectNeutralButton;

            int highestMappedButton = Math.Max(
                _gearUpButton,
                Math.Max(_gearDownButton, _selectNeutralButton));
            if (highestMappedButton >= initialButtons.Length)
            {
                throw new ArgumentException("A mapped button is not exposed by the controller.", "initialButtons");
            }

            _previousButtons = new bool[initialButtons.Length];
            Array.Copy(initialButtons, _previousButtons, initialButtons.Length);
            _armed = AllMappedButtonsReleased(initialButtons);
        }

        public ControllerActionKind Process(bool[] currentButtons)
        {
            if (currentButtons == null || currentButtons.Length != _previousButtons.Length)
            {
                throw new ArgumentException("Controller button count changed.", "currentButtons");
            }

            if (!_armed)
            {
                _armed = AllMappedButtonsReleased(currentButtons);
                Array.Copy(currentButtons, _previousButtons, currentButtons.Length);
                return ControllerActionKind.None;
            }

            List<ControllerActionKind> risingActions = new List<ControllerActionKind>(3);
            AddRisingAction(currentButtons, _gearUpButton, ControllerActionKind.GearUp, risingActions);
            AddRisingAction(currentButtons, _gearDownButton, ControllerActionKind.GearDown, risingActions);
            AddRisingAction(
                currentButtons,
                _selectNeutralButton,
                ControllerActionKind.SelectNeutral,
                risingActions);

            Array.Copy(currentButtons, _previousButtons, currentButtons.Length);

            if (risingActions.Count > 1)
            {
                _armed = false;
                return ControllerActionKind.RejectedSimultaneous;
            }

            return risingActions.Count == 1
                ? risingActions[0]
                : ControllerActionKind.None;
        }

        private void AddRisingAction(
            bool[] currentButtons,
            int buttonIndex,
            ControllerActionKind action,
            ICollection<ControllerActionKind> actions)
        {
            if (currentButtons[buttonIndex] && !_previousButtons[buttonIndex])
            {
                actions.Add(action);
            }
        }

        private bool AllMappedButtonsReleased(bool[] buttons)
        {
            return !buttons[_gearUpButton] &&
                   !buttons[_gearDownButton] &&
                   !buttons[_selectNeutralButton];
        }
    }
}
