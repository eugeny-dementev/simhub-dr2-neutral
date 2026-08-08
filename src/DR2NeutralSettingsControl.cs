using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DR2NeutralPlugin
{
    public sealed class DR2NeutralSettingsControl : UserControl
    {
        private readonly DR2Neutral _plugin;
        private readonly DispatcherTimer _refreshTimer;
        private readonly List<FunctionKeyOption> _functionKeys;

        private CheckBox _builtInInputCheckBox;
        private ComboBox _gearUpKeyComboBox;
        private ComboBox _gearDownKeyComboBox;
        private Button _calibrateButton;
        private Button _cancelCalibrationButton;
        private Button _clearCalibrationButton;
        private TextBlock _controllerValue;
        private TextBlock _gearUpButtonValue;
        private TextBlock _gearDownButtonValue;
        private TextBlock _neutralButtonValue;
        private TextBlock _calibrationStatusValue;
        private TextBlock _inputStatusValue;
        private TextBlock _gameValue;
        private TextBlock _gearValue;
        private TextBlock _readyValue;
        private TextBlock _telemetryAgeValue;
        private TextBlock _busyValue;
        private TextBlock _lastResultValue;
        private TextBlock _localMessageValue;
        private bool _refreshing;

        public DR2NeutralSettingsControl(DR2Neutral plugin)
        {
            if (plugin == null)
            {
                throw new ArgumentNullException("plugin");
            }

            _plugin = plugin;
            _functionKeys = Enumerable.Range(1, 12)
                .Select(number => new FunctionKeyOption(0x6F + number, "F" + number))
                .ToList();

            Content = BuildContent();

            _refreshTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(250),
                DispatcherPriority.Background,
                delegate { RefreshView(); },
                Dispatcher);

            Loaded += delegate
            {
                RefreshView();
                _refreshTimer.Start();
            };
            Unloaded += delegate { _refreshTimer.Stop(); };
        }

        private FrameworkElement BuildContent()
        {
            StackPanel page = new StackPanel
            {
                Margin = new Thickness(24),
                MaxWidth = 920,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            page.Children.Add(new TextBlock
            {
                Text = "DR2 Neutral Selector",
                FontSize = 26,
                FontWeight = FontWeights.SemiBold
            });
            page.Children.Add(new TextBlock
            {
                Text = "Version " + DR2Neutral.PluginVersion +
                       " · self-contained controller calibration for DiRT Rally 2.0",
                Margin = new Thickness(0, 4, 0, 18),
                Opacity = 0.75
            });

            Border safetyNotice = new Border
            {
                BorderBrush = Brushes.DarkOrange,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 18),
                Child = new TextBlock
                {
                    Text = "Safety: select Neutral only while stopped or nearly stopped, " +
                           "preferably with the clutch held. The plug-in must shift through " +
                           "intermediate gears because DiRT Rally 2.0 has no direct sequential " +
                           "Neutral command.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 840
                }
            };
            page.Children.Add(safetyNotice);

            page.Children.Add(CreateSectionTitle("1 · Keyboard bindings in DiRT Rally 2.0"));
            page.Children.Add(new TextBlock
            {
                Text = "Under the game's Keyboard device, bind Gear Up and Gear Down to the " +
                       "same keys selected here. Remove direct Gear Up/Gear Down bindings from " +
                       "the physical sequential shifter to prevent double shifts.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            Grid keyGrid = CreateTwoColumnGrid();
            _gearUpKeyComboBox = CreateFunctionKeyComboBox();
            _gearDownKeyComboBox = CreateFunctionKeyComboBox();
            AddRow(keyGrid, 0, "Gear Up key", _gearUpKeyComboBox);
            AddRow(keyGrid, 1, "Gear Down key", _gearDownKeyComboBox);
            page.Children.Add(keyGrid);

            _gearUpKeyComboBox.SelectionChanged += KeySelectionChanged;
            _gearDownKeyComboBox.SelectionChanged += KeySelectionChanged;

            page.Children.Add(CreateSectionTitle("2 · Controller calibration"));

            _builtInInputCheckBox = new CheckBox
            {
                Content = "Use the plug-in's built-in controller input",
                Margin = new Thickness(0, 0, 0, 10)
            };
            _builtInInputCheckBox.Checked += BuiltInInputChanged;
            _builtInInputCheckBox.Unchecked += BuiltInInputChanged;
            page.Children.Add(_builtInInputCheckBox);

            page.Children.Add(new TextBlock
            {
                Text = "Click Start calibration, release all controls, then follow the three " +
                       "prompts. Any DirectInput sequential shifter is supported; MOZA hardware " +
                       "is not required. Existing assignments are kept unless all three new " +
                       "inputs are captured successfully.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            StackPanel calibrationButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12)
            };
            _calibrateButton = CreateButton("Start calibration", true);
            _cancelCalibrationButton = CreateButton("Cancel", false);
            _clearCalibrationButton = CreateButton("Clear assignments", false);
            _calibrateButton.Click += CalibrateClick;
            _cancelCalibrationButton.Click += delegate { _plugin.CancelCalibration(); };
            _clearCalibrationButton.Click += ClearCalibrationClick;
            calibrationButtons.Children.Add(_calibrateButton);
            calibrationButtons.Children.Add(_cancelCalibrationButton);
            calibrationButtons.Children.Add(_clearCalibrationButton);
            page.Children.Add(calibrationButtons);

            Grid calibrationGrid = CreateTwoColumnGrid();
            _controllerValue = CreateValueText();
            _gearUpButtonValue = CreateValueText();
            _gearDownButtonValue = CreateValueText();
            _neutralButtonValue = CreateValueText();
            _calibrationStatusValue = CreateValueText();
            AddRow(calibrationGrid, 0, "Controller", _controllerValue);
            AddRow(calibrationGrid, 1, "Pull / Gear Up", _gearUpButtonValue);
            AddRow(calibrationGrid, 2, "Push / Gear Down", _gearDownButtonValue);
            AddRow(calibrationGrid, 3, "Reverse trigger / Neutral", _neutralButtonValue);
            AddRow(calibrationGrid, 4, "Calibration", _calibrationStatusValue);
            page.Children.Add(calibrationGrid);

            page.Children.Add(CreateSectionTitle("3 · Live status"));
            Grid statusGrid = CreateTwoColumnGrid();
            _inputStatusValue = CreateValueText();
            _gameValue = CreateValueText();
            _gearValue = CreateValueText();
            _readyValue = CreateValueText();
            _telemetryAgeValue = CreateValueText();
            _busyValue = CreateValueText();
            _lastResultValue = CreateValueText();
            AddRow(statusGrid, 0, "Controller input", _inputStatusValue);
            AddRow(statusGrid, 1, "SimHub game", _gameValue);
            AddRow(statusGrid, 2, "Current gear", _gearValue);
            AddRow(statusGrid, 3, "Neutral ready", _readyValue);
            AddRow(statusGrid, 4, "Telemetry age", _telemetryAgeValue);
            AddRow(statusGrid, 5, "Neutral sequence", _busyValue);
            AddRow(statusGrid, 6, "Last result", _lastResultValue);
            page.Children.Add(statusGrid);

            _localMessageValue = new TextBlock
            {
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold
            };
            page.Children.Add(_localMessageValue);

            page.Children.Add(CreateSectionTitle("First safe test"));
            page.Children.Add(new TextBlock
            {
                Text = "Start a stage, stop the car, hold the clutch, and keep DiRT Rally 2.0 " +
                       "focused. Confirm pull/push each shift once. Then test 4 → 3 → 2 → 1 → N, " +
                       "R → N, and finally confirm that triggering from N sends no shift.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            page.Children.Add(new TextBlock
            {
                Text = "Advanced use: DR2Neutral.GearUp, DR2Neutral.GearDown, and " +
                       "DR2Neutral.SelectNeutral remain available as SimHub actions. Do not map " +
                       "the same physical buttons to those actions while built-in input is enabled.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            });

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = page
            };
        }

        private void RefreshView()
        {
            if (!IsLoaded)
            {
                return;
            }

            _refreshing = true;
            try
            {
                DR2NeutralSettings settings = _plugin.GetSettingsSnapshot();
                _builtInInputCheckBox.IsChecked = settings.BuiltInControllerEnabled;
                SelectFunctionKey(_gearUpKeyComboBox, settings.GearUpVirtualKey);
                SelectFunctionKey(_gearDownKeyComboBox, settings.GearDownVirtualKey);

                bool calibrated = settings.IsCalibrated;
                bool calibrationActive = _plugin.CalibrationActive;
                _calibrateButton.Content = calibrated ? "Recalibrate" : "Start calibration";
                _calibrateButton.IsEnabled = !calibrationActive;
                _cancelCalibrationButton.IsEnabled = calibrationActive;
                _clearCalibrationButton.IsEnabled = calibrated && !calibrationActive;

                _controllerValue.Text = calibrated ? settings.ControllerName : "Not calibrated";
                _gearUpButtonValue.Text = FormatButton(settings.GearUpButton);
                _gearDownButtonValue.Text = FormatButton(settings.GearDownButton);
                _neutralButtonValue.Text = FormatButton(settings.SelectNeutralButton);
                _calibrationStatusValue.Text = _plugin.CalibrationStatus;

                _inputStatusValue.Text = _plugin.InputStatus;
                _gameValue.Text = EmptyAs(_plugin.GameName, "No game telemetry");
                _gearValue.Text = EmptyAs(_plugin.CurrentGear, "Unavailable");
                _readyValue.Text = _plugin.Ready ? "Yes" : "No";
                _busyValue.Text = _plugin.Busy ? "Running" : "Idle";
                _lastResultValue.Text = EmptyAs(_plugin.LastResult, "Idle");

                double age = _plugin.TelemetryAgeMs;
                _telemetryAgeValue.Text = age < 0
                    ? "No telemetry"
                    : Math.Round(age, 0).ToString(CultureInfo.InvariantCulture) + " ms";
            }
            finally
            {
                _refreshing = false;
            }
        }

        private async void CalibrateClick(object sender, RoutedEventArgs e)
        {
            _localMessageValue.Text = "Calibration started. Follow the prompt shown above.";
            RefreshView();

            try
            {
                await _plugin.StartCalibrationAsync();
            }
            catch (Exception ex)
            {
                _localMessageValue.Text = "Calibration error: " + ex.Message;
            }

            RefreshView();
        }

        private void ClearCalibrationClick(object sender, RoutedEventArgs e)
        {
            string error;
            _localMessageValue.Text = _plugin.TryClearCalibration(out error)
                ? "Controller assignments cleared."
                : error;
            RefreshView();
        }

        private void BuiltInInputChanged(object sender, RoutedEventArgs e)
        {
            if (_refreshing)
            {
                return;
            }

            string error;
            bool enabled = _builtInInputCheckBox.IsChecked == true;
            _localMessageValue.Text = _plugin.TrySetBuiltInControllerEnabled(enabled, out error)
                ? (enabled ? "Built-in controller input enabled." : "Built-in controller input disabled.")
                : error;
            RefreshView();
        }

        private void KeySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshing)
            {
                return;
            }

            FunctionKeyOption gearUp = _gearUpKeyComboBox.SelectedItem as FunctionKeyOption;
            FunctionKeyOption gearDown = _gearDownKeyComboBox.SelectedItem as FunctionKeyOption;
            if (gearUp == null || gearDown == null)
            {
                return;
            }

            string error;
            _localMessageValue.Text = _plugin.TrySetVirtualKeys(
                gearUp.VirtualKey,
                gearDown.VirtualKey,
                out error)
                ? "Keyboard bindings saved."
                : error;
            RefreshView();
        }

        private ComboBox CreateFunctionKeyComboBox()
        {
            ComboBox comboBox = new ComboBox
            {
                Width = 120,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 3)
            };

            foreach (FunctionKeyOption option in _functionKeys)
            {
                comboBox.Items.Add(option);
            }

            return comboBox;
        }

        private static Grid CreateTwoColumnGrid()
        {
            Grid grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        private static void AddRow(
            Grid grid,
            int rowIndex,
            string label,
            FrameworkElement value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 5, 16, 5),
                FontWeight = FontWeights.SemiBold
            };

            value.Margin = new Thickness(0, 5, 0, 5);
            Grid.SetRow(labelBlock, rowIndex);
            Grid.SetColumn(labelBlock, 0);
            Grid.SetRow(value, rowIndex);
            Grid.SetColumn(value, 1);
            grid.Children.Add(labelBlock);
            grid.Children.Add(value);
        }

        private static TextBlock CreateSectionTitle(string title)
        {
            return new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 10)
            };
        }

        private static TextBlock CreateValueText()
        {
            return new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static Button CreateButton(string text, bool primary)
        {
            return new Button
            {
                Content = text,
                MinWidth = primary ? 150 : 90,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        private static string FormatButton(int buttonIndex)
        {
            return DR2NeutralSettings.IsButtonIndexValid(buttonIndex)
                ? ControllerInputManager.FormatButton(buttonIndex)
                : "Not assigned";
        }

        private static string EmptyAs(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static void SelectFunctionKey(ComboBox comboBox, int virtualKey)
        {
            foreach (object item in comboBox.Items)
            {
                FunctionKeyOption option = item as FunctionKeyOption;
                if (option != null && option.VirtualKey == virtualKey)
                {
                    comboBox.SelectedItem = option;
                    return;
                }
            }
        }

        private sealed class FunctionKeyOption
        {
            public int VirtualKey { get; private set; }
            public string Name { get; private set; }

            public FunctionKeyOption(int virtualKey, string name)
            {
                VirtualKey = virtualKey;
                Name = name;
            }

            public override string ToString()
            {
                return Name;
            }
        }
    }
}
