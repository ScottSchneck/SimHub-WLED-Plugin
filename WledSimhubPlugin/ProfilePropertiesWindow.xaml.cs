using System.Windows;

namespace WledSimHubPlugin
{
    public partial class ProfilePropertiesWindow : Window
    {
        private WLEDPlugin _plugin;
        private WledProfile _profile;
        private bool _isNew;

        public ProfilePropertiesWindow(WLEDPlugin plugin, WledProfile profile, bool isNew)
        {
            InitializeComponent();
            _plugin = plugin;
            _profile = profile;
            _isNew = isNew;

            // Bind the temporary object directly to the UI elements
            this.DataContext = _profile;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_profile.ProfileName))
            {
                MessageBox.Show("Profile Name cannot be empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_profile.Game)) _profile.Game = "Any game";

            _plugin.SaveSettings(); // Force a disk write for safety
            this.DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
        }
    }
}