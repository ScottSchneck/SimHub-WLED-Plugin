using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;

namespace WledSimHubPlugin
{
    public enum ConnectionType { Network, Serial }
    public enum AutoSwitchMode { Disabled, LastSelected, Automatic }
    public enum AnimationType { Solid, Flash, Alternate, BarFill, RpmSweep, Chase }

    public class LedSegment : INotifyPropertyChanged
    {
        private int    _startPixel;
        private int    _stopPixel      = 10;
        private string _idleHexColor   = "#000000";
        private string _hexColor       = "#FF0000";
        private string _hexColor2      = "#0000FF";
        private string _hexColor3      = "#00FF00";

        public int    StartPixel    { get => _startPixel;  set { _startPixel  = value; OnPropertyChanged(); } }
        public int    StopPixel     { get => _stopPixel;   set { _stopPixel   = value; OnPropertyChanged(); } }
        public string IdleHexColor  { get => _idleHexColor; set { _idleHexColor = value; OnPropertyChanged(); } }
        public string HexColor      { get => _hexColor;    set { _hexColor    = value; OnPropertyChanged(); } }
        public string HexColor2     { get => _hexColor2;   set { _hexColor2   = value; OnPropertyChanged(); } }
        public string HexColor3     { get => _hexColor3;   set { _hexColor3   = value; OnPropertyChanged(); } }

        public LedSegment Clone() => new LedSegment
        {
            StartPixel = StartPixel, StopPixel = StopPixel,
            IdleHexColor = IdleHexColor, HexColor = HexColor,
            HexColor2 = HexColor2, HexColor3 = HexColor3
        };

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public class WledDevice : INotifyPropertyChanged
    {
        public string DeviceId { get; set; } = Guid.NewGuid().ToString();

        private string _deviceName = "New Device";
        public string DeviceName { get => _deviceName; set { _deviceName = value; OnPropertyChanged(); } }

        private ConnectionType _connectionMethod = ConnectionType.Network;
        public ConnectionType ConnectionMethod
        {
            get => _connectionMethod;
            set { _connectionMethod = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectionMethodInt)); OnPropertyChanged(nameof(IsNetwork)); OnPropertyChanged(nameof(IsSerial)); }
        }

        public int ConnectionMethodInt { get => (int)ConnectionMethod; set => ConnectionMethod = (ConnectionType)value; }
        public bool IsNetwork => ConnectionMethod == ConnectionType.Network;
        public bool IsSerial => ConnectionMethod == ConnectionType.Serial;

        private string _wledIp = "192.168.1.100";
        public string WledIp { get => _wledIp; set { _wledIp = value; OnPropertyChanged(); } }

        private int _udpPort = 21324;
        public int UdpPort { get => _udpPort; set { _udpPort = value; OnPropertyChanged(); } }

        private string _comPort = "";
        public string ComPort { get => _comPort; set { _comPort = value; OnPropertyChanged(); } }

        private int _baudRate = 115200;
        public int BaudRate { get => _baudRate; set { _baudRate = value; OnPropertyChanged(); } }

        private int _totalLedCount = 150;
        public int TotalLedCount { get => _totalLedCount; set { _totalLedCount = value; OnPropertyChanged(); } }

        public bool ShouldSerializeConnectionMethodInt() => false;
        public bool ShouldSerializeIsNetwork() => false;
        public bool ShouldSerializeIsSerial() => false;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class DeviceRuleSet : INotifyPropertyChanged
    {
        public string DeviceId { get; set; }
        public ObservableCollection<PixelRule> Rules { get; set; } = new ObservableCollection<PixelRule>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class WledProfile : INotifyPropertyChanged
    {
        private string _profileId = Guid.NewGuid().ToString();
        private string _profileName = "Default Profile";
        private string _game = "Any game";
        private string _car = "";

        public string ProfileId { get => _profileId; set { _profileId = value; OnPropertyChanged(); } }
        public string ProfileName { get => _profileName; set { _profileName = value; OnPropertyChanged(); } }
        public string Game { get => _game; set { _game = value; OnPropertyChanged(); } }
        public string Car { get => _car; set { _car = value; OnPropertyChanged(); } }

        public ObservableCollection<DeviceRuleSet> DeviceRuleSets { get; set; } = new ObservableCollection<DeviceRuleSet>();

        public int TotalRuleCount => DeviceRuleSets?.Sum(d => d.Rules?.Count ?? 0) ?? 0;
        public bool ShouldSerializeTotalRuleCount() => false;

        public DeviceRuleSet EnsureDeviceRuleSet(string deviceId)
        {
            var drs = DeviceRuleSets.FirstOrDefault(d => d.DeviceId == deviceId);
            if (drs == null) { drs = new DeviceRuleSet { DeviceId = deviceId }; DeviceRuleSets.Add(drs); }
            return drs;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public WledProfile Clone(string newName)
        {
            var clone = new WledProfile { ProfileName = newName, Game = this.Game, Car = this.Car };
            foreach (var drs in this.DeviceRuleSets)
            {
                var newSet = new DeviceRuleSet { DeviceId = drs.DeviceId };
                foreach (var r in drs.Rules)
                    newSet.Rules.Add(r.Clone());
                clone.DeviceRuleSets.Add(newSet);
            }
            return clone;
        }
    }

    public class PixelRule : INotifyPropertyChanged
    {
        private string _ruleName = "New Rule";
        private bool _isExpanded = true;
        private bool _isActive = true;
        private string _formula = "[DataCorePlugin.GameData.NewData.Flag_Yellow] == 1";
        private bool _usePreset = false;
        private int _presetId = 1;
        private int _startPixel = 0;
        private int _stopPixel = 10;
        private string _idleHexColor = "#000000";
        private string _hexColor = "#FF0000";
        private string _hexColor2 = "#0000FF";
        private bool _isFlashing = false;
        private int _flashSpeedMs = 500;
        private bool _isAlternating = false;
        private int _alternateSpeedMs = 250;
        private AnimationType _animationType = AnimationType.Solid;
        private string _valueFormula = "";
        private double _valueMin = 0;
        private double _valueMax = 100;
        private int _chaseWindowSize = 3;
        private string _hexColor3 = "#FF0000";
        private int _rpmZone2Start = 60;
        private int _rpmZone3Start = 85;

        public string RuleName { get => _ruleName; set { _ruleName = value; OnPropertyChanged(); } }
        public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(); } }
        public bool IsActive { get => _isActive; set { _isActive = value; OnPropertyChanged(); } }
        public string Formula
        {
            get => _formula;
            set
            {
                _formula = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormulaSyntaxStatus));
                OnPropertyChanged(nameof(FormulaSyntaxStatusColor));
            }
        }
        public bool UsePreset { get => _usePreset; set { _usePreset = value; OnPropertyChanged(); } }
        public int PresetId { get => _presetId; set { _presetId = value; OnPropertyChanged(); } }
        public int StartPixel { get => _startPixel; set { _startPixel = value; OnPropertyChanged(); } }
        public int StopPixel  { get => _stopPixel;  set { _stopPixel  = value; OnPropertyChanged(); } }

        private ObservableCollection<LedSegment> _segments = new ObservableCollection<LedSegment>();
        public ObservableCollection<LedSegment> Segments
        {
            get => _segments;
            set { _segments = value ?? new ObservableCollection<LedSegment>(); OnPropertyChanged(); }
        }

        public string IdleHexColor { get => _idleHexColor; set { _idleHexColor = value; OnPropertyChanged(); } }
        public string HexColor { get => _hexColor; set { _hexColor = value; OnPropertyChanged(); } }
        public string HexColor2 { get => _hexColor2; set { _hexColor2 = value; OnPropertyChanged(); } }
        public bool IsFlashing { get => _isFlashing; set { _isFlashing = value; OnPropertyChanged(); } }
        public int FlashSpeedMs { get => _flashSpeedMs; set { _flashSpeedMs = Math.Max(1, value); OnPropertyChanged(); } }
        public bool IsAlternating { get => _isAlternating; set { _isAlternating = value; OnPropertyChanged(); } }
        public int AlternateSpeedMs { get => _alternateSpeedMs; set { _alternateSpeedMs = Math.Max(1, value); OnPropertyChanged(); } }

        public AnimationType AnimationType
        {
            get => _animationType;
            set
            {
                _animationType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AnimationTypeInt));
                OnPropertyChanged(nameof(IsFlashAnimation));
                OnPropertyChanged(nameof(IsAlternateAnimation));
                OnPropertyChanged(nameof(IsValueAnimation));
                OnPropertyChanged(nameof(IsChaseAnimation));
                OnPropertyChanged(nameof(IsRpmSweepAnimation));
                OnPropertyChanged(nameof(NeedsSecondaryColor));
                OnPropertyChanged(nameof(NeedsThirdColor));
            }
        }
        public int AnimationTypeInt { get => (int)AnimationType; set => AnimationType = (AnimationType)value; }
        public string ValueFormula { get => _valueFormula; set { _valueFormula = value; OnPropertyChanged(); } }
        public double ValueMin { get => _valueMin; set { _valueMin = value; OnPropertyChanged(); } }
        public double ValueMax { get => _valueMax; set { _valueMax = value; OnPropertyChanged(); } }
        public int ChaseWindowSize { get => _chaseWindowSize; set { _chaseWindowSize = Math.Max(1, value); OnPropertyChanged(); } }
        public string HexColor3 { get => _hexColor3; set { _hexColor3 = value; OnPropertyChanged(); } }
        public int RpmZone2Start { get => _rpmZone2Start; set { _rpmZone2Start = value; OnPropertyChanged(); } }
        public int RpmZone3Start { get => _rpmZone3Start; set { _rpmZone3Start = value; OnPropertyChanged(); } }

        public bool IsFlashAnimation => AnimationType == AnimationType.Flash;
        public bool IsAlternateAnimation => AnimationType == AnimationType.Alternate;
        public bool IsValueAnimation => AnimationType == AnimationType.BarFill || AnimationType == AnimationType.RpmSweep || AnimationType == AnimationType.Chase;
        public bool IsChaseAnimation => AnimationType == AnimationType.Chase;
        public bool IsRpmSweepAnimation => AnimationType == AnimationType.RpmSweep;
        public bool NeedsSecondaryColor => AnimationType == AnimationType.Alternate || AnimationType == AnimationType.RpmSweep;
        public bool NeedsThirdColor => AnimationType == AnimationType.RpmSweep;

        private bool _isCurrentlyFiring;
        public bool IsCurrentlyFiring { get => _isCurrentlyFiring; set { _isCurrentlyFiring = value; OnPropertyChanged(); } }

        public string FormulaSyntaxStatus
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_formula)) return "⚠️ No formula set";
                if (!_formula.Contains("[") || !_formula.Contains("]")) return "❌ Properties must use [Brackets]";
                return "✅ Syntax looks good";
            }
        }

        public string FormulaSyntaxStatusColor => FormulaSyntaxStatus.StartsWith("✅") ? "#1DD383" : FormulaSyntaxStatus.StartsWith("❌") ? "#CF6679" : "#FFB74D";

        public bool ShouldSerializeIsCurrentlyFiring() => false;
        public bool ShouldSerializeFormulaSyntaxStatus() => false;
        public bool ShouldSerializeFormulaSyntaxStatusColor() => false;

        // Colors live on LedSegment now; keep these fields readable from old JSON for migration but don't write them
        public bool ShouldSerializeIdleHexColor() => false;
        public bool ShouldSerializeHexColor()     => false;
        public bool ShouldSerializeHexColor2()    => false;
        public bool ShouldSerializeHexColor3()    => false;

        public bool ShouldSerializeIsFlashing() => false;
        public bool ShouldSerializeIsAlternating() => false;
        public bool ShouldSerializeAnimationTypeInt() => false;
        public bool ShouldSerializeIsFlashAnimation() => false;
        public bool ShouldSerializeIsAlternateAnimation() => false;
        public bool ShouldSerializeIsValueAnimation() => false;
        public bool ShouldSerializeIsChaseAnimation() => false;
        public bool ShouldSerializeIsRpmSweepAnimation() => false;
        public bool ShouldSerializeNeedsSecondaryColor() => false;
        public bool ShouldSerializeNeedsThirdColor() => false;

        public PixelRule Clone()
        {
            var c = new PixelRule
            {
                RuleName = RuleName, Formula = Formula, IsActive = IsActive, IsExpanded = IsExpanded,
                UsePreset = UsePreset, PresetId = PresetId,
                StartPixel = StartPixel, StopPixel = StopPixel,
                IdleHexColor = IdleHexColor, HexColor = HexColor, HexColor2 = HexColor2,
                IsFlashing = IsFlashing, FlashSpeedMs = FlashSpeedMs,
                IsAlternating = IsAlternating, AlternateSpeedMs = AlternateSpeedMs,
                AnimationType = AnimationType, ValueFormula = ValueFormula,
                ValueMin = ValueMin, ValueMax = ValueMax,
                ChaseWindowSize = ChaseWindowSize, HexColor3 = HexColor3,
                RpmZone2Start = RpmZone2Start, RpmZone3Start = RpmZone3Start
            };
            foreach (var seg in Segments) c.Segments.Add(seg.Clone());
            return c;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class WledSettings : INotifyPropertyChanged
    {
        public ObservableCollection<WledDevice> Devices { get; set; } = new ObservableCollection<WledDevice>();
        public AutoSwitchMode SwitchMode { get; set; } = AutoSwitchMode.Automatic;
        public ObservableCollection<WledProfile> Profiles { get; set; } = new ObservableCollection<WledProfile>();

        private string _selectedProfileId;
        public string SelectedProfileId
        {
            get => _selectedProfileId;
            set { _selectedProfileId = value; _activeProfileCache = null; OnPropertyChanged(); OnPropertyChanged(nameof(ActiveProfile)); NotifyRulesChanged(); }
        }

        private WledDevice _selectedDevice;
        public WledDevice SelectedDevice
        {
            get => _selectedDevice;
            set { _selectedDevice = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelectedDevice)); NotifyRulesChanged(); }
        }

        public bool HasSelectedDevice => SelectedDevice != null;

        private string _cachedProfileId;
        private WledProfile _activeProfileCache;
        public WledProfile ActiveProfile
        {
            get
            {
                if (_activeProfileCache == null || _cachedProfileId != _selectedProfileId)
                {
                    _activeProfileCache = Profiles.FirstOrDefault(p => p.ProfileId == _selectedProfileId);
                    _cachedProfileId = _selectedProfileId;
                }
                return _activeProfileCache;
            }
        }
        public ObservableCollection<string> AvailableComPorts { get; set; } = new ObservableCollection<string>();

        public ObservableCollection<PixelRule> CurrentDeviceRules
        {
            get
            {
                if (ActiveProfile == null || SelectedDevice == null) return null;
                return ActiveProfile.DeviceRuleSets.FirstOrDefault(d => d.DeviceId == SelectedDevice.DeviceId)?.Rules;
            }
        }

        public void NotifyRulesChanged() => OnPropertyChanged(nameof(CurrentDeviceRules));

        public bool ShouldSerializeActiveProfile() => false;
        public bool ShouldSerializeAvailableComPorts() => false;
        public bool ShouldSerializeSelectedDevice() => false;
        public bool ShouldSerializeHasSelectedDevice() => false;
        public bool ShouldSerializeCurrentDeviceRules() => false;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}