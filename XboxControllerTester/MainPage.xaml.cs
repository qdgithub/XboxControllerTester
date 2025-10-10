using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Gaming.Input;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Input;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.System.Threading;
using System.Numerics;
using BattStatus = Windows.System.Power.BatteryStatus;

using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.Foundation.Collections;

namespace XboxControllerTester
{
    public sealed partial class MainPage : Page
    {
        // ====== Timers ======
        private readonly DispatcherTimer pollTimer;
        private ThreadPoolTimer? fastTimer16;
        private int _fastBusy = 0;

        // ====== Gamepad & rumble ======
        private Gamepad? currentGamepad;
        private enum RumbleMode { Off, Motor, Trigger }
        private RumbleMode currentRumbleMode = RumbleMode.Off;

        // ====== Header UI ======
        private TextBlock? tbRumbleStatus = null;
        private TextBlock? tbBatteryStatus = null;

        private bool lastComboMotor = false;
        private bool lastComboTrigger = false;

        // ====== Button states & counters ======
        private readonly Dictionary<string, int> buttonPressCount = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> lastBtnState = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> btnStatus = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = false,
            ["B"] = false,
            ["X"] = false,
            ["Y"] = false,
            ["Up"] = false,
            ["Down"] = false,
            ["Left"] = false,
            ["Right"] = false,
            ["Menu"] = false,
            ["View"] = false,
            ["LS"] = false,
            ["RS"] = false,
            ["LB"] = false,
            ["RB"] = false
        };

        private bool _lastLTPressedFast = false, _lastRTPressedFast = false;

        // ====== Thresholds & brushes ======
        private const double TRIGGER_SHOW_EPS = 0.001;
        private const double TRIGGER_COUNT_EDGE = 0.95;

        private static readonly Brush BrushTransparent = new SolidColorBrush(Colors.Transparent);
        private static readonly Brush BrushBlue = new SolidColorBrush(Colors.DeepSkyBlue);
        private static readonly Brush BrushPurple = new SolidColorBrush(Colors.Violet);
        private static readonly Brush BrushOrange = new SolidColorBrush(Colors.Orange);
        private static readonly Brush BrushCyan = new SolidColorBrush(Colors.MediumSpringGreen);
        private static readonly Brush BrushGray = new SolidColorBrush(Colors.Gray);
        private static readonly Brush BrushRed = new SolidColorBrush(Colors.OrangeRed);
        private static readonly Brush BrushGreen = new SolidColorBrush(Colors.LimeGreen);

        // ====== Warn rumble queue ======
        private const int THRESHOLD_COUNT_DEFAULT = 10;
        private static readonly TimeSpan WARN_DUR_SHORT = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan WARN_DUR_LONG = TimeSpan.FromMilliseconds(500);
        private const double WARN_FORCE_MED = 0.5;
        private const double WARN_FORCE_STRONG = 1.0;

        private DateTimeOffset _warnLM_Until = DateTimeOffset.MinValue; private double _warnLM_Force = 0;
        private DateTimeOffset _warnRM_Until = DateTimeOffset.MinValue; private double _warnRM_Force = 0;
        private DateTimeOffset _warnLT_Until = DateTimeOffset.MinValue; private double _warnLT_Force = 0;
        private DateTimeOffset _warnRT_Until = DateTimeOffset.MinValue; private double _warnRT_Force = 0;

        private double _testLM = 0, _testRM = 0, _testLT = 0, _testRT = 0;

        // ====== Smooth output ======
        private class SmoothChannel
        {
            public double Value = 0.0;
            public double Target = 0.0;
            public double TauUpSec = 0.018;
            public double TauDownSec = 0.032;
            public double Step(double dtSec)
            {
                double tau = (Target > Value) ? TauUpSec : TauDownSec;
                if (tau < 1e-5) { Value = Target; return Value; }
                double alpha = 1 - Math.Exp(-dtSec / tau);
                Value += (Target - Value) * alpha;
                return Value;
            }
        }
        private readonly SmoothChannel _smLM = new();
        private readonly SmoothChannel _smRM = new();
        private readonly SmoothChannel _smLT = new();
        private readonly SmoothChannel _smRT = new();
        private DateTimeOffset _lastFastRumbleAt = DateTimeOffset.Now;
        private const double OUT_DEADZONE = 0.002;
        private const double VIB_EPS = 0.001;
        private double _lastVibLM = -1, _lastVibRM = -1, _lastVibLT = -1, _lastVibRT = -1;
        private DateTimeOffset _lastVibSetAt = DateTimeOffset.MinValue;
        private static readonly TimeSpan USB_MIN_SET_INTERVAL = TimeSpan.FromMilliseconds(30);
        private static readonly TimeSpan BT_MIN_SET_INTERVAL = TimeSpan.FromMilliseconds(16);
        private const double BIG_JUMP = 0.08;

        // ====== Transport ======
        private enum ConnTransport { Unknown, Usb, Bluetooth }
        private ConnTransport currentTransport = ConnTransport.Unknown;

        // ====== Product type ======
        private enum ProductType { Unknown = 0, Jelling = 1, Durham = 2 }
        private ProductType _productType = ProductType.Unknown;

        // ====== Anti-flicker & lookahead ======
        private ProductType _candType = ProductType.Unknown;
        private int _candSeq = 0;
        private DateTimeOffset _candStartedAt = DateTimeOffset.MinValue;

        private readonly TimeSpan STABLE_JELLING;
        private readonly TimeSpan STABLE_DURHAM;
        private readonly TimeSpan STABLE_UNKNOWN;

        private DateTimeOffset _durhamLookaheadUntil = DateTimeOffset.MinValue;
        private readonly TimeSpan DURHAM_LOOKAHEAD;
        private int _lookaheadSeq = 0;

        private static readonly TimeSpan _earlyProbe = TimeSpan.FromMilliseconds(30);
        private readonly TimeSpan _microWindow;
        private DateTimeOffset _jellingGateUntil = DateTimeOffset.MinValue;
        private bool _gateArmed = false;
        private int _microRecheckSeq = 0;

        private ProductType _providerHint = ProductType.Unknown;
        private DateTimeOffset _providerHintAt = DateTimeOffset.MinValue;
        private static readonly TimeSpan PROVIDER_EVIDENCE_HOLD = TimeSpan.FromMilliseconds(8000);

        // ====== HID selectors ======
        private static readonly string _hidSelector05 = HidDevice.GetDeviceSelector(0x01, 0x05);
        private static readonly string _hidSelector06 = HidDevice.GetDeviceSelector(0x01, 0x06);
        private static readonly string[] _hidAdditionalProperties = new[]
        {
            "System.Devices.DeviceInstanceId",
            "System.Devices.HardwareIds",
            "System.ItemNameDisplay",
            "System.Devices.Aep.ModelId"
        };

        // ====== Watchers & caches ======
        private DeviceWatcher? _watch05, _watch06;
        private readonly HashSet<string> _hid05JellingIds = new();
        private readonly HashSet<string> _hid06DurhamIds = new();
        private readonly HashSet<string> _hid06AsJelling = new();
        private readonly object _hidCacheLock = new();
        private readonly SemaphoreSlim _classifyLock = new(1, 1);

        private DateTimeOffset _lastSeen05 = DateTimeOffset.MinValue;
        private DateTimeOffset _lastSeen06 = DateTimeOffset.MinValue;
        private DateTimeOffset _lastSeenRawDurham = DateTimeOffset.MinValue;
        private static readonly TimeSpan PRESENT_HOLD_05 = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan PRESENT_HOLD_06 = TimeSpan.FromMilliseconds(800);
        private const ushort XBOX_VENDOR_ID = 0x045E;
        private static readonly HashSet<ushort> KnownDurhamPids = new()
        {
            0x0B00, // Elite Series 2 wired (launch)
            0x0B01, // Elite Series 2 accessory variants
            0x0B02, // Elite Series 2 wired refresh
            0x0B03, // Elite Series 2 accessory refresh
            0x0B04, // Elite Series 2 accessory alt
            0x0B05, // Elite Series 2 Bluetooth (retail)
            0x0B06, // Elite Series 2 Bluetooth revisions
            0x0B0A, // Elite Series 2 Core wired
            0x0B0B, // Elite Series 2 Core accessory
            0x0B0C, // Elite Series 2 Core accessory alt
            0x0B0D, // Elite Series 2 Core accessory alt 2
            0x0B0E, // Elite Series 2 Core Bluetooth
            0x0B0F, // Elite Series 2 Core Bluetooth alt
            0x0B20  // Future Elite Series 2 revisions
        };

        private static readonly HashSet<ushort> KnownJellingPids = new()
        {
            0x02E0, // Xbox One Wired Controller
            0x02FD, // Xbox Wireless Controller (1708) Bluetooth
            0x02FF, // Xbox Wireless Controller (1708) USB
            0x0719, // Xbox One Wireless Controller legacy
            0x0B12, // Xbox Wireless Controller (1914) USB
            0x0B13, // Xbox Wireless Controller (1914) Bluetooth
            0x0B1C, // Xbox Wireless Controller (1914) BLE alt
            0x0B1D  // Xbox Wireless Controller (1914) USB alt
        };

        // ====== Layout ======
        private readonly (string leftLabel, string rightLabel, string leftKey, string rightKey)[] layout =
        {
            ("Guide","Share","Guide","Share"),
            ("View","Menu","View","Menu"),
            ("Down","A","Down","A"),
            ("Right","B","Right","B"),
            ("Up","Y","Up","Y"),
            ("Left","X","Left","X"),
            ("Bumper","Bumper","LB","RB"),
            ("Thumbstick","Thumbstick","LS","RS"),
            ("X","X","LS X","RS X"),
            ("Y","Y","LS Y","RS Y"),
            ("Trigger","Trigger","LT","RT"),
        };
        private static readonly string[] digitalCountKeys =
            { "Guide","Share","View","Menu","Down","A","Right","B","Up","Y","Left","X","LB","RB","LS","RS" };

        private bool uiBuilt = false;
        private readonly List<StackPanel> leftCells = new();
        private readonly List<StackPanel> rightCells = new();

        private readonly Dictionary<string, FrameworkElement[]> _segParts =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _fxLock = new();
        private readonly List<(string key, int idx)> _pendingSegFx = new();
        private readonly List<string> _pendingFinalRewards = new();

        // ====== Quick socket ======
        private StreamSocket? stateSocket; private DataWriter? stateWriter; private DataReader? stateReader;
        private readonly SemaphoreSlim _socketLock = new(1, 1);
        private DateTimeOffset _quickFailUntil = DateTimeOffset.MinValue;

        // ====== Battery ======
        private DateTimeOffset _lastBatteryUiAt = DateTimeOffset.MinValue;
        private static readonly TimeSpan BatteryUiInterval = TimeSpan.FromMilliseconds(1200);

        // ====== Snapshot ======
        private readonly object _readingLock = new();
        private GamepadReading _latestReading;
        private bool _haveReading = false;

        // ====== Misc ======
        private static readonly HashSet<string> LeftSideKeys = new(StringComparer.OrdinalIgnoreCase)
        { "Guide","View","Down","Right","Up","Left","LB","LS","LT" };
        private static readonly HashSet<string> RightSideKeys = new(StringComparer.OrdinalIgnoreCase)
        { "Share","Menu","A","B","Y","X","RB","RS","RT" };

        private double _lastLSX = double.NaN, _lastLSY = double.NaN, _lastRSX = double.NaN, _lastRSY = double.NaN;

        private readonly Compositor _compositor = Window.Current.Compositor;
        private Vector3KeyFrameAnimation? _popAnim = null;

        private double _resetHoldMs = 0;
        private DateTimeOffset _lastUiTick = DateTimeOffset.Now;

        // ====== >>> VƯỢT NGƯỠNG: state & config <<<
        // Số mốc mỗi 10 lần nhấn (10, 20, 30, ...)
        private const int THRESH_STEP = THRESHOLD_COUNT_DEFAULT;
        // Thời gian ô hiển thị đổi màu khi vượt mốc
        private static readonly TimeSpan FLASH_SHORT = TimeSpan.FromMilliseconds(450);
        // Màu nháy cảnh báo
        private static readonly Brush BrushWarn = new SolidColorBrush(Colors.Gold);
        // Lưu mốc đã kích hoạt cho từng key (để không lặp rung/đổi màu nhiều lần cho cùng mốc)
        private readonly Dictionary<string, int> _lastSegByKey = new(StringComparer.OrdinalIgnoreCase);
        // Lưu thời điểm hết hiệu ứng nháy màu cho từng key
        private readonly Dictionary<string, DateTimeOffset> _flashUntil = new(StringComparer.OrdinalIgnoreCase);

        public MainPage()
        {
            InitializeComponent();

            STABLE_JELLING = IsRunningOnXbox() ? TimeSpan.FromMilliseconds(140) : TimeSpan.FromMilliseconds(120);
            STABLE_DURHAM = IsRunningOnXbox() ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromMilliseconds(60);
            STABLE_UNKNOWN = IsRunningOnXbox() ? TimeSpan.FromMilliseconds(420) : TimeSpan.FromMilliseconds(350);
            DURHAM_LOOKAHEAD = IsRunningOnXbox() ? TimeSpan.FromMilliseconds(220) : TimeSpan.FromMilliseconds(160);
            _microWindow = IsRunningOnXbox() ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromMilliseconds(65);

            Gamepad.GamepadAdded += Gamepad_GamepadAdded;
            Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;
            RawGameController.RawGameControllerAdded += RawGameController_Added;
            RawGameController.RawGameControllerRemoved += RawGameController_Removed;

            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            pollTimer.Tick += PollTimer_Tick;

            foreach (var key in digitalCountKeys) { buttonPressCount[key] = 0; lastBtnState[key] = false; }
            buttonPressCount["LT"] = 0; buttonPressCount["RT"] = 0;

            // Khởi tạo mốc vượt ngưỡng và hiệu ứng flash cho mọi key có counter
            foreach (var k in buttonPressCount.Keys)
                _lastSegByKey[k] = 0;

            ApplyToolbarFocusPolicy();
            if (TopBar != null) TopBar.Loaded += (_, __) => DisableFocusForToolbarCommands();

            StartWatchers();
            RefreshDevices();
            _ = QuickClassifyAsync();
        }

        // ===== Helper chạy UI an toàn (fix COMException) =====
        private static async Task RunOnUiAsync(DispatchedHandler action)
        {
            static async Task<bool> TryDispatchAsync(CoreDispatcher? dispatcher, DispatchedHandler handler)
            {
                if (dispatcher == null) return false;

                if (dispatcher.HasThreadAccess)
                {
                    try { handler(); }
                    catch { }
                    return true;
                }

                try
                {
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, handler);
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            if (await TryDispatchAsync(Window.Current?.Dispatcher, action)) return;

            var mainView = CoreApplication.MainView;
            if (mainView != null && await TryDispatchAsync(mainView.CoreWindow?.Dispatcher, action)) return;

            try { action(); }
            catch { }
        }

        // ===== Focus shield for toolbar =====
        private void ApplyToolbarFocusPolicy()
        {
            if (TopBar != null)
            {
                TopBar.IsTabStop = false;
                TopBar.GettingFocus -= TopBar_GettingFocus;
                TopBar.GettingFocus += TopBar_GettingFocus;
            }
            if (btnReset != null)
            {
                btnReset.IsTabStop = false;
                btnReset.GettingFocus -= BtnReset_GettingFocus;
                btnReset.GettingFocus += BtnReset_GettingFocus;
            }

            DisableFocusForToolbarCommands();

            if (TopBar != null)
            {
                if (TopBar.PrimaryCommands is IObservableVector<ICommandBarElement> ov1)
                    ov1.VectorChanged += (_, __) => DisableFocusForToolbarCommands();
                if (TopBar.SecondaryCommands is IObservableVector<ICommandBarElement> ov2)
                    ov2.VectorChanged += (_, __) => DisableFocusForToolbarCommands();
            }
        }
        private void DisableFocusForToolbarCommands()
        {
            if (TopBar == null) return;
            void Patch(IList<ICommandBarElement> list)
            {
                foreach (var el in list)
                {
                    if (el is AppBarButton b)
                    { b.IsTabStop = false; b.GettingFocus -= ToolbarItem_GettingFocus; b.GettingFocus += ToolbarItem_GettingFocus; }
                    else if (el is AppBarToggleButton t)
                    { t.IsTabStop = false; t.GettingFocus -= ToolbarItem_GettingFocus; t.GettingFocus += ToolbarItem_GettingFocus; }
                    else if (el is UIElement u)
                    { u.GettingFocus -= ToolbarItem_GettingFocus; u.GettingFocus += ToolbarItem_GettingFocus; }
                }
            }
            Patch(TopBar.PrimaryCommands);
            Patch(TopBar.SecondaryCommands);
        }
        private void ToolbarItem_GettingFocus(object? sender, GettingFocusEventArgs e)
        { if (e.InputDevice == FocusInputDeviceKind.GameController) e.Cancel = true; }
        private void TopBar_GettingFocus(object? sender, GettingFocusEventArgs e)
        { if (e.InputDevice == FocusInputDeviceKind.GameController) e.Cancel = true; }
        private void BtnReset_GettingFocus(object? sender, GettingFocusEventArgs e)
        { if (e.InputDevice == FocusInputDeviceKind.GameController) e.Cancel = true; }

        // ===== Page lifecycle =====
        private void Page_Loaded(object? sender, RoutedEventArgs e)
        {
            pollTimer.Start();
            fastTimer16 = ThreadPoolTimer.CreatePeriodicTimer(FastTimer_Tick, TimeSpan.FromMilliseconds(16));
            _lastUiTick = DateTimeOffset.Now;
        }
        private void Page_Unloaded(object? sender, RoutedEventArgs e)
        {
            pollTimer.Stop();
            try { fastTimer16?.Cancel(); } catch { }
            fastTimer16 = null;
            DisposeStateSocket();
            StopWatchers();
            RawGameController.RawGameControllerAdded -= RawGameController_Added;
            RawGameController.RawGameControllerRemoved -= RawGameController_Removed;
        }

        // ===== Gamepad/Raw events =====
        private void Gamepad_GamepadAdded(object? sender, Gamepad e) => RefreshDevices();
        private void Gamepad_GamepadRemoved(object? sender, Gamepad e)
        {
            if (e == currentGamepad) { StopRumble(); currentGamepad = null; }
            ForceUnknownUi();
        }
        private void RawGameController_Added(object? sender, RawGameController e)
        { if (IsDurhamRaw(e)) { _lastSeenRawDurham = DateTimeOffset.Now; TryPublish(ProductType.Durham, null, false); } }
        private void RawGameController_Removed(object? sender, RawGameController e)
        { if (IsDurhamRaw(e)) { _lastSeenRawDurham = DateTimeOffset.MinValue; TryPublish(ProductType.Unknown, null, false); } }

        // ===== Reset counts =====
        private void ResetCounts_Click(object? sender, RoutedEventArgs e) => DoResetCounts();
        private void DoResetCounts()
        {
            var keys = new List<string>(buttonPressCount.Keys);
            foreach (var k in keys) buttonPressCount[k] = 0;
            _segParts.Clear();
            _pendingSegFx.Clear();
            _pendingFinalRewards.Clear();

            // Reset state của "vượt ngưỡng"
            _flashUntil.Clear();
            _lastSegByKey.Clear();
            foreach (var k in buttonPressCount.Keys) _lastSegByKey[k] = 0;
        }

        // ===== Devices & transport =====
        private async void RefreshDevices()
        {
            var pads = Gamepad.Gamepads;
            currentGamepad = pads.Count > 0 ? pads[0] : null;

            if (currentGamepad == null)
            { currentTransport = ConnTransport.Unknown; ForceUnknownUi(); return; }

            currentTransport = await DetectTransportAsync(currentGamepad) ?? ConnTransport.Unknown;
            UpdateBorderByTransport();
            UpdateProviderEvidence(currentGamepad);
            _ = QuickClassifyAsync();
        }
        private async Task<ConnTransport?> DetectTransportAsync(Gamepad gp)
        { await Task.Yield(); try { return gp.IsWireless ? ConnTransport.Bluetooth : ConnTransport.Usb; } catch { return ConnTransport.Unknown; } }
        private void UpdateBorderByTransport()
        {
            Brush b = BrushTransparent;
            if (currentGamepad != null)
            {
                switch (currentTransport)
                {
                    case ConnTransport.Bluetooth:
                        b = BrushPurple;
                        break;
                    case ConnTransport.Usb:
                        b = BrushGreen;
                        break;
                    default:
                        b = BrushBlue;
                        break;
                }
            }

            _ = RunOnUiAsync(() => gridBorder.BorderBrush = b);
        }
        public static bool IsRunningOnXbox() =>
            Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";

        // ===== Watchers (no props → tránh CCW crash) =====
        private void StartWatchers()
        {
            StopWatchers();
            _watch05 = DeviceInformation.CreateWatcher(_hidSelector05, _hidAdditionalProperties);
            _watch06 = DeviceInformation.CreateWatcher(_hidSelector06, _hidAdditionalProperties);

            _watch05.Added += Watch05_Added;
            _watch05.Removed += Watch05_Removed;
            _watch06.Added += Watch06_Added;
            _watch06.Removed += Watch06_Removed;

            _watch05.Start();
            _watch06.Start();
        }
        private void StopWatchers()
        {
            try
            {
                if (_watch05 != null)
                { _watch05.Added -= Watch05_Added; _watch05.Removed -= Watch05_Removed; _watch05.Stop(); }
                if (_watch06 != null)
                { _watch06.Added -= Watch06_Added; _watch06.Removed -= Watch06_Removed; _watch06.Stop(); }
            }
            catch { }
            finally { _watch05 = null; _watch06 = null; }
        }

        private static bool TryGetVidPid(string id, out ushort vid, out ushort pid)
        {
            vid = 0; pid = 0;
            if (string.IsNullOrEmpty(id)) return false;

            bool vidOk = false, pidOk = false;

            int vidIdx = id.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            if (vidIdx >= 0 && vidIdx + 8 <= id.Length)
            {
                string hex = id.Substring(vidIdx + 4, 4);
                vidOk = ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid);
            }

            int pidIdx = id.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
            if (pidIdx >= 0 && pidIdx + 8 <= id.Length)
            {
                string hex = id.Substring(pidIdx + 4, 4);
                pidOk = ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
            }

            return vidOk && pidOk;
        }

        private static bool TryResolveVidPid(DeviceInformation? di, out ushort vid, out ushort pid)
        {
            vid = 0; pid = 0;
            if (di == null) return false;

            if (TryGetVidPid(di.Id ?? string.Empty, out vid, out pid))
                return true;

            try
            {
                if (di.Properties != null)
                {
                    if (di.Properties.TryGetValue("System.Devices.DeviceInstanceId", out object instObj) && instObj is string instStr)
                        if (TryGetVidPid(instStr, out vid, out pid))
                            return true;

                    if (di.Properties.TryGetValue("System.Devices.HardwareIds", out object hwObj))
                    {
                        switch (hwObj)
                        {
                            case IEnumerable<string> list:
                                foreach (var entry in list)
                                    if (TryGetVidPid(entry, out vid, out pid))
                                        return true;
                                break;
                            case IEnumerable<object> objList:
                                foreach (var entry in objList)
                                    if (entry is string s && TryGetVidPid(s, out vid, out pid))
                                        return true;
                                break;
                            case string single:
                                if (TryGetVidPid(single, out vid, out pid))
                                    return true;
                                break;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        private static bool ContainsEliteKeyword(string value) =>
            !string.IsNullOrEmpty(value) && value.IndexOf("Elite", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsDurhamPid(ushort pid) => KnownDurhamPids.Contains(pid);
        private static bool IsJellingPid(ushort pid) => KnownJellingPids.Contains(pid);

        private static bool LooksLikeXboxHid(DeviceInformation? di)
        {
            if (di == null) return false;

            if (TryResolveVidPid(di, out ushort vid, out _))
                if (vid == XBOX_VENDOR_ID) return true;

            string name = di.Name ?? string.Empty;
            return name.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool LooksLikeDurhamDevice(DeviceInformation? di)
        {
            if (di == null) return false;

            string name = di.Name ?? string.Empty;
            if (ContainsEliteKeyword(name))
                return true;

            try
            {
                if (di.Properties != null)
                {
                    if (di.Properties.TryGetValue("System.ItemNameDisplay", out object displayObj) && displayObj is string displayStr)
                        if (ContainsEliteKeyword(displayStr))
                            return true;

                    if (di.Properties.TryGetValue("System.Devices.Aep.ModelId", out object modelObj) && modelObj is string modelStr)
                        if (ContainsEliteKeyword(modelStr))
                            return true;
                }
            }
            catch { }

            if (!TryResolveVidPid(di, out ushort vid, out ushort pid))
                return false;

            if (vid != XBOX_VENDOR_ID)
                return false;

            if (IsDurhamPid(pid))
                return true;

            try
            {
                foreach (var rgc in RawGameController.RawGameControllers)
                {
                    if (rgc.HardwareVendorId == vid && rgc.HardwareProductId == pid && IsDurhamRaw(rgc))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private bool TryGetGamepadVidPid(Gamepad? gp, out ushort vid, out ushort pid)
        {
            vid = 0; pid = 0;
            if (gp == null) return false;

            bool TryUseRaw(RawGameController? raw)
            {
                if (raw == null) return false;
                try
                {
                    uint rawVid = raw.HardwareVendorId;
                    uint rawPid = raw.HardwareProductId;
                    if (rawVid == 0 && rawPid == 0)
                        return false;

                    vid = (ushort)rawVid;
                    pid = (ushort)rawPid;
                    return true;
                }
                catch { return false; }
            }

            try
            {
                if (TryUseRaw(RawGameController.FromGameController(gp)))
                    return true;
            }
            catch { }

            try
            {
                var user = gp.User;
                var all = RawGameController.RawGameControllers;
                if (user != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        var raw = all[i];
                        if (raw != null && raw.User == user && TryUseRaw(raw))
                            return true;
                    }
                }

                if (all.Count == 1)
                    if (TryUseRaw(all[0]))
                        return true;
            }
            catch { }

            return false;
        }

        private ProductType ClassifyGamepad(Gamepad? gp)
        {
            if (gp == null) return ProductType.Unknown;

            if (TryGetGamepadVidPid(gp, out ushort vid, out ushort pid))
            {
                if (vid == XBOX_VENDOR_ID)
                {
                    if (IsDurhamPid(pid))
                        return ProductType.Durham;
                    if (IsJellingPid(pid))
                        return ProductType.Jelling;
                }

                try
                {
                    foreach (var rgc in RawGameController.RawGameControllers)
                    {
                        if (rgc.HardwareVendorId == vid && rgc.HardwareProductId == pid)
                        {
                            if (IsDurhamRaw(rgc))
                                return ProductType.Durham;
                        }
                    }
                }
                catch { }
            }

            return ProductType.Unknown;
        }

        private void UpdateProviderEvidence(Gamepad? gp)
        {
            if (gp == null)
            {
                _providerHint = ProductType.Unknown;
                _providerHintAt = DateTimeOffset.MinValue;
                return;
            }

            var hint = ClassifyGamepad(gp);
            _providerHint = hint;
            _providerHintAt = DateTimeOffset.Now;

            if (hint == ProductType.Durham)
                _durhamLookaheadUntil = DateTimeOffset.Now + DURHAM_LOOKAHEAD;

            if (hint != ProductType.Unknown)
                TryPublish(hint, null, false);
        }

        private void Watch05_Added(DeviceWatcher s, DeviceInformation di)
        {
            if (!LooksLikeXboxHid(di)) return;

            var id = di.Id;
            if (string.IsNullOrEmpty(id)) return;

            lock (_hidCacheLock)
            {
                _hid06AsJelling.Remove(id);
                _hid05JellingIds.Add(id);
                _lastSeen05 = DateTimeOffset.Now;
            }
            TryPublish(ProductType.Jelling, null, false);
        }
        private void Watch05_Removed(DeviceWatcher s, DeviceInformationUpdate up)
        {
            lock (_hidCacheLock)
            {
                if (_hid05JellingIds.Remove(up.Id) && _hid05JellingIds.Count == 0)
                    _lastSeen05 = DateTimeOffset.MinValue;
                _hid06AsJelling.Remove(up.Id);
            }
            TryPublish(ProductType.Unknown, null, false);
        }
        private void Watch06_Added(DeviceWatcher s, DeviceInformation di)
        {
            if (!LooksLikeXboxHid(di)) return;

            var id = di.Id;
            if (string.IsNullOrEmpty(id)) return;

            bool isDurham = LooksLikeDurhamDevice(di);

            lock (_hidCacheLock)
            {
                if (isDurham)
                {
                    _hid06AsJelling.Remove(id);
                    _hid06DurhamIds.Add(id);
                    _lastSeen06 = DateTimeOffset.Now;
                }
                else
                {
                    _hid06AsJelling.Add(id);
                    _hid05JellingIds.Add(id);
                    _lastSeen05 = DateTimeOffset.Now;
                }
            }
            if (isDurham)
            {
                _durhamLookaheadUntil = DateTimeOffset.Now + DURHAM_LOOKAHEAD;
                TryPublish(ProductType.Durham, null, false);
            }
            else
            {
                TryPublish(ProductType.Jelling, null, false);
            }
        }
        private void Watch06_Removed(DeviceWatcher s, DeviceInformationUpdate up)
        {
            lock (_hidCacheLock)
            {
                if (_hid06DurhamIds.Remove(up.Id) && _hid06DurhamIds.Count == 0)
                    _lastSeen06 = DateTimeOffset.MinValue;
                if (_hid06AsJelling.Remove(up.Id))
                {
                    if (_hid05JellingIds.Remove(up.Id) && _hid05JellingIds.Count == 0)
                        _lastSeen05 = DateTimeOffset.MinValue;
                }
            }
            TryPublish(ProductType.Unknown, null, false);
        }

        private bool IsDurhamRaw(RawGameController rgc)
        {
            try
            {
                if (rgc == null) return false;

                if (rgc.HardwareVendorId == XBOX_VENDOR_ID && IsDurhamPid((ushort)rgc.HardwareProductId))
                    return true;

                var name = rgc.DisplayName ?? string.Empty;
                return ContainsEliteKeyword(name);
            }
            catch { return false; }
        }

        private void TryPublish(ProductType proposal, object? sender = null, bool fromReset = false)
        {
            _ = sender;
            _ = fromReset;

            if (DateTimeOffset.Now < _durhamLookaheadUntil && proposal == ProductType.Jelling)
                proposal = ProductType.Durham;

            var now = DateTimeOffset.Now;
            bool has05 = (now - _lastSeen05) <= PRESENT_HOLD_05 && _hid05JellingIds.Count > 0;
            bool has06 = (now - _lastSeen06) <= PRESENT_HOLD_06 && _hid06DurhamIds.Count > 0;
            bool hasRawDurham = (now - _lastSeenRawDurham) <= PRESENT_HOLD_06;
            bool hasProvider = (now - _providerHintAt) <= PROVIDER_EVIDENCE_HOLD && _providerHint != ProductType.Unknown;

            if (hasProvider)
            {
                if (_providerHint == ProductType.Durham)
                {
                    has06 = true;
                    hasRawDurham = true;
                }
                else if (_providerHint == ProductType.Jelling)
                {
                    has05 = true;
                }
            }

            if (!has05 && !has06 && !hasRawDurham) proposal = ProductType.Unknown;
            else if (has06 || hasRawDurham) proposal = ProductType.Durham;
            else if (has05) proposal = ProductType.Jelling;

            if (hasProvider && _providerHint != ProductType.Unknown)
                proposal = _providerHint;

            if (_candType != proposal)
            { _candType = proposal; _candStartedAt = now; _candSeq++; }

            TimeSpan need = proposal switch
            {
                ProductType.Durham => STABLE_DURHAM,
                ProductType.Jelling => STABLE_JELLING,
                _ => STABLE_UNKNOWN
            };

            if ((now - _candStartedAt) >= need)
            {
                if (_productType != proposal)
                {
                    _productType = proposal;
                    UpdateRumbleHeader(); // chạy qua dispatcher bên trong
                }
            }
        }

        private async Task QuickClassifyAsync()
        {
            foreach (var rgc in RawGameController.RawGameControllers)
                if (IsDurhamRaw(rgc)) { _lastSeenRawDurham = DateTimeOffset.Now; TryPublish(ProductType.Durham, null, false); return; }

            try
            {
                var d6 = await DeviceInformation.FindAllAsync(_hidSelector06, _hidAdditionalProperties);
                for (int i = 0; i < d6.Count; i++)
                {
                    var info = d6[i];
                    if (!LooksLikeXboxHid(info)) continue;

                    var id = info.Id;
                    if (string.IsNullOrEmpty(id)) continue;

                    bool isDurham = LooksLikeDurhamDevice(info);

                    lock (_hidCacheLock)
                    {
                        if (isDurham)
                        {
                            _hid06AsJelling.Remove(id);
                            _hid06DurhamIds.Add(id);
                            _lastSeen06 = DateTimeOffset.Now;
                        }
                        else
                        {
                            _hid06AsJelling.Add(id);
                            _hid05JellingIds.Add(id);
                            _lastSeen05 = DateTimeOffset.Now;
                        }
                    }
                    if (isDurham)
                    {
                        _durhamLookaheadUntil = DateTimeOffset.Now + DURHAM_LOOKAHEAD;
                        TryPublish(ProductType.Durham, null, false);
                    }
                    else
                    {
                        TryPublish(ProductType.Jelling, null, false);
                    }
                    return;
                }

                var d5 = await DeviceInformation.FindAllAsync(_hidSelector05, _hidAdditionalProperties);
                for (int i = 0; i < d5.Count; i++)
                {
                    var info = d5[i];
                    if (!LooksLikeXboxHid(info)) continue;

                    var id = info.Id;
                    if (string.IsNullOrEmpty(id)) continue;

                    lock (_hidCacheLock)
                    {
                        _hid06AsJelling.Remove(id);
                        _hid05JellingIds.Add(id);
                        _lastSeen05 = DateTimeOffset.Now;
                    }
                    TryPublish(ProductType.Jelling, null, false);
                    return;
                }
            }
            catch { }

            TryPublish(ProductType.Unknown, null, false);
        }

        private void ForceUnknownUi()
        {
            _productType = ProductType.Unknown;
            _hid05JellingIds.Clear();
            _hid06DurhamIds.Clear();
            _hid06AsJelling.Clear();
            _lastSeen05 = _lastSeen06 = DateTimeOffset.MinValue;
            _lastSeenRawDurham = DateTimeOffset.MinValue;
            _providerHint = ProductType.Unknown;
            _providerHintAt = DateTimeOffset.MinValue;

            _ = RunOnUiAsync(() =>
            {
                UpdateRumbleHeader_NoDispatch();
                gridBorder.BorderBrush = BrushTransparent;
            });
        }

        // ===== Fast 16ms loop =====
        private async void FastTimer_Tick(ThreadPoolTimer t)
        {
            if (Interlocked.Exchange(ref _fastBusy, 1) == 1) return;
            try
            {
                int quickTimeout = (currentTransport == ConnTransport.Usb) ? 18 : 24;
                var quick = await ReadGuideShareQuickAsync(quickTimeout);
                if (quick.HasValue)
                {
                    bool guideNow = quick.Value.guide, shareNow = quick.Value.share;
                    bool lastG = lastBtnState.TryGetValue("Guide", out var lg) && lg;
                    bool lastS = lastBtnState.TryGetValue("Share", out var ls) && ls;

                    if (!lastG && guideNow) buttonPressCount["Guide"]++;
                    if (!lastS && shareNow) buttonPressCount["Share"]++;
                    lastBtnState["Guide"] = guideNow; lastBtnState["Share"] = shareNow;
                }

                var gp = currentGamepad; if (gp == null) return;
                GamepadReading r; try { r = gp.GetCurrentReading(); } catch { return; }
                lock (_readingLock) { _latestReading = r; _haveReading = true; }

                bool ltPressed = r.LeftTrigger > TRIGGER_COUNT_EDGE;
                if (!_lastLTPressedFast && ltPressed) buttonPressCount["LT"]++;
                _lastLTPressedFast = ltPressed;

                bool rtPressed = r.RightTrigger > TRIGGER_COUNT_EDGE;
                if (!_lastRTPressedFast && rtPressed) buttonPressCount["RT"]++;
                _lastRTPressedFast = rtPressed;

                UpdateTestRumbleFromReading(r);

                var now = DateTimeOffset.Now;
                double dt = Math.Max(0.001, (now - _lastFastRumbleAt).TotalSeconds);
                _lastFastRumbleAt = now;

                double warnLM = (now < _warnLM_Until) ? _warnLM_Force : 0;
                double warnRM = (now < _warnRM_Until) ? _warnRM_Force : 0;
                double warnLT = (now < _warnLT_Until) ? _warnLT_Force : 0;
                double warnRT = (now < _warnRT_Until) ? _warnRT_Force : 0;

                _smLM.Target = (_testLM > VIB_EPS) ? _testLM : warnLM;
                _smRM.Target = (_testRM > VIB_EPS) ? _testRM : warnRM;
                _smLT.Target = (_testLT > VIB_EPS) ? _testLT : warnLT;
                _smRT.Target = (_testRT > VIB_EPS) ? _testRT : warnRT;

                double outLM = _smLM.Step(dt);
                double outRM = _smRM.Step(dt);
                double outLT = _smLT.Step(dt);
                double outRT = _smRT.Step(dt);

                if (outLM < OUT_DEADZONE) outLM = 0;
                if (outRM < OUT_DEADZONE) outRM = 0;
                if (outLT < OUT_DEADZONE) outLT = 0;
                if (outRT < OUT_DEADZONE) outRT = 0;

                double diffEps = (currentTransport == ConnTransport.Usb) ? 0.004 : 0.002;
                bool bigJump =
                    Math.Abs(outLM - _lastVibLM) > BIG_JUMP ||
                    Math.Abs(outRM - _lastVibRM) > BIG_JUMP ||
                    Math.Abs(outLT - _lastVibLT) > BIG_JUMP ||
                    Math.Abs(outRT - _lastVibRT) > BIG_JUMP;

                bool needSet =
                    Math.Abs(outLM - _lastVibLM) > diffEps ||
                    Math.Abs(outRM - _lastVibRM) > diffEps ||
                    Math.Abs(outLT - _lastVibLT) > diffEps ||
                    Math.Abs(outRT - _lastVibRT) > diffEps;

                var minInterval = (currentTransport == ConnTransport.Usb) ? USB_MIN_SET_INTERVAL : BT_MIN_SET_INTERVAL;
                bool timeOk = (now - _lastVibSetAt) >= minInterval || bigJump;

                if (needSet && timeOk)
                {
                    try
                    {
                        gp.Vibration = new GamepadVibration
                        {
                            LeftMotor = outLM,
                            RightMotor = outRM,
                            LeftTrigger = outLT,
                            RightTrigger = outRT
                        };
                        _lastVibLM = outLM; _lastVibRM = outRM; _lastVibLT = outLT; _lastVibRT = outRT;
                        _lastVibSetAt = now;
                    }
                    catch { }
                }
            }
            finally { Interlocked.Exchange(ref _fastBusy, 0); }
        }

        // ===== 33ms UI loop =====
        private bool _isPolling = false;
        private void PollTimer_Tick(object? sender, object e)
        {
            if (_isPolling) return; _isPolling = true;
            try
            {
                var nowTick = DateTimeOffset.Now;
                double dtMs = (nowTick - _lastUiTick).TotalMilliseconds;
                _lastUiTick = nowTick;

                BuildUiIfNeeded();

                GamepadReading reading;
                lock (_readingLock) { if (!_haveReading) return; reading = _latestReading; }

                btnStatus["A"] = (reading.Buttons & GamepadButtons.A) != 0;
                btnStatus["B"] = (reading.Buttons & GamepadButtons.B) != 0;
                btnStatus["X"] = (reading.Buttons & GamepadButtons.X) != 0;
                btnStatus["Y"] = (reading.Buttons & GamepadButtons.Y) != 0;
                btnStatus["Up"] = (reading.Buttons & GamepadButtons.DPadUp) != 0;
                btnStatus["Down"] = (reading.Buttons & GamepadButtons.DPadDown) != 0;
                btnStatus["Left"] = (reading.Buttons & GamepadButtons.DPadLeft) != 0;
                btnStatus["Right"] = (reading.Buttons & GamepadButtons.DPadRight) != 0;
                btnStatus["Menu"] = (reading.Buttons & GamepadButtons.Menu) != 0;
                btnStatus["View"] = (reading.Buttons & GamepadButtons.View) != 0;
                btnStatus["LS"] = (reading.Buttons & GamepadButtons.LeftThumbstick) != 0;
                btnStatus["RS"] = (reading.Buttons & GamepadButtons.RightThumbstick) != 0;
                btnStatus["LB"] = (reading.Buttons & GamepadButtons.LeftShoulder) != 0;
                btnStatus["RB"] = (reading.Buttons & GamepadButtons.RightShoulder) != 0;

                foreach (var key in digitalCountKeys)
                {
                    if (key == "Guide" || key == "Share") continue;
                    bool isPressed = btnStatus.TryGetValue(key, out bool v) && v;
                    bool last = lastBtnState.TryGetValue(key, out bool lv) && lv;
                    if (!last && isPressed) buttonPressCount[key]++;
                    lastBtnState[key] = isPressed;
                }

                // Cập nhật header (chế độ rung / loại tay cầm) + pin
                UpdateRumbleHeader();
                _ = UpdateBatteryHeaderAsync();

                // Tổ hợp toggle chế độ rung (LB+RB+DPadUp/Down)
                bool lb = btnStatus["LB"], rb = btnStatus["RB"], dpadUp = btnStatus["Up"], dpadDown = btnStatus["Down"];
                bool comboMotor = lb && rb && dpadUp;
                if (comboMotor && !lastComboMotor)
                { currentRumbleMode = (currentRumbleMode == RumbleMode.Motor) ? RumbleMode.Off : RumbleMode.Motor; UpdateRumbleHeader(); }
                lastComboMotor = comboMotor;

                bool comboTrigger = lb && rb && dpadDown;
                if (comboTrigger && !lastComboTrigger)
                { currentRumbleMode = (currentRumbleMode == RumbleMode.Trigger) ? RumbleMode.Off : RumbleMode.Trigger; UpdateRumbleHeader(); }
                lastComboTrigger = comboTrigger;

                // Giữ Reset counts (LB+RB+Menu giữ 1.2s)
                bool menu = (reading.Buttons & GamepadButtons.Menu) != 0;
                if (lb && rb && menu)
                {
                    _resetHoldMs += dtMs;
                    if (_resetHoldMs >= 1200) { _resetHoldMs = 0; DoResetCounts(); }
                }
                else _resetHoldMs = 0;

                bool guide = lastBtnState.TryGetValue("Guide", out var lg) && lg;
                bool share = lastBtnState.TryGetValue("Share", out var ls) && ls;

                // >>> Kiểm tra "vượt ngưỡng" để phát cảnh báo (đổi màu + rung)
                CheckThresholdsAndWarn();

                // Vẽ UI các ô
                for (int i = 0; i < layout.Length; i++)
                {
                    var (leftLabel, rightLabel, leftKey, rightKey) = layout[i];
                    RenderCell(leftCells[i], leftLabel, leftKey, reading, guide);
                    RenderCell(rightCells[i], rightLabel, rightKey, reading, share);
                }
            }
            finally { _isPolling = false; }
        }

        // ====== Vẽ một ô trạng thái (có hỗ trợ "flash" khi vượt ngưỡng) ======
        private void RenderCell(StackPanel sp, string label, string key, GamepadReading r, bool special)
        {
            Brush on = BrushOrange, off = BrushGray;

            switch (key)
            {
                case "Guide":
                    RenderSimple(sp, $"Guide:{buttonPressCount["Guide"]}", GetWithFlash("Guide", special ? on : off));
                    break;
                case "Share":
                    RenderSimple(sp, $"Share:{buttonPressCount["Share"]}", GetWithFlash("Share", special ? on : off));
                    break;
                case "LB":
                    RenderSimple(sp, $"Bumper:{buttonPressCount["LB"]}", GetWithFlash("LB", btnStatus["LB"] ? on : off));
                    break;
                case "RB":
                    RenderSimple(sp, $"Bumper:{buttonPressCount["RB"]}", GetWithFlash("RB", btnStatus["RB"] ? on : off));
                    break;
                case "LT":
                    {
                        var baseBrush = (r.LeftTrigger > TRIGGER_SHOW_EPS) ? on : off;
                        RenderSimple(sp, $"Trigger:{r.LeftTrigger:0.000} ({buttonPressCount["LT"]})", GetWithFlash("LT", baseBrush));
                        break;
                    }
                case "RT":
                    {
                        var baseBrush = (r.RightTrigger > TRIGGER_SHOW_EPS) ? on : off;
                        RenderSimple(sp, $"Trigger:{r.RightTrigger:0.000} ({buttonPressCount["RT"]})", GetWithFlash("RT", baseBrush));
                        break;
                    }
                case "LS X":
                    RenderSimple(sp, $"X: {r.LeftThumbstickX:0.000}", Math.Abs(r.LeftThumbstickX) > 0.15 ? BrushCyan : off);
                    break;
                case "LS Y":
                    RenderSimple(sp, $"Y: {r.LeftThumbstickY:0.000}", Math.Abs(r.LeftThumbstickY) > 0.15 ? BrushCyan : off);
                    break;
                case "RS X":
                    RenderSimple(sp, $"X: {r.RightThumbstickX:0.000}", Math.Abs(r.RightThumbstickX) > 0.15 ? BrushCyan : off);
                    break;
                case "RS Y":
                    RenderSimple(sp, $"Y: {r.RightThumbstickY:0.000}", Math.Abs(r.RightThumbstickY) > 0.15 ? BrushCyan : off);
                    break;
                default:
                    bool pressed = btnStatus.TryGetValue(key, out bool v) && v;
                    Brush baseBrush2 = GetWithFlash(key, pressed ? on : off);
                    if (buttonPressCount.ContainsKey(key))
                        RenderSimple(sp, $"{label}: {buttonPressCount[key]}", baseBrush2);
                    else
                        RenderSimple(sp, $"{label}:", baseBrush2);
                    break;
            }
        }

        private void BuildUiIfNeeded()
        {
            if (uiBuilt) return;

            gridStatus.Children.Clear(); gridStatus.RowDefinitions.Clear(); gridStatus.ColumnDefinitions.Clear();
            gridStatus.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            gridStatus.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            tbRumbleStatus = new TextBlock { FontSize = 20, FontWeight = Windows.UI.Text.FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
            gridStatus.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(tbRumbleStatus, 0); Grid.SetColumnSpan(tbRumbleStatus, 2); gridStatus.Children.Add(tbRumbleStatus);

            tbBatteryStatus = new TextBlock { FontSize = 16, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = BrushGray };
            gridStatus.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(tbBatteryStatus, 1); Grid.SetColumnSpan(tbBatteryStatus, 2); gridStatus.Children.Add(tbBatteryStatus);

            for (int i = 0; i < layout.Length; i++)
            {
                gridStatus.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var left = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 2, 8, 2) };
                var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 2, 8, 2) };

                Grid.SetRow(left, i + 2); Grid.SetColumn(left, 0);
                Grid.SetRow(right, i + 2); Grid.SetColumn(right, 1);

                gridStatus.Children.Add(left); gridStatus.Children.Add(right);
                leftCells.Add(left); rightCells.Add(right);
            }

            UpdateBorderByTransport();
            UpdateRumbleHeader_NoDispatch();
            _ = UpdateBatteryHeaderAsync();
            uiBuilt = true;
        }

        private void RenderSimple(StackPanel sp, string text, Brush color)
        {
            if (sp.Children.Count == 1 && sp.Children[0] is TextBlock existed)
            {
                if (existed.Text != text) existed.Text = text;
                if (!ReferenceEquals(existed.Foreground, color)) existed.Foreground = color;
                return;
            }
            sp.Children.Clear();
            sp.Children.Add(new TextBlock { Text = text, Foreground = color, FontSize = 20, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5) });
        }

        private string GetProductTypeLabel() =>
            _productType == ProductType.Jelling ? "Loại: Jelling" :
            _productType == ProductType.Durham ? "Loại: Durham" : "Loại: Unknown";

        // Version không dispatch: chỉ dùng nội bộ khi chắc chắn đang ở UI (BuildUiIfNeeded / ForceUnknownUi)
        private void UpdateRumbleHeader_NoDispatch()
        {
            if (tbRumbleStatus == null) return;

            var modeText = currentRumbleMode switch
            {
                RumbleMode.Motor => "Chế độ rung: Motor",
                RumbleMode.Trigger => "Chế độ rung: Trigger",
                _ => "Chế độ rung: Tắt"
            };
            var color = currentRumbleMode switch
            {
                RumbleMode.Motor => BrushBlue,
                RumbleMode.Trigger => BrushRed,
                _ => BrushGray
            };
            string productText = GetProductTypeLabel();
            string headerText = $"{modeText}  |  {productText}";

            if (tbRumbleStatus.Text != headerText) tbRumbleStatus.Text = headerText;
            if (!ReferenceEquals(tbRumbleStatus.Foreground, color)) tbRumbleStatus.Foreground = color;
        }

        // Version an toàn dùng mọi nơi (fix COMException)
        private void UpdateRumbleHeader()
        {
            var modeText = currentRumbleMode switch
            {
                RumbleMode.Motor => "Chế độ rung: Motor",
                RumbleMode.Trigger => "Chế độ rung: Trigger",
                _ => "Chế độ rung: Tắt"
            };
            var color = currentRumbleMode switch
            {
                RumbleMode.Motor => BrushBlue,
                RumbleMode.Trigger => BrushRed,
                _ => BrushGray
            };
            string productText = GetProductTypeLabel();
            string headerText = $"{modeText}  |  {productText}";

            _ = RunOnUiAsync(() =>
            {
                if (tbRumbleStatus == null) return;
                if (tbRumbleStatus.Text != headerText) tbRumbleStatus.Text = headerText;
                if (!ReferenceEquals(tbRumbleStatus.Foreground, color)) tbRumbleStatus.Foreground = color;
            });
        }

        private void SetBatteryText(string text, Brush color)
        {
            _ = RunOnUiAsync(() =>
            {
                if (tbBatteryStatus == null) return;
                if (tbBatteryStatus.Text != text) tbBatteryStatus.Text = text;
                if (!ReferenceEquals(tbBatteryStatus.Foreground, color)) tbBatteryStatus.Foreground = color;
            });
        }

        private void RenderBatteryBle(double? pct, BattStatus? status)
        {
            if (status == BattStatus.Charging) SetBatteryText(pct.HasValue ? $"Pin: Đang sạc ({pct.Value:0}%)" : "Pin: Đang sạc", BrushBlue);
            else if (!pct.HasValue || status == BattStatus.NotPresent) SetBatteryText("Pin: N/A", BrushGray);
            else { var color = pct.Value <= 20 ? BrushRed : (pct.Value >= 80 ? BrushGreen : BrushGray); SetBatteryText($"Pin: {pct.Value:0}%", color); }
        }
        private void RenderBatteryUsb(double? pct, BattStatus? status)
        { SetBatteryText("Pin: Đang sạc", BrushBlue); }

        private async Task UpdateBatteryHeaderAsync()
        {
            var now = DateTimeOffset.Now; if ((now - _lastBatteryUiAt) < BatteryUiInterval) return; _lastBatteryUiAt = now;
            if (tbBatteryStatus == null || currentGamepad == null) { SetBatteryText("", BrushGray); return; }
            if (currentTransport == ConnTransport.Usb) { RenderBatteryUsb(null, BattStatus.Charging); return; }

            var r = await EmbeddedBatteryReader.ReadAsync(currentGamepad, false);
            var percent = r.percent;
            var status = r.status;
            RenderBatteryBle(percent, status);
        }

        private static double ShapeInput(double x)
        {
            const double INPUT_DEADZONE = 0.005, INPUT_EXPO = 1.05;
            if (x <= INPUT_DEADZONE) return 0;
            return Math.Pow(Math.Min(1.0, Math.Max(0.0, x)), INPUT_EXPO);
        }
        private void UpdateTestRumbleFromReading(Gamepad reading) { /* overload placeholder */ }
        private void UpdateTestRumbleFromReading(GamepadReading reading)
        {
            double lt = ShapeInput(reading.LeftTrigger);
            double rt = ShapeInput(reading.RightTrigger);

            if (currentRumbleMode == RumbleMode.Motor)
            { _testLM = lt; _testRM = rt; _testLT = _testRT = 0; }
            else if (currentRumbleMode == RumbleMode.Trigger)
            { _testLT = lt; _testRT = rt; _testLM = _testRM = 0; }
            else
            { _testLM = _testRM = _testLT = _testRT = 0; }
        }

        private void StopRumble()
        {
            var gp = currentGamepad;
            try { if (gp != null) gp.Vibration = new GamepadVibration(); } catch { }
            _lastVibLM = _lastVibRM = _lastVibLT = _lastVibRT = 0;
            _testLM = _testRM = _testLT = _testRT = 0;
            _warnLM_Until = _warnRM_Until = _warnLT_Until = _warnRT_Until = DateTimeOffset.MinValue;
            _warnLM_Force = _warnRM_Force = _warnLT_Force = _warnRT_Force = 0;
            _smLM.Value = _smRM.Value = _smLT.Value = _smRT.Value = 0;
            _smLM.Target = _smRM.Target = _smLT.Target = _smRT.Target = 0;
            _segParts.Clear();
        }

        private async Task<bool> EnsureStateSocketAsync()
        {
            if (IsRunningOnXbox()) return false;
            if (DateTimeOffset.Now < _quickFailUntil) return false;

            if (stateSocket != null && stateWriter != null && stateReader != null) return true;
            try
            {
                stateSocket = new StreamSocket();
                await stateSocket.ConnectAsync(new Windows.Networking.HostName("127.0.0.1"), "12345");
                stateWriter = new DataWriter(stateSocket.OutputStream);
                stateReader = new DataReader(stateSocket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
                return true;
            }
            catch
            {
                DisposeStateSocket();
                _quickFailUntil = DateTimeOffset.Now + TimeSpan.FromSeconds(1.0);
                return false;
            }
        }
        private void DisposeStateSocket()
        {
            try { stateWriter?.DetachStream(); } catch { }
            try { stateReader?.DetachStream(); } catch { }
            try { stateSocket?.Dispose(); } catch { }
            stateWriter = null; stateReader = null; stateSocket = null;
        }
        private async Task<uint> LoadWithTimeoutAsync(DataReader reader, uint count, int timeoutMs)
        {
            using (var cts = new System.Threading.CancellationTokenSource(timeoutMs))
            { try { return await reader.LoadAsync(count).AsTask(cts.Token); } catch (TaskCanceledException) { throw new TimeoutException(); } }
        }
        private async Task<(bool guide, bool share)?> ReadGuideShareQuickAsync(int timeoutMs = 24)
        {
            await _socketLock.WaitAsync();
            try
            {
                if (!await EnsureStateSocketAsync()) return null;
                var writer = stateWriter; var reader = stateReader;
                if (writer == null || reader == null) return null;

                writer.WriteString("get_state_bin");
                await writer.StoreAsync();

                uint l1 = await LoadWithTimeoutAsync(reader, 1, timeoutMs);
                if (l1 == 0) throw new Exception("closed");

                byte count = reader.ReadByte();
                if (count == 0) return (false, false);

                uint lN = await LoadWithTimeoutAsync(reader, count, timeoutMs);
                if (lN < count) throw new Exception("incomplete");

                byte first = reader.ReadByte();
                for (int i = 1; i < count; i++) reader.ReadByte();

                return ((first & 0b10) != 0, (first & 0b01) != 0);
            }
            catch
            {
                DisposeStateSocket();
                _quickFailUntil = DateTimeOffset.Now + TimeSpan.FromSeconds(1.0);
                return null;
            }
            finally { _socketLock.Release(); }
        }

        // ==================== EmbeddedBatteryReader (GỘP) ====================
        internal static class EmbeddedBatteryReader
        {
            public static Task<(double? percent, BattStatus? status)> ReadAsync(Gamepad gp, bool allowUsb)
            {
                try
                {
                    if (gp == null)
                        return Task.FromResult<(double?, BattStatus?)>((null, (BattStatus?)null));

                    if (!gp.IsWireless)  // USB
                        return Task.FromResult<(double?, BattStatus?)>((null, BattStatus.Charging));

                    // BLE: không có % cụ thể → N/A
                    return Task.FromResult<(double?, BattStatus?)>((null, BattStatus.NotPresent));
                }
                catch
                {
                    return Task.FromResult<(double?, BattStatus?)>((null, (BattStatus?)null));
                }
            }
        }

        // ====== >>> Phần cài đặt TÍNH NĂNG VƯỢT NGƯỠNG (mới thêm) ======

        /// <summary>
        /// Kiểm tra tất cả các key có counter; nếu vượt mốc (mỗi 10 lần) thì:
        /// 1) Nháy vàng ô hiển thị trong FLASH_SHORT
        /// 2) Phát rung cảnh báo (motor + trigger) tương ứng bên trái/phải
        /// </summary>
        private void CheckThresholdsAndWarn()
        {
            var now = DateTimeOffset.Now;
            foreach (var kvp in buttonPressCount)
            {
                string key = kvp.Key;
                int count = kvp.Value;

                // Xác định mốc hiện tại (10, 20, 30, ...)
                int seg = (THRESH_STEP > 0) ? (count / THRESH_STEP) : 0;

                if (!_lastSegByKey.TryGetValue(key, out int lastSeg)) lastSeg = 0;

                if (seg > lastSeg) // vừa vượt thêm một mốc
                {
                    _lastSegByKey[key] = seg;
                    OnThresholdHit(key, seg, now);
                }
            }
        }

        /// <summary>
        /// Gọi khi một key vừa vượt mốc. Tạo hiệu ứng nháy màu + rung.
        /// </summary>
        private void OnThresholdHit(string key, int seg, DateTimeOffset now)
        {
            // Nháy màu cho ô đó
            _flashUntil[key] = now + FLASH_SHORT;

            // Cường độ & thời gian rung tăng dần theo mốc
            double force = seg >= 5 ? 1.0 : (seg >= 3 ? 0.8 : WARN_FORCE_MED);
            TimeSpan dur = seg >= 3 ? WARN_DUR_LONG : WARN_DUR_SHORT;

            bool isLeft = LeftSideKeys.Contains(key);
            bool isRight = RightSideKeys.Contains(key);

            // Nếu key thuộc bên trái → ưu tiên motor/trigger trái; bên phải → phải
            if (isLeft)
            {
                _warnLM_Force = Math.Max(_warnLM_Force, force);
                _warnLM_Until = now + dur;

                _warnLT_Force = Math.Max(_warnLT_Force, force * 0.8);
                _warnLT_Until = now + dur;
            }

            if (isRight)
            {
                _warnRM_Force = Math.Max(_warnRM_Force, force);
                _warnRM_Until = now + dur;

                _warnRT_Force = Math.Max(_warnRT_Force, force * 0.8);
                _warnRT_Until = now + dur;
            }

            // Trường hợp đặc biệt: nếu key không thuộc sets (hầu như không xảy ra), rung cả hai bên nhẹ
            if (!isLeft && !isRight)
            {
                _warnLM_Force = Math.Max(_warnLM_Force, force * 0.6);
                _warnRM_Force = Math.Max(_warnRM_Force, force * 0.6);
                _warnLM_Until = now + dur;
                _warnRM_Until = now + dur;
            }
        }

        /// <summary>
        /// Nếu key đang trong thời gian "flash", trả về BrushWarn; ngược lại dùng brush mặc định.
        /// </summary>
        private Brush GetWithFlash(string key, Brush defaultBrush)
        {
            if (_flashUntil.TryGetValue(key, out var until) && DateTimeOffset.Now < until)
                return BrushWarn;
            return defaultBrush;
        }
    }
}
