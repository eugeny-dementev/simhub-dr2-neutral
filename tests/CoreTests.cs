using DR2NeutralPlugin;
using System;
using System.Reflection;
using System.Windows.Controls;

namespace DR2NeutralPlugin.Tests
{
    internal static class CoreTests
    {
        private static int _assertions;

        [STAThread]
        private static int Main()
        {
            try
            {
                TestSettingsDefaultsAndValidation();
                TestMappedButtonEdges();
                TestGearNormalizationAndNeutralRouting();
                TestGameDetection();
                TestStructuredEventFormatting();
                TestSettingsUiConstruction();
                TestAssemblyMetadata();

                Console.WriteLine("DR2Neutral core tests passed: " + _assertions + " assertions.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("DR2Neutral core test failure: " + ex);
                return 1;
            }
        }

        private static void TestSettingsDefaultsAndValidation()
        {
            DR2NeutralSettings settings = new DR2NeutralSettings();
            Equal(DR2NeutralSettings.DefaultGearUpVirtualKey, settings.GearUpVirtualKey, "default up key");
            Equal(DR2NeutralSettings.DefaultGearDownVirtualKey, settings.GearDownVirtualKey, "default down key");
            True(settings.BuiltInControllerEnabled, "built-in input defaults enabled");
            False(settings.IsCalibrated, "defaults are not calibrated");

            settings.ControllerInstanceGuid = Guid.NewGuid().ToString("D");
            settings.ControllerProductGuid = Guid.NewGuid().ToString("D");
            settings.ControllerName = "Test controller";
            settings.GearUpButton = 1;
            settings.GearDownButton = 2;
            settings.SelectNeutralButton = 3;
            Equal("", settings.NormalizeAndValidate(), "valid settings produce no warning");
            True(settings.IsCalibrated, "three distinct buttons are calibrated");

            DR2NeutralSettings clone = settings.Clone();
            clone.GearUpButton = 7;
            Equal(1, settings.GearUpButton, "clone mutation does not affect source");

            settings.GearDownButton = settings.GearUpButton;
            True(settings.NormalizeAndValidate().Length > 0, "duplicate calibration is rejected");
            False(settings.IsCalibrated, "invalid calibration is cleared");
            Equal(DR2NeutralSettings.UnassignedButton, settings.GearUpButton, "clear is total");
        }

        private static void TestMappedButtonEdges()
        {
            DR2NeutralSettings settings = CalibratedSettings(1, 2, 3);
            bool[] buttons = new bool[8];
            MappedButtonEdgeDetector detector = new MappedButtonEdgeDetector(settings, buttons);

            buttons = new bool[8];
            buttons[1] = true;
            Equal(ControllerActionKind.GearUp, detector.Process(buttons), "single up rising edge");
            Equal(ControllerActionKind.None, detector.Process(buttons), "held button does not repeat");

            buttons = new bool[8];
            Equal(ControllerActionKind.None, detector.Process(buttons), "release has no action");
            buttons[2] = true;
            Equal(ControllerActionKind.GearDown, detector.Process(buttons), "single down rising edge");

            buttons = new bool[8];
            detector.Process(buttons);
            buttons[1] = true;
            buttons[2] = true;
            Equal(
                ControllerActionKind.RejectedSimultaneous,
                detector.Process(buttons),
                "simultaneous mapped buttons are rejected");
            Equal(ControllerActionKind.None, detector.Process(buttons), "detector disarms after rejection");

            buttons = new bool[8];
            Equal(ControllerActionKind.None, detector.Process(buttons), "release rearms detector");
            buttons[3] = true;
            Equal(
                ControllerActionKind.SelectNeutral,
                detector.Process(buttons),
                "neutral trigger rising edge");

            buttons = new bool[8];
            buttons[1] = true;
            detector = new MappedButtonEdgeDetector(settings, buttons);
            Equal(ControllerActionKind.None, detector.Process(buttons), "held startup input is not emitted");
            buttons = new bool[8];
            detector.Process(buttons);
            buttons[1] = true;
            Equal(ControllerActionKind.GearUp, detector.Process(buttons), "startup input works after release");
        }

        private static void TestGearNormalizationAndNeutralRouting()
        {
            Equal("N", DR2Neutral.NormalizeGear(" neutral "), "neutral normalization");
            Equal("N", DR2Neutral.NormalizeGear("0"), "zero normalization");
            Equal("R", DR2Neutral.NormalizeGear("-1"), "reverse normalization");
            Equal("4", DR2Neutral.NormalizeGear("4"), "forward gear normalization");

            int up = DR2NeutralSettings.DefaultGearUpVirtualKey;
            int down = DR2NeutralSettings.DefaultGearDownVirtualKey;
            Equal(0, DR2Neutral.GetNeutralVirtualKey("N", up, down), "neutral sends no key");
            Equal(up, DR2Neutral.GetNeutralVirtualKey("R", up, down), "reverse routes up");
            Equal(down, DR2Neutral.GetNeutralVirtualKey("4", up, down), "forward gear routes down");
            Equal(-1, DR2Neutral.GetNeutralVirtualKey("?", up, down), "unknown gear aborts");
        }

        private static void TestGameDetection()
        {
            True(DR2Neutral.LooksLikeDirtRally2("DiRT Rally 2.0"), "display name detection");
            True(DR2Neutral.LooksLikeDirtRally2("dirtrally2.exe"), "process name detection");
            False(DR2Neutral.LooksLikeDirtRally2("EA Sports WRC"), "other game rejection");
            False(DR2Neutral.LooksLikeDirtRally2(""), "empty game rejection");
        }

        private static void TestStructuredEventFormatting()
        {
            DateTime timestamp = new DateTime(
                    2026,
                    8,
                    9,
                    1,
                    2,
                    3,
                    DateTimeKind.Utc)
                .AddTicks(4567890);
            string json = StructuredEventFormatter.Format(
                timestamp,
                "neutral.conditions_checked",
                StructuredEventFormatter.Field("operationId", 42L),
                StructuredEventFormatter.Field("allowed", false),
                StructuredEventFormatter.Field("reason", "focus\"lost\nnow"),
                StructuredEventFormatter.Field("telemetryAgeMs", 12.5));

            Equal(
                "{\"timestampUtc\":\"2026-08-09T01:02:03.4567890Z\"," +
                "\"event\":\"neutral.conditions_checked\"," +
                "\"operationId\":42,\"allowed\":false," +
                "\"reason\":\"focus\\\"lost\\nnow\",\"telemetryAgeMs\":12.5}",
                json,
                "structured JSON is deterministic and escaped");

            string longValue = new string('x', 241);
            string bounded = StructuredEventFormatter.Format(
                timestamp,
                "bounded",
                StructuredEventFormatter.Field("value", longValue));
            True(bounded.Contains(new string('x', 240)), "structured strings keep bounded content");
            False(bounded.Contains(new string('x', 241)), "structured strings are bounded");

            string nonFinite = StructuredEventFormatter.Format(
                timestamp,
                "number",
                StructuredEventFormatter.Field("value", double.NaN));
            True(nonFinite.Contains("\"value\":null"), "non-finite JSON numbers become null");

            StructuredLogField[] tooMany = new StructuredLogField[25];
            for (int index = 0; index < tooMany.Length; index++)
            {
                tooMany[index] = StructuredEventFormatter.Field("field" + index, index);
            }

            Throws<ArgumentOutOfRangeException>(
                () => StructuredEventFormatter.Format(timestamp, "bounded", tooMany),
                "structured field count is bounded");
            Throws<ArgumentException>(
                () => StructuredEventFormatter.Format(
                    timestamp,
                    "bounded",
                    StructuredEventFormatter.Field("invalid field", 1)),
                "structured field names are validated");
        }

        private static void TestSettingsUiConstruction()
        {
            DR2Neutral plugin = new DR2Neutral();
            DR2NeutralSettingsControl control = new DR2NeutralSettingsControl(plugin);
            ScrollViewer scrollViewer = control.Content as ScrollViewer;
            True(scrollViewer != null, "settings UI root is scrollable");
            StackPanel page = scrollViewer == null ? null : scrollViewer.Content as StackPanel;
            True(page != null && page.Children.Count >= 10, "settings UI contains setup sections");
        }

        private static void TestAssemblyMetadata()
        {
            Assembly assembly = typeof(DR2Neutral).Assembly;
            Equal(new Version(1, 1, 0, 0), assembly.GetName().Version, "assembly version");
            Equal("DR2Neutral", assembly.GetName().Name, "assembly name");
            True(typeof(SimHub.Plugins.IWPFSettingsV2).IsAssignableFrom(typeof(DR2Neutral)), "settings interface");
        }

        private static DR2NeutralSettings CalibratedSettings(int up, int down, int neutral)
        {
            return new DR2NeutralSettings
            {
                ControllerInstanceGuid = Guid.NewGuid().ToString("D"),
                ControllerProductGuid = Guid.NewGuid().ToString("D"),
                ControllerName = "Test controller",
                GearUpButton = up,
                GearDownButton = down,
                SelectNeutralButton = neutral
            };
        }

        private static void True(bool condition, string name)
        {
            _assertions++;
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + name);
            }
        }

        private static void False(bool condition, string name)
        {
            True(!condition, name);
        }

        private static void Equal<T>(T expected, T actual, string name)
        {
            _assertions++;
            if (!Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    "Assertion failed: " + name +
                    ". Expected '" + expected + "', actual '" + actual + "'.");
            }
        }

        private static void Throws<TException>(Action action, string name)
            where TException : Exception
        {
            _assertions++;
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(
                "Assertion failed: " + name + ". Expected " +
                typeof(TException).FullName + ".");
        }
    }
}
