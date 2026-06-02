using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace WledSimHubPlugin
{
    public partial class CloneRuleToDeviceWindow : Window
    {
        public WledDevice SelectedDevice => DeviceList.SelectedItem as WledDevice;

        public CloneRuleToDeviceWindow(PixelRule rule, IEnumerable<WledDevice> targetDevices)
        {
            InitializeComponent();
            RuleNameText.Text = rule.RuleName;
            DeviceList.ItemsSource = targetDevices;
            DeviceList.SelectionChanged += (s, e) => CloneBtn.IsEnabled = DeviceList.SelectedItem != null;
        }

        private void Clone_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedDevice != null)
                DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
