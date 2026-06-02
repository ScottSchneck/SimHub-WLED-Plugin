using SimHub.Plugins.ProfilesCommon;
using System.Windows;
using System.Windows.Controls;

namespace WledSimHubPlugin
{
    public partial class ProfileManagerWindow : Window
    {
        private WLEDPlugin _plugin;

        public ProfileManagerWindow(WLEDPlugin plugin)
        {
            InitializeComponent();
            _plugin = plugin;
            this.DataContext = _plugin.Settings;
            ProfileList.SelectedItem = _plugin.Settings.ActiveProfile;
        }

        private void SetActive_Click(object sender, RoutedEventArgs e)
        {
            if (ProfileList.SelectedItem is WledProfile profile)
            {
                _plugin.Settings.SelectedProfileId = profile.ProfileId;
                _plugin.SaveSettings();
                this.DialogResult = true;
            }
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin.Settings.Profiles.Count <= 1)
            {
                MessageBox.Show("You must have at least one profile.", "Cannot Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (sender is Button btn && btn.DataContext is WledProfile profile)
            {
                var result = MessageBox.Show($"Delete '{profile.ProfileName}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _plugin.Settings.Profiles.Remove(profile);
                    if (_plugin.Settings.SelectedProfileId == profile.ProfileId)
                    {
                        _plugin.Settings.SelectedProfileId = _plugin.Settings.Profiles[0].ProfileId;
                    }
                    _plugin.SaveSettings();
                }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => this.DialogResult = false;
    }
}