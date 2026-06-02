using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SimHub.Plugins;
using GameReaderCommon;
using System.Text.RegularExpressions;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.IO.Ports;
using System.Collections.Generic;
using System.Globalization;

namespace WledSimHubPlugin
{
    [PluginDescription("WLED Multi-Device Plugin"), PluginAuthor("Scott Schneckenburger"), PluginName("WLED SimHub Plugin")]
    public class WLEDPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public PluginManager PluginManager { get; set; }
        public WledSettings Settings { get; private set; }

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private static readonly UdpClient _udpClient = new UdpClient();
        private static readonly Regex _propRegex = new Regex(@"\[(.*?)\]", RegexOptions.Compiled);
        private static readonly DataTable _dt = new DataTable();

        private Thread _renderThread;
        private bool _isRunning = false;
        private long _lastLogicMs = 0;
        private long _isGameActive = 0;
        private const int SIMHUB_THROTTLE_MS = 100;
        private string _lastDetectedGame = "FORCE_INIT";

        // UI Test Control
        private string _testDeviceId = "";
        private readonly byte[] _testColor = new byte[3];
        private static readonly byte[] _black = new byte[3];
        private readonly Dictionary<string, PixelRule> _firingSnapshot = new Dictionary<string, PixelRule>();
        private readonly Dictionary<string, PixelRule> _prevFiringSnapshot = new Dictionary<string, PixelRule>();

        private class DeviceEngine
        {
            public WledDevice DeviceConfig;
            public PixelRule TargetRule;

            public bool IsAwake = true;
            public int LastPresetId = -1;
            public long PauseBinaryUntilMs = 0;
            public long LastFrameMs = 0;

            public SerialPort Serial;
            public byte[] PayloadBuffer;
            public long NextSerialAttemptMs;
            public long SerialReadyMs = 0;

            public ConnectionType LastMode;
            public string CachedIp;
            public int CachedPort = -1;
            public IPEndPoint Endpoint;

            public readonly byte[] TempC1   = new byte[3];
            public readonly byte[] TempC2   = new byte[3];
            public readonly byte[] TempC3   = new byte[3];
            public readonly byte[] TempIdle = new byte[3];
            public double TargetValue;
            public string CachedComPortSource;
            public string CachedRawPort = "";

            public DeviceEngine(WledDevice config) { DeviceConfig = config; }

            public void MaintainNetwork()
            {
                if (DeviceConfig.ConnectionMethod == ConnectionType.Network)
                {
                    if (CachedIp != DeviceConfig.WledIp || CachedPort != DeviceConfig.UdpPort)
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(DeviceConfig.WledIp))
                            {
                                Endpoint = new IPEndPoint(IPAddress.Parse(DeviceConfig.WledIp), DeviceConfig.UdpPort);
                                CachedIp = DeviceConfig.WledIp;
                                CachedPort = DeviceConfig.UdpPort;
                            }
                        }
                        catch { }
                    }
                }
            }

            public void Close() { if (Serial != null && Serial.IsOpen) { try { Serial.Close(); Serial.Dispose(); } catch { } } }
        }

        private List<DeviceEngine> _engines = new List<DeviceEngine>();
        private readonly object _engineLock = new object();

        public Control GetWPFSettingsControl(PluginManager p) => new SettingsControl(this);

        public void Init(PluginManager p)
        {
            Settings = this.ReadCommonSettings("WledPixelSettings", () => new WledSettings());

            if (Settings.Profiles == null) Settings.Profiles = new System.Collections.ObjectModel.ObservableCollection<WledProfile>();
            if (Settings.Profiles.Count == 0) Settings.Profiles.Add(new WledProfile { ProfileName = "Default Profile", Game = "Any game" });
            if (Settings.SelectedProfileId == null) Settings.SelectedProfileId = Settings.Profiles.First().ProfileId;
            if (Settings.Devices == null) Settings.Devices = new System.Collections.ObjectModel.ObservableCollection<WledDevice>();

            SyncEngines();

            foreach (var profile in Settings.Profiles)
                foreach (var drs in profile.DeviceRuleSets)
                    foreach (var rule in drs.Rules)
                    {
                        if (rule.AnimationType == AnimationType.Solid)
                        {
                            if (rule.IsFlashing) rule.AnimationType = AnimationType.Flash;
                            else if (rule.IsAlternating) rule.AnimationType = AnimationType.Alternate;
                        }
                        if (rule.Segments.Count == 0)
                            rule.Segments.Add(new LedSegment
                            {
                                StartPixel   = rule.StartPixel,  StopPixel  = rule.StopPixel,
                                IdleHexColor = rule.IdleHexColor, HexColor  = rule.HexColor,
                                HexColor2    = rule.HexColor2,    HexColor3 = rule.HexColor3
                            });
                    }

            _isRunning = true;
            _renderThread = new Thread(RenderLoop) { IsBackground = true, Priority = ThreadPriority.Highest };
            _renderThread.Start();
        }

        public void End(PluginManager p)
        {
            _isRunning = false;
            _renderThread?.Join(500);
            lock (_engineLock) { foreach (var e in _engines) e.Close(); }
            SaveSettings();
        }

        public void SaveSettings() { this.SaveCommonSettings("WledPixelSettings", Settings); SyncEngines(); }

        public void SyncEngines()
        {
            lock (_engineLock)
            {
                var toRemove = _engines.Where(e => !Settings.Devices.Any(d => d.DeviceId == e.DeviceConfig.DeviceId)).ToList();
                foreach (var r in toRemove) { r.Close(); _engines.Remove(r); }
                foreach (var dev in Settings.Devices)
                {
                    if (!_engines.Any(e => e.DeviceConfig.DeviceId == dev.DeviceId)) _engines.Add(new DeviceEngine(dev));
                }
            }
        }

        public string GetRawComPort(string fullPort)
        {
            if (string.IsNullOrEmpty(fullPort)) return "";
            int dashIdx = fullPort.IndexOf(" -");
            return dashIdx > 0 ? fullPort.Substring(0, dashIdx) : fullPort;
        }

        public void StartTest(WledDevice targetDevice, byte r, byte g, byte b)
        {
            if (targetDevice == null) return;
            _testColor[0] = r; _testColor[1] = g; _testColor[2] = b;
            _testDeviceId = targetDevice.DeviceId;
        }

        public void StopTest() => _testDeviceId = "";

        public void DataUpdate(PluginManager p, ref GameData data)
        {
            bool isRunning = data.GameRunning;
            Interlocked.Exchange(ref _isGameActive, isRunning ? 1 : 0);

            string currentGame = (isRunning && !string.IsNullOrWhiteSpace(data.GameName)) ? data.GameName : "Any game";
            if (currentGame != _lastDetectedGame)
            {
                _lastDetectedGame = currentGame;
                if (Settings.SwitchMode == AutoSwitchMode.Automatic)
                {
                    string currentCar = (isRunning && p.GetPropertyValue("DataCorePlugin.GameData.NewData.CarModel") != null) ? p.GetPropertyValue("DataCorePlugin.GameData.NewData.CarModel").ToString() : "";
                    var matchedProfile = Settings.Profiles.FirstOrDefault(prof => prof.Game == currentGame && prof.Car == currentCar && !string.IsNullOrWhiteSpace(currentCar)) ?? Settings.Profiles.FirstOrDefault(prof => prof.Game == currentGame) ?? Settings.Profiles.FirstOrDefault(prof => prof.Game == "Any game");
                    if (matchedProfile != null && Settings.SelectedProfileId != matchedProfile.ProfileId)
                    {
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { Settings.SelectedProfileId = matchedProfile.ProfileId; });
                    }
                }
            }

            if (!isRunning)
            {
                lock (_engineLock) { foreach (var e in _engines) e.TargetRule = null; }
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var profile in Settings.Profiles)
                        foreach (var drs in profile.DeviceRuleSets)
                            foreach (var rule in drs.Rules)
                                if (rule.IsCurrentlyFiring) rule.IsCurrentlyFiring = false;
                }, System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            long currentMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (currentMs - _lastLogicMs < SIMHUB_THROTTLE_MS) return;
            _lastLogicMs = currentMs;

            _firingSnapshot.Clear();
            lock (_engineLock)
            {
                var activeProf = Settings.ActiveProfile;
                if (activeProf == null) return;

                foreach (var engine in _engines)
                {
                    PixelRule matchedRule = null;
                    var drs = activeProf.DeviceRuleSets.FirstOrDefault(d => d.DeviceId == engine.DeviceConfig.DeviceId);

                    if (drs != null)
                    {
                        foreach (var rule in drs.Rules)
                        {
                            if (rule.IsActive && EvaluateFormula(p, rule.Formula)) { matchedRule = rule; break; }
                        }
                    }
                    engine.TargetRule = matchedRule;
                    if (matchedRule != null)
                    {
                        var at = matchedRule.AnimationType;
                        if (at == AnimationType.BarFill || at == AnimationType.RpmSweep || at == AnimationType.Chase)
                            engine.TargetValue = EvaluateValue(p, matchedRule.ValueFormula);
                    }
                    _firingSnapshot[engine.DeviceConfig.DeviceId] = matchedRule;
                }
            }

            bool firingChanged = false;
            foreach (var kv in _firingSnapshot)
                if (!_prevFiringSnapshot.TryGetValue(kv.Key, out var prev) || prev != kv.Value) { firingChanged = true; break; }
            if (!firingChanged)
                foreach (var kv in _prevFiringSnapshot)
                    if (!_firingSnapshot.ContainsKey(kv.Key)) { firingChanged = true; break; }

            if (firingChanged)
            {
                _prevFiringSnapshot.Clear();
                foreach (var kv in _firingSnapshot) _prevFiringSnapshot[kv.Key] = kv.Value;
                var dispatchCopy = new Dictionary<string, PixelRule>(_firingSnapshot);
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    var activeProf = Settings.ActiveProfile;
                    if (activeProf == null) return;
                    foreach (var drs in activeProf.DeviceRuleSets)
                    {
                        dispatchCopy.TryGetValue(drs.DeviceId, out var targetRule);
                        foreach (var rule in drs.Rules)
                            rule.IsCurrentlyFiring = rule == targetRule;
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private string SubstituteProperties(PluginManager p, string formula)
            => _propRegex.Replace(formula, m =>
            {
                var val = p.GetPropertyValue(m.Groups[1].Value);
                return val == null ? "0" : Convert.ToString(val, CultureInfo.InvariantCulture);
            });

        private bool EvaluateFormula(PluginManager p, string f)
        {
            if (string.IsNullOrWhiteSpace(f)) return false;
            try { return Convert.ToBoolean(_dt.Compute(SubstituteProperties(p, f).Replace("==", "="), "")); }
            catch { return false; }
        }

        private double EvaluateValue(PluginManager p, string f)
        {
            if (string.IsNullOrWhiteSpace(f)) return 0;
            try { return Convert.ToDouble(_dt.Compute(SubstituteProperties(p, f), ""), CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private void RenderLoop()
        {
            Stopwatch sw = new Stopwatch();
            double targetFrameTimeMs = 16.666;

            while (_isRunning)
            {
                sw.Restart();
                long currentMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                bool isGame = Interlocked.Read(ref _isGameActive) == 1;

                lock (_engineLock)
                {
                    foreach (var engine in _engines)
                    {
                        ProcessEngine(engine, currentMs, isGame);
                    }
                }

                double elapsed = sw.Elapsed.TotalMilliseconds;
                double remainingMs = targetFrameTimeMs - elapsed;
                if (remainingMs > 2.0) { Thread.Sleep((int)(remainingMs - 2.0)); }
                SpinWait.SpinUntil(() => sw.Elapsed.TotalMilliseconds >= targetFrameTimeMs);
            }
        }

        private void ProcessEngine(DeviceEngine engine, long currentMs, bool isGame)
        {
            bool isTestingThisDevice = (engine.DeviceConfig.DeviceId == _testDeviceId);
            bool shouldBeAwake = isGame || isTestingThisDevice;
            bool isSerial = engine.DeviceConfig.ConnectionMethod == ConnectionType.Serial;

            // --- 1. HARDWARE CONNECTION MANAGEMENT ---
            if (isSerial)
            {
                if (engine.Serial == null || !engine.Serial.IsOpen)
                {
                    if (engine.DeviceConfig.ComPort != engine.CachedComPortSource)
                    {
                        engine.CachedRawPort = GetRawComPort(engine.DeviceConfig.ComPort);
                        engine.CachedComPortSource = engine.DeviceConfig.ComPort;
                    }
                    string rawPort = engine.CachedRawPort;
                    if (currentMs > engine.NextSerialAttemptMs && !string.IsNullOrWhiteSpace(rawPort))
                    {
                        try
                        {
                            if (engine.Serial != null) { engine.Serial.Dispose(); engine.Serial = null; }
                            engine.Serial = new SerialPort(rawPort, engine.DeviceConfig.BaudRate) { DtrEnable = false, RtsEnable = false, WriteTimeout = 50 };
                            engine.Serial.Open();
                            engine.SerialReadyMs = currentMs + 1000;
                        }
                        catch { engine.NextSerialAttemptMs = currentMs + 5000; return; }
                    }
                    else { return; }
                }

                if (currentMs < engine.SerialReadyMs) return;
            }
            else
            {
                engine.MaintainNetwork();
                if (engine.Endpoint == null) return;
            }

            // --- 2. WAKE UP / SHUT DOWN ROUTING ---
            if (shouldBeAwake && !engine.IsAwake)
            {
                SendJsonCommand(engine, "{\"on\":true,\"live\":true}");
                engine.IsAwake = true;
                engine.LastPresetId = -1;
                engine.PauseBinaryUntilMs = currentMs + 150;
                return;
            }
            else if (!shouldBeAwake && engine.IsAwake)
            {
                SendJsonCommand(engine, "{\"on\":false,\"live\":false}");
                engine.IsAwake = false;
                engine.LastPresetId = -1;

                if (isSerial && engine.Serial != null && engine.Serial.IsOpen)
                {
                    try { engine.Serial.Close(); engine.Serial.Dispose(); } catch { }
                    engine.Serial = null;
                }
                return;
            }

            if (!engine.IsAwake || currentMs < engine.PauseBinaryUntilMs) return;

            // --- 3. BUFFER MANAGEMENT ---
            int offset = isSerial ? 6 : 2;
            int reqSize = offset + (engine.DeviceConfig.TotalLedCount * 3);

            bool needsRealloc = engine.PayloadBuffer == null || engine.PayloadBuffer.Length != reqSize;
            if (needsRealloc)
                engine.PayloadBuffer = new byte[reqSize];
            if (needsRealloc || engine.LastMode != engine.DeviceConfig.ConnectionMethod)
            {
                if (isSerial)
                {
                    engine.PayloadBuffer[0] = 65; engine.PayloadBuffer[1] = 100; engine.PayloadBuffer[2] = 97;
                    int c = engine.DeviceConfig.TotalLedCount - 1;
                    engine.PayloadBuffer[3] = (byte)(c >> 8);
                    engine.PayloadBuffer[4] = (byte)(c & 0xFF);
                    engine.PayloadBuffer[5] = (byte)(engine.PayloadBuffer[3] ^ engine.PayloadBuffer[4] ^ 0x55);
                }
                else
                {
                    engine.PayloadBuffer[0] = 2; engine.PayloadBuffer[1] = 2;
                }
                engine.LastMode = engine.DeviceConfig.ConnectionMethod;
            }

            // --- 4. PRESET OVERRIDES & BINARY GENERATION ---
            if (isTestingThisDevice)
            {
                if (engine.LastPresetId != -1)
                {
                    SendJsonCommand(engine, "{\"on\":true,\"live\":true}");
                    engine.LastPresetId = -1;
                    engine.PauseBinaryUntilMs = currentMs + 150;
                    return;
                }

                if (IsHardwareThrottled(engine, currentMs, isSerial)) return;

                FillBuffer(engine, _testColor, offset);
                SendBuffer(engine);
            }
            else if (isGame)
            {
                if (engine.TargetRule != null)
                {
                    if (engine.TargetRule.UsePreset)
                    {
                        if (engine.LastPresetId != engine.TargetRule.PresetId)
                        {
                            SendJsonCommand(engine, $"{{\"on\":true,\"ps\":{engine.TargetRule.PresetId},\"live\":false}}");
                            engine.LastPresetId = engine.TargetRule.PresetId;
                        }
                    }
                    else
                    {
                        if (engine.LastPresetId != -1)
                        {
                            SendJsonCommand(engine, "{\"on\":true,\"live\":true}");
                            engine.LastPresetId = -1;
                            engine.PauseBinaryUntilMs = currentMs + 150;
                            return;
                        }

                        if (IsHardwareThrottled(engine, currentMs, isSerial)) return;

                        GeneratePayload(engine, currentMs, offset);
                        SendBuffer(engine);
                    }
                }
                else
                {
                    if (engine.LastPresetId != -1)
                    {
                        SendJsonCommand(engine, "{\"on\":true,\"live\":true}");
                        engine.LastPresetId = -1;
                        engine.PauseBinaryUntilMs = currentMs + 150;
                        return;
                    }

                    if (IsHardwareThrottled(engine, currentMs, isSerial)) return;

                    FillBuffer(engine, _black, offset);
                    SendBuffer(engine);
                }
            }
        }

        // --- PHYSICS-AWARE DYNAMIC THROTTLE ---
        private bool IsHardwareThrottled(DeviceEngine engine, long currentMs, bool isSerial)
        {
            if (isSerial)
            {
                // 1. Calculate physical transmission time based on configured Baud Rate
                int bytesPerFrame = 6 + (engine.DeviceConfig.TotalLedCount * 3);
                double bytesPerMs = (engine.DeviceConfig.BaudRate / 10.0) / 1000.0;

                // Failsafe against division by zero if baud rate is unconfigured
                if (bytesPerMs <= 0) bytesPerMs = 11.52;

                int msToTransmit = (int)Math.Ceiling(bytesPerFrame / bytesPerMs);

                // 2. Add a 9ms "Deaf Block" buffer. 
                // WS2812B LEDs block ESP32 hardware interrupts while rendering colors.
                int safePacingMs = msToTransmit + 9;

                // 3. Cap max speed to 60 FPS (16ms) to prevent CPU burning
                safePacingMs = Math.Max(safePacingMs, 16);

                if (currentMs - engine.LastFrameMs < safePacingMs) return true;
            }
            else
            {
                if (currentMs - engine.LastFrameMs < 16) return true;
            }

            engine.LastFrameMs = currentMs;
            return false;
        }

        private void SendBuffer(DeviceEngine engine)
        {
            try
            {
                if (engine.DeviceConfig.ConnectionMethod == ConnectionType.Serial && engine.Serial != null && engine.Serial.IsOpen)
                    engine.Serial.Write(engine.PayloadBuffer, 0, engine.PayloadBuffer.Length);
                else if (engine.DeviceConfig.ConnectionMethod == ConnectionType.Network && engine.Endpoint != null)
                    _udpClient.Send(engine.PayloadBuffer, engine.PayloadBuffer.Length, engine.Endpoint);
            }
            catch { }
        }

        private void SendJsonCommand(DeviceEngine engine, string json)
        {
            // Inject the UDP Network isolation flag into every single command.
            // This prevents the COM port device from secretly listening to WLED Wi-Fi Sync packets.
            if (json.EndsWith("}"))
            {
                json = json.Substring(0, json.Length - 1) + ",\"udpn\":{\"send\":false,\"recv\":false}}";
            }

            if (engine.DeviceConfig.ConnectionMethod == ConnectionType.Network && !string.IsNullOrEmpty(engine.DeviceConfig.WledIp))
            {
                string ip = engine.DeviceConfig.WledIp;
                Task.Run(async () => { try { using (var content = new StringContent(json, Encoding.UTF8, "application/json")) using (await _httpClient.PostAsync($"http://{ip}/json/state", content)) { } } catch { } });
            }
            else if (engine.DeviceConfig.ConnectionMethod == ConnectionType.Serial)
            {
                if (engine.Serial != null && engine.Serial.IsOpen)
                {
                    try { engine.Serial.WriteLine(json); } catch { }
                }
            }
        }

        private void FillBuffer(DeviceEngine engine, byte[] color, int offset)
        {
            for (int i = 0; i < engine.DeviceConfig.TotalLedCount; i++)
            {
                int idx = offset + (i * 3);
                engine.PayloadBuffer[idx] = color[0];
                engine.PayloadBuffer[idx + 1] = color[1];
                engine.PayloadBuffer[idx + 2] = color[2];
            }
        }

        private void GeneratePayload(DeviceEngine engine, long currentMs, int offset)
        {
            var r        = engine.TargetRule;
            int ledCount = engine.DeviceConfig.TotalLedCount;

            // Pixels outside every segment are off
            for (int i = 0; i < ledCount; i++)
            {
                int idx = offset + (i * 3);
                engine.PayloadBuffer[idx] = engine.PayloadBuffer[idx + 1] = engine.PayloadBuffer[idx + 2] = 0;
            }

            double valueRange = r.ValueMax - r.ValueMin;
            foreach (var seg in r.Segments)
            {
                int start    = Math.Max(0, seg.StartPixel);
                int stop     = Math.Min(seg.StopPixel, ledCount);
                int rangeLen = Math.Max(0, stop - start);
                if (rangeLen == 0) continue;

                HexToRgbFast(seg.IdleHexColor, engine.TempIdle);
                HexToRgbFast(seg.HexColor,     engine.TempC1);
                HexToRgbFast(seg.HexColor2,    engine.TempC2);
                HexToRgbFast(seg.HexColor3,    engine.TempC3);

                ApplySegmentAnimation(engine, r, currentMs, offset, start, stop, rangeLen, valueRange);
            }
        }

        private void ApplySegmentAnimation(DeviceEngine engine, PixelRule r, long currentMs, int offset, int start, int stop, int rangeLen, double valueRange)
        {
            // Fill this segment with its idle colour first
            for (int i = start; i < stop; i++)
            {
                int idx = offset + (i * 3);
                engine.PayloadBuffer[idx]     = engine.TempIdle[0];
                engine.PayloadBuffer[idx + 1] = engine.TempIdle[1];
                engine.PayloadBuffer[idx + 2] = engine.TempIdle[2];
            }

            switch (r.AnimationType)
            {
                case AnimationType.BarFill:
                {
                    double pct      = valueRange <= 0 ? 0 : Math.Min(1.0, Math.Max(0.0, (engine.TargetValue - r.ValueMin) / valueRange));
                    int    litCount = (int)Math.Round(pct * rangeLen);
                    for (int i = start; i < start + litCount; i++)
                    {
                        int idx = offset + (i * 3);
                        engine.PayloadBuffer[idx] = engine.TempC1[0]; engine.PayloadBuffer[idx + 1] = engine.TempC1[1]; engine.PayloadBuffer[idx + 2] = engine.TempC1[2];
                    }
                    break;
                }
                case AnimationType.RpmSweep:
                {
                    double pct      = valueRange <= 0 ? 0 : Math.Min(1.0, Math.Max(0.0, (engine.TargetValue - r.ValueMin) / valueRange));
                    int    litCount = (int)Math.Round(pct * rangeLen);
                    double z2       = r.RpmZone2Start / 100.0;
                    double z3       = r.RpmZone3Start / 100.0;
                    for (int i = start; i < start + litCount; i++)
                    {
                        int    idx      = offset + (i * 3);
                        double relPct   = (double)(i - start) / rangeLen;
                        byte[] zone     = relPct < z2 ? engine.TempC1 : (relPct < z3 ? engine.TempC2 : engine.TempC3);
                        engine.PayloadBuffer[idx] = zone[0]; engine.PayloadBuffer[idx + 1] = zone[1]; engine.PayloadBuffer[idx + 2] = zone[2];
                    }
                    break;
                }
                case AnimationType.Chase:
                {
                    double pct          = valueRange <= 0 ? 0 : Math.Min(1.0, Math.Max(0.0, (engine.TargetValue - r.ValueMin) / valueRange));
                    int    windowCenter = start + (int)Math.Round(pct * rangeLen);
                    int    halfWindow   = r.ChaseWindowSize / 2;
                    for (int i = start; i < stop; i++)
                    {
                        if (Math.Abs(i - windowCenter) <= halfWindow)
                        {
                            int idx = offset + (i * 3);
                            engine.PayloadBuffer[idx] = engine.TempC1[0]; engine.PayloadBuffer[idx + 1] = engine.TempC1[1]; engine.PayloadBuffer[idx + 2] = engine.TempC1[2];
                        }
                    }
                    break;
                }
                default: // Solid, Flash, Alternate
                {
                    for (int i = start; i < stop; i++)
                    {
                        int    idx   = offset + (i * 3);
                        byte[] color = engine.TempC1;
                        if (r.AnimationType == AnimationType.Alternate)
                            color = ((i % 2 == 0) == ((currentMs / r.AlternateSpeedMs) % 2 == 0)) ? engine.TempC1 : engine.TempC2;
                        else if (r.AnimationType == AnimationType.Flash)
                            color = ((currentMs / r.FlashSpeedMs) % 2 != 0) ? engine.TempIdle : engine.TempC1;
                        engine.PayloadBuffer[idx] = color[0]; engine.PayloadBuffer[idx + 1] = color[1]; engine.PayloadBuffer[idx + 2] = color[2];
                    }
                    break;
                }
            }
        }

        private void HexToRgbFast(string hex, byte[] targetArray)
        {
            if (string.IsNullOrEmpty(hex)) return;
            int hOffset = hex[0] == '#' ? 1 : 0;
            if (hex.Length - hOffset < 6) return;
            try
            {
                targetArray[0] = Convert.ToByte(hex.Substring(hOffset, 2), 16);
                targetArray[1] = Convert.ToByte(hex.Substring(hOffset + 2, 2), 16);
                targetArray[2] = Convert.ToByte(hex.Substring(hOffset + 4, 2), 16);
            }
            catch { }
        }

        public ImageSource PictureIcon => null;
        public string LeftMenuTitle => "WLED";
    }
}