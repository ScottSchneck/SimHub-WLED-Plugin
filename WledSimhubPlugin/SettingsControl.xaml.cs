using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WledSimHubPlugin
{
    public partial class SettingsControl : UserControl
    {
        private static readonly Brush _warningBrush = MakeFrozenBrush("#FFB74D");
        private static readonly Brush _errorBrush   = MakeFrozenBrush("#CF6679");
        private static readonly Brush _successBrush = MakeFrozenBrush("#1DD383");
        private static Brush MakeFrozenBrush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

        public WLEDPlugin Plugin { get; }
        private PixelRule _draggedItem;
        private Point _startPoint;
        public ObservableCollection<string> FilteredProperties { get; set; }
        private List<string> allProperties;

        private DispatcherTimer _searchDebounce;
        private DispatcherTimer _autocompleteDebounce;
        private DispatcherTimer _snackbarTimer;
        private Action _pendingUndoAction;
        private Action _pendingOnTimeout;
        private string _pendingSearchQuery;
        private TextBox _pendingAutocompleteBox;

        public SettingsControl(WLEDPlugin plugin)
        {
            InitializeComponent();
            Plugin = plugin;
            this.DataContext = plugin.Settings;
            FilteredProperties = new ObservableCollection<string>();
            PropertyListBox.ItemsSource = FilteredProperties;
            allProperties = new List<string>(plugin.PluginManager.GetAllPropertiesNames());
            UpdateSearch("");

            RefreshComPorts();

            _searchDebounce = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            _searchDebounce.Tick += (s, e) => { _searchDebounce.Stop(); UpdateSearch(_pendingSearchQuery ?? ""); };
            _autocompleteDebounce = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            _autocompleteDebounce.Tick += (s, e) => { _autocompleteDebounce.Stop(); if (_pendingAutocompleteBox != null) HandleAutocomplete(_pendingAutocompleteBox); };

            SearchBox.GotFocus += (s, e) => { if (SearchBox.Text == "Search properties...") SearchBox.Text = ""; };
            SearchBox.LostFocus += (s, e) => { if (string.IsNullOrWhiteSpace(SearchBox.Text)) SearchBox.Text = "Search properties..."; };
            NewRuleNameBox.GotFocus += (s, e) => { if (NewRuleNameBox.Text == "My Custom Rule") NewRuleNameBox.Text = ""; };
        }

        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            var dev = new WledDevice();
            Plugin.Settings.Devices.Add(dev);
            Plugin.Settings.SelectedDevice = dev;
            Plugin.SaveSettings();
            Plugin.SyncEngines();
        }

        private void DeleteDevice_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin.Settings.SelectedDevice == null) return;
            string name = Plugin.Settings.SelectedDevice.DeviceName;
            var result = MessageBox.Show(
                $"Delete \"{name}\" and all its rules?\nThis cannot be undone.",
                "Delete Device", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            Plugin.Settings.Devices.Remove(Plugin.Settings.SelectedDevice);
            Plugin.Settings.SelectedDevice = Plugin.Settings.Devices.FirstOrDefault();
            Plugin.SaveSettings();
            Plugin.SyncEngines();
            ShowSnackbar($"Device \"{name}\" deleted");
        }

        private void RefreshComPorts()
        {
            string current = Plugin.Settings.SelectedDevice?.ComPort;
            Plugin.Settings.AvailableComPorts.Clear();
            List<string> portList = new List<string>();

            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Caption like '%(COM%'"))
                {
                    foreach (var queryObj in searcher.Get())
                    {
                        string caption = queryObj["Caption"]?.ToString();
                        if (!string.IsNullOrEmpty(caption))
                        {
                            int startIdx = caption.LastIndexOf("(COM");
                            int endIdx = caption.LastIndexOf(")");
                            if (startIdx != -1 && endIdx > startIdx)
                            {
                                string port = caption.Substring(startIdx + 1, endIdx - startIdx - 1);
                                string name = caption.Substring(0, startIdx).Trim();
                                portList.Add($"{port} - {name}");
                            }
                        }
                    }
                }
            }
            catch { }

            var basicPorts = System.IO.Ports.SerialPort.GetPortNames();
            foreach (var p in basicPorts) if (!portList.Any(x => x.StartsWith(p + " -") || x == p)) portList.Add(p);
            foreach (var port in portList.OrderBy(x => x)) Plugin.Settings.AvailableComPorts.Add(port);

            if (!string.IsNullOrEmpty(current) && !Plugin.Settings.AvailableComPorts.Contains(current)) Plugin.Settings.AvailableComPorts.Insert(0, current);
        }

        private void ComPort_DropDownOpened(object sender, EventArgs e) => RefreshComPorts();

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchBox.Text == "Search properties...") return;
            _pendingSearchQuery = SearchBox.Text;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
        private void UpdateSearch(string q) { FilteredProperties.Clear(); foreach (var m in (string.IsNullOrWhiteSpace(q) ? allProperties.Take(50) : allProperties.Where(p => p.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(50))) FilteredProperties.Add(m); }

        private void InsertProperty_Click(object sender, RoutedEventArgs e)
        {
            if (PropertyListBox.SelectedItem is string p)
            {
                string insertText = $"[{p}]";
                int caretIndex = ConditionBox.CaretIndex;
                ConditionBox.Text = ConditionBox.Text.Insert(caretIndex, insertText);
                ConditionBox.CaretIndex = caretIndex + insertText.Length;
                ConditionBox.Focus();
            }
        }

        private void PropertyListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ValidateSyntax();
        private void ConditionBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ValidateSyntax();
            _pendingAutocompleteBox = ConditionBox;
            _autocompleteDebounce.Stop();
            _autocompleteDebounce.Start();
        }

        private void ValidateSyntax()
        {
            if (SyntaxStatusText == null || AddRuleBtn == null) return;
            string cond = ConditionBox.Text?.Trim();
            if (string.IsNullOrEmpty(cond)) { SyntaxStatusText.Text = "⚠️ Use the ➔ button to insert properties."; SyntaxStatusText.Foreground = _warningBrush; AddRuleBtn.IsEnabled = false; return; }
            if (!cond.Contains("[") || !cond.Contains("]")) { SyntaxStatusText.Text = "❌ Invalid: Properties must be wrapped in [Brackets]."; SyntaxStatusText.Foreground = _errorBrush; AddRuleBtn.IsEnabled = false; return; }
            SyntaxStatusText.Text = "✅ Syntax looks good"; SyntaxStatusText.Foreground = _successBrush; AddRuleBtn.IsEnabled = true;
        }

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            var activeProf = Plugin.Settings.ActiveProfile;
            var selectedDevice = Plugin.Settings.SelectedDevice;
            if (activeProf == null || selectedDevice == null) return;
            string c = ConditionBox.Text?.Trim();
            if (!string.IsNullOrEmpty(c))
            {
                string rName = NewRuleNameBox.Text?.Trim();
                if (string.IsNullOrEmpty(rName) || rName == "My Custom Rule") rName = "New Formula Rule";
                activeProf.EnsureDeviceRuleSet(selectedDevice.DeviceId).Rules.Add(new PixelRule { RuleName = rName, Formula = c, IsExpanded = true });
                Plugin.Settings.NotifyRulesChanged();
                ConditionBox.Text = ""; SearchBox.Text = "Search properties..."; NewRuleNameBox.Text = "My Custom Rule";
                Plugin.SaveSettings();
                ShowSnackbar($"Rule \"{rName}\" added");
            }
        }

        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin.Settings.CurrentDeviceRules == null || !(sender is Button b) || !(b.DataContext is PixelRule r)) return;
            var activeProf = Plugin.Settings.ActiveProfile;
            var selectedDevice = Plugin.Settings.SelectedDevice;
            var rules = Plugin.Settings.CurrentDeviceRules;
            int idx = rules.IndexOf(r);
            string name = r.RuleName;
            rules.Remove(r);

            ShowSnackbar(
                $"Rule \"{name}\" removed",
                undoAction: () =>
                {
                    var target = activeProf?.DeviceRuleSets.FirstOrDefault(d => d.DeviceId == selectedDevice?.DeviceId)?.Rules;
                    target?.Insert(Math.Min(idx, target.Count), r);
                },
                onTimeout: () => Plugin.SaveSettings());
        }

        private void DuplicateRule_Click(object sender, RoutedEventArgs e)
        {
            var activeProf = Plugin.Settings.ActiveProfile;
            var selectedDevice = Plugin.Settings.SelectedDevice;
            if (activeProf == null || selectedDevice == null || !(sender is Button b) || !(b.DataContext is PixelRule r)) return;
            var copy = r.Clone();
            copy.RuleName += " (Copy)";
            copy.IsExpanded = false;
            var rules = activeProf.EnsureDeviceRuleSet(selectedDevice.DeviceId).Rules;
            int idx = rules.IndexOf(r);
            rules.Insert(idx >= 0 ? idx + 1 : rules.Count, copy);
            Plugin.Settings.NotifyRulesChanged();
            Plugin.SaveSettings();
            ShowSnackbar($"Rule \"{copy.RuleName}\" duplicated");
        }

        private void CloneRuleToDevice_Click(object sender, RoutedEventArgs e)
        {
            var activeProf = Plugin.Settings.ActiveProfile;
            var currentDevice = Plugin.Settings.SelectedDevice;
            if (activeProf == null || currentDevice == null || !(sender is Button b) || !(b.DataContext is PixelRule r)) return;

            var otherDevices = Plugin.Settings.Devices.Where(d => d.DeviceId != currentDevice.DeviceId).ToList();
            if (otherDevices.Count == 0)
            {
                MessageBox.Show("No other devices available to clone to.", "Clone Rule", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new CloneRuleToDeviceWindow(r, otherDevices) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true && window.SelectedDevice != null)
            {
                var copy = r.Clone();
                copy.RuleName += " (Copy)";
                copy.IsExpanded = false;
                activeProf.EnsureDeviceRuleSet(window.SelectedDevice.DeviceId).Rules.Add(copy);
                Plugin.SaveSettings();
                ShowSnackbar($"Rule cloned to \"{window.SelectedDevice.DeviceName}\"");
            }
        }

        private void ShowSnackbar(string message, Action undoAction = null, Action onTimeout = null)
        {
            SnackbarText.Text = message;
            SnackbarUndoBtn.Visibility = undoAction != null ? Visibility.Visible : Visibility.Collapsed;
            _pendingUndoAction = undoAction;
            _pendingOnTimeout = onTimeout;

            SnackbarTranslate.Y = 20;
            SnackbarBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            SnackbarTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            _snackbarTimer?.Stop();
            _snackbarTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(3) };
            _snackbarTimer.Tick += (s, e) => DismissSnackbar(commit: true);
            _snackbarTimer.Start();
        }

        private void DismissSnackbar(bool commit)
        {
            _snackbarTimer?.Stop();
            if (commit) _pendingOnTimeout?.Invoke();
            _pendingUndoAction = null;
            _pendingOnTimeout = null;
            SnackbarBorder.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250)));
        }

        private void SnackbarUndo_Click(object sender, RoutedEventArgs e)
        {
            var undo = _pendingUndoAction;
            DismissSnackbar(commit: false);
            undo?.Invoke();
        }

        private void AddSegment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is PixelRule rule)
                rule.Segments.Add(new LedSegment { StartPixel = 0, StopPixel = 10 });
        }

        private void RemoveSegment_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.DataContext is LedSegment seg && b.Tag is PixelRule rule && rule.Segments.Count > 1)
                rule.Segments.Remove(seg);
        }

        private void Save_Click(object sender, RoutedEventArgs e) => Plugin.SaveSettings();

        private void ProfilesManager_Click(object sender, RoutedEventArgs e) { try { var window = new ProfileManagerWindow(Plugin) { Owner = Window.GetWindow(this) }; window.ShowDialog(); } catch { } }

        private void EditProfile_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin.Settings.ActiveProfile != null) { var window = new ProfilePropertiesWindow(Plugin, Plugin.Settings.ActiveProfile, false) { Owner = Window.GetWindow(this) }; window.ShowDialog(); }
        }

        private void CloneProfile_Click(object sender, RoutedEventArgs e)
        {
            if (Plugin.Settings.ActiveProfile != null)
            {
                var cloned = Plugin.Settings.ActiveProfile.Clone($"{Plugin.Settings.ActiveProfile.ProfileName} - Copy");
                var window = new ProfilePropertiesWindow(Plugin, cloned, true) { Owner = Window.GetWindow(this) };
                if (window.ShowDialog() == true) { Plugin.Settings.Profiles.Add(cloned); Plugin.Settings.SelectedProfileId = cloned.ProfileId; }
            }
        }

        private void NewProfile_Click(object sender, RoutedEventArgs e)
        {
            var newProfile = new WledProfile { ProfileName = "New Profile", Game = "Any game" };
            var window = new ProfilePropertiesWindow(Plugin, newProfile, true) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true) { Plugin.Settings.Profiles.Add(newProfile); Plugin.Settings.SelectedProfileId = newProfile.ProfileId; }
        }

        private TextBox _activeFormulaBox;

        private static int FindOpenBracket(string text, int caret)
        {
            for (int i = caret - 1; i >= 0; i--)
            {
                if (text[i] == ']') return -1;
                if (text[i] == '[') return i;
            }
            return -1;
        }

        private void HandleAutocomplete(TextBox box)
        {
            _activeFormulaBox = box;
            string text = box.Text ?? "";
            int caret = box.CaretIndex;

            int bracketStart = FindOpenBracket(text, caret);
            if (bracketStart == -1) { PropertyPopup.IsOpen = false; return; }

            string partial = text.Substring(bracketStart + 1, caret - bracketStart - 1);
            var matches = allProperties
                .Where(p => p.IndexOf(partial, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(50)
                .ToList();

            if (matches.Count == 0) { PropertyPopup.IsOpen = false; return; }

            PropertySuggestionList.ItemsSource = matches;
            PropertySuggestionList.SelectedIndex = 0;
            PropertyPopup.PlacementTarget = box;
            PropertyPopup.Width = Math.Max(300, box.ActualWidth);
            PropertyPopup.IsOpen = true;
        }

        private void CommitSuggestion(string property)
        {
            if (_activeFormulaBox == null) return;
            string text = _activeFormulaBox.Text ?? "";
            int caret = _activeFormulaBox.CaretIndex;

            int bracketStart = FindOpenBracket(text, caret);
            if (bracketStart == -1) return;

            string replacement = $"[{property}]";
            _activeFormulaBox.Text = text.Substring(0, bracketStart) + replacement + text.Substring(caret);
            _activeFormulaBox.CaretIndex = bracketStart + replacement.Length;
            _activeFormulaBox.Focus();
            PropertyPopup.IsOpen = false;
        }

        private void FormulaBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _pendingAutocompleteBox = (TextBox)sender;
            _autocompleteDebounce.Stop();
            _autocompleteDebounce.Start();
        }

        private void FormulaBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!PropertyPopup.IsOpen) return;
            switch (e.Key)
            {
                case Key.Down:
                    PropertySuggestionList.SelectedIndex = Math.Min(PropertySuggestionList.SelectedIndex + 1, PropertySuggestionList.Items.Count - 1);
                    PropertySuggestionList.ScrollIntoView(PropertySuggestionList.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Up:
                    PropertySuggestionList.SelectedIndex = Math.Max(PropertySuggestionList.SelectedIndex - 1, 0);
                    PropertySuggestionList.ScrollIntoView(PropertySuggestionList.SelectedItem);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (PropertySuggestionList.SelectedItem is string p) CommitSuggestion(p);
                    e.Handled = true;
                    break;
                case Key.Tab:
                    if (PropertySuggestionList.SelectedItem is string pt) CommitSuggestion(pt);
                    break;
                case Key.Escape:
                    PropertyPopup.IsOpen = false;
                    e.Handled = true;
                    break;
            }
        }

        private void FormulaBox_LostFocus(object sender, RoutedEventArgs e) => PropertyPopup.IsOpen = false;

        private void PropertySuggestionList_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var item = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
            if (item?.Content is string prop) CommitSuggestion(prop);
            e.Handled = true;
        }

        private void OpenColorPicker(object sender, Func<PixelRule, string> getColor, Action<PixelRule, string> setColor)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is PixelRule rule)) return;
            using (var dialog = new System.Windows.Forms.ColorDialog())
            {
                dialog.FullOpen = true;
                try { var c = (Color)ColorConverter.ConvertFromString(getColor(rule)); dialog.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B); } catch { }
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    setColor(rule, $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
                    Plugin.SaveSettings();
                }
            }
        }

        private void SegmentColorPicker_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button b) || !(b.DataContext is LedSegment seg)) return;
            string which = b.Tag?.ToString() ?? "Primary";

            Func<LedSegment, string>         getColor;
            Action<LedSegment, string>       setColor;
            switch (which)
            {
                case "Idle":   getColor = s => s.IdleHexColor; setColor = (s, v) => s.IdleHexColor = v; break;
                case "Color2": getColor = s => s.HexColor2;    setColor = (s, v) => s.HexColor2    = v; break;
                case "Color3": getColor = s => s.HexColor3;    setColor = (s, v) => s.HexColor3    = v; break;
                default:       getColor = s => s.HexColor;     setColor = (s, v) => s.HexColor     = v; break;
            }

            using (var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true })
            {
                try
                {
                    var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(getColor(seg));
                    dialog.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
                }
                catch { }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    setColor(seg, $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
                    Plugin.SaveSettings();
                }
            }
        }

        private void RulesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _startPoint = e.GetPosition(null);
        private void RulesList_MouseMove(object sender, MouseEventArgs e) { Point mousePos = e.GetPosition(null); Vector diff = _startPoint - mousePos; if (e.LeftButton == MouseButtonState.Pressed && (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)) { if (sender is ListBoxItem item && item.Content is PixelRule r) { _draggedItem = r; DragDrop.DoDragDrop(item, new DataObject(typeof(PixelRule), r), DragDropEffects.Move); } } }
        private void RulesList_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(typeof(PixelRule)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; }

        private void RulesList_Drop(object sender, DragEventArgs e)
        {
            var targetRow = (e.OriginalSource as DependencyObject)?.FindAncestor<ListBoxItem>();
            if (Plugin.Settings.CurrentDeviceRules != null && targetRow != null && targetRow.Content is PixelRule targetRule && _draggedItem != null && _draggedItem != targetRule)
            {
                Plugin.Settings.CurrentDeviceRules.Move(Plugin.Settings.CurrentDeviceRules.IndexOf(_draggedItem), Plugin.Settings.CurrentDeviceRules.IndexOf(targetRule));
                Plugin.SaveSettings();
            }
            _draggedItem = null;
        }

        private void TestButton_Down(object sender, MouseButtonEventArgs e) => Plugin.StartTest(Plugin.Settings.SelectedDevice, 0, 255, 255);
        private void TestButton_Up(object sender, MouseButtonEventArgs e) => Plugin.StopTest();
    }
}