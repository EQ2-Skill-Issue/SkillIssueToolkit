using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SkillIssueToolkit.Overlay
{
    public partial class MainWindow : Window
    {
        // Process name for the EQ2 client (EverQuest2.exe), used to detect when the game
        // itself is the foreground window.
        private const string GameProcessName = "EverQuest2";

        // Taken from the plugin's own .csproj Reference HintPath ("Advanced Combat
        // Tracker.exe"); Windows process names are the exe filename minus extension.
        private const string ActProcessName = "Advanced Combat Tracker";

        // Which overlay page this instance shows, and which settings file it persists to.
        private readonly string _overlayUrl;
        private readonly string _settingsKey;

        private DispatcherTimer? _focusTimer;
        private IntPtr _selfHwnd;
        private IntPtr _cachedGameHwnd = IntPtr.Zero;
        private bool _lockToWindow;
        private double _lockOffsetX;
        private double _lockOffsetY;
        private bool _clickThrough;
        private OverlaySettings _settings = new();
        private DateTime _settingsFileLastWriteUtc;
        private bool _allowDragging;
        private double _dragHandleHeight = 12;
        private bool _hideWhenUnfocused = false;
        private double _zoomFactor = 1.0;
        private bool _showPreview;

        // Cached from the last contentSize message - lets ApplyAllowDragging recompute Height
        // immediately when the grip row is toggled.
        private double _lastReportedContentWidth = 480;
        private double _lastReportedContentHeight = MinContentHeight;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_ALT = 0x0001;

        private const int HOTKEY_CLICKTHROUGH = 9001;
        private const int HOTKEY_ZOOM_IN = 9002;
        private const int HOTKEY_ZOOM_OUT = 9003;
        private const int HOTKEY_ZOOM_RESET = 9004;
        private const uint VK_L = 0x4C;
        private const uint VK_OEM_PLUS = 0xBB;  // the "=/+" key
        private const uint VK_OEM_MINUS = 0xBD; // the "-/_" key
        private const uint VK_0 = 0x30;

        // settingsKey "" (or null) preserves the unkeyed settings.json path used by the DPS
        // overlay; a second instance should pass a distinct key (e.g. "triggers") for its own
        // file. fallbackLeft/fallbackTop/fallbackAllowDragging/fallbackClickThrough only apply
        // when no settings file exists yet, letting each overlay default differently (e.g.
        // triggers defaults click-through with no grip strip, as a passive display).
        public MainWindow(string overlayUrl, string settingsKey, double fallbackLeft = 40, double fallbackTop = 40,
            bool fallbackAllowDragging = true, bool fallbackClickThrough = false)
        {
            _overlayUrl = overlayUrl;
            _settingsKey = settingsKey ?? "";

            InitializeComponent();

            // WebView2 defaults to an opaque white background - only fully-opaque or
            // fully-transparent is supported (no semi-transparent), which is fine since our
            // HTML page draws its own semi-transparent panel background via CSS on top of this.
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            webView.Source = new Uri(_overlayUrl);

            var settingsFileExisted = File.Exists(OverlaySettings.SettingsPath(_settingsKey));
            _settings = OverlaySettings.Load(_settingsKey);
            if (!settingsFileExisted)
            {
                _settings.AllowDragging = fallbackAllowDragging;
                _settings.ClickThrough = fallbackClickThrough;
            }
            Left = settingsFileExisted ? _settings.Left : fallbackLeft;
            Top = settingsFileExisted ? _settings.Top : fallbackTop;
            Title = string.IsNullOrEmpty(_settingsKey) ? "EQ2 DPS Overlay" : $"EQ2 {_settingsKey} Overlay";
            RecordSettingsFileWriteTime();
        }

        private void RecordSettingsFileWriteTime()
        {
            try
            {
                var path = OverlaySettings.SettingsPath(_settingsKey);
                _settingsFileLastWriteUtc = File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : DateTime.MinValue;
            }
            catch
            {
                // best-effort - worst case we just re-check next tick
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _selfHwnd = new WindowInteropHelper(this).Handle;

            // Global hotkeys, registered regardless of which overlay window has focus.
            // Ctrl+Alt+ instead of plain Ctrl+/- since WebView2 already intercepts those for
            // its own zoom.
            RegisterHotKey(_selfHwnd, HOTKEY_CLICKTHROUGH, MOD_CONTROL | MOD_ALT, VK_L);
            RegisterHotKey(_selfHwnd, HOTKEY_ZOOM_IN, MOD_CONTROL | MOD_ALT, VK_OEM_PLUS);
            RegisterHotKey(_selfHwnd, HOTKEY_ZOOM_OUT, MOD_CONTROL | MOD_ALT, VK_OEM_MINUS);
            RegisterHotKey(_selfHwnd, HOTKEY_ZOOM_RESET, MOD_CONTROL | MOD_ALT, VK_0);
            var source = HwndSource.FromHwnd(_selfHwnd);
            source?.AddHook(WndProc);

            StartFocusWatcher();
            RestoreSettings();

            // Each overlay page reports its own rendered content size on every data update
            // (see reportContentSize() in common.js). WebView2's bounds otherwise claim mouse
            // input across the whole rectangle even where nothing renders, blocking clicks on
            // the game underneath in that dead space - sizing to match content removes it.
            await webView.EnsureCoreWebView2Async();
            webView.CoreWebView2.WebMessageReceived += (s, args) =>
            {
                ApplyReportedContentSize(args.TryGetWebMessageAsString());
            };

            // Zoom needs CoreWebView2 to exist (applied via ExecuteScriptAsync), so it's
            // restored here instead of in RestoreSettings(), which runs earlier.
            if (_settings.ZoomFactor > 0 && Math.Abs(_settings.ZoomFactor - 1.0) > 0.001)
            {
                await ApplyZoom(_settings.ZoomFactor);
            }

            if (_settings.ShowPreview)
            {
                await ApplyPreviewMode(true);
            }
        }

        private const double MinContentHeight = 40;
        private const double MaxContentHeight = 320; // shared ceiling for either overlay page
        private const double MinContentWidth = 150;
        private const double MaxContentWidth = 1200; // generous, for higher zoom levels

        private void ApplyReportedContentSize(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "contentSize") return;
                if (!root.TryGetProperty("width", out var widthProp)) return;
                if (!root.TryGetProperty("height", out var heightProp)) return;

                _lastReportedContentWidth = widthProp.GetDouble();
                _lastReportedContentHeight = heightProp.GetDouble();
                ApplyWindowSizeFromCache();
            }
            catch
            {
                // malformed or unrelated message - ignore rather than crash the app over it
            }
        }

        // Applies Width/Height from the last-known reported content size, adding the grip
        // row's height when enabled. Separated from ApplyReportedContentSize so
        // ApplyAllowDragging can reapply sizing immediately on toggle.
        private void ApplyWindowSizeFromCache()
        {
            Width = Math.Max(MinContentWidth, Math.Min(MaxContentWidth, _lastReportedContentWidth));
            var clampedHeight = Math.Max(MinContentHeight, Math.Min(MaxContentHeight, _lastReportedContentHeight));
            Height = clampedHeight + (_allowDragging ? _dragHandleHeight : 0);
        }

        // Injects the zoom factor via ExecuteScriptAsync. CSS zoom reflows layout (unlike
        // transform: scale), so the ResizeObserver in common.js picks up the size change and
        // reports it back through the normal contentSize path - no extra plumbing needed.
        private async System.Threading.Tasks.Task ApplyZoom(double factor)
        {
            _zoomFactor = factor;
            if (webView.CoreWebView2 == null) return;
            try
            {
                await webView.CoreWebView2.ExecuteScriptAsync($"window.setOverlayZoom({factor.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            }
            catch
            {
                // page may not have setOverlayZoom yet (still loading) - best-effort
            }
        }

        private const double ZoomStep = 0.1;
        private const double MinZoom = 0.5;
        private const double MaxZoom = 3.0;

        // Forces the overlay page to show placeholder content even when nothing's running,
        // so it can be seen and grabbed for positioning - an empty triggers.html/timers.html
        // is otherwise invisible by design. dps-meter.html has no setPreviewMode function at
        // all (it's never empty), so calling this there is a harmless no-op via the try/catch
        // below.
        private async System.Threading.Tasks.Task ApplyPreviewMode(bool enabled)
        {
            _showPreview = enabled;
            if (webView.CoreWebView2 == null) return;
            try
            {
                await webView.CoreWebView2.ExecuteScriptAsync($"if (window.setPreviewMode) window.setPreviewMode({(enabled ? "true" : "false")})");
            }
            catch
            {
                // page may not have loaded yet - best-effort
            }
        }

        private async void ZoomIn()
        {
            await ApplyZoom(Math.Min(MaxZoom, _zoomFactor + ZoomStep));
            SaveSettings();
        }

        private async void ZoomOut()
        {
            await ApplyZoom(Math.Max(MinZoom, _zoomFactor - ZoomStep));
            SaveSettings();
        }

        private async void ZoomReset()
        {
            await ApplyZoom(1.0);
            SaveSettings();
        }

        // Toggles whether dragging is possible at all - off means no grip strip; on shows a
        // small transparent strip (still hit-tests, renders nothing) in its own reserved row.
        private void ApplyAllowDragging(bool enabled)
        {
            _allowDragging = enabled;
            GripRow.Height = new GridLength(enabled ? _dragHandleHeight : 0);
            // Reapply sizing immediately rather than waiting for the next content report, so
            // toggling while idle doesn't leave a stale height.
            ApplyWindowSizeFromCache();
        }

        // Applies a changed grip height live - only visible while dragging is enabled, but
        // the value is always stored so it's ready when AllowDragging next turns on.
        private void ApplyDragHandleHeight(double height)
        {
            _dragHandleHeight = height;
            if (_allowDragging)
            {
                GripRow.Height = new GridLength(height);
                ApplyWindowSizeFromCache();
            }
        }

        // Lock-to-window state, applied both from settings and from a user toggle.
        // recalculateOffset distinguishes a fresh, user-initiated toggle (recompute from
        // current position) from restoring an already-known offset from settings (false).
        private void ApplyLockToWindow(bool enabled, bool recalculateOffset)
        {
            _lockToWindow = enabled;
            if (enabled && recalculateOffset) RecalculateLockOffset();
        }

        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove(); // blocks until the drag ends
            if (_lockToWindow) RecalculateLockOffset();
            SaveSettings();
        }

        private void RestoreSettings()
        {
            if (_settings.LockToWindow)
            {
                _lockOffsetX = _settings.LockOffsetX;
                _lockOffsetY = _settings.LockOffsetY;
                ApplyLockToWindow(true, recalculateOffset: false); // offset already known, don't recompute it
            }

            if (_settings.ClickThrough)
            {
                SetClickThrough(true);
            }

            _dragHandleHeight = _settings.DragHandleHeight > 0 ? _settings.DragHandleHeight : 12;
            ApplyAllowDragging(_settings.AllowDragging);
            _hideWhenUnfocused = _settings.HideWhenUnfocused;
            // Zoom is NOT restored here - see Window_Loaded, it needs CoreWebView2 to
            // exist first, which isn't guaranteed yet at this point.
        }

        private void SaveSettings()
        {
            // Enabled is owned by the ACT plugin's settings checkbox, not tracked here - it's
            // only ever set once at construction, so re-read the current on-disk value first
            // to avoid clobbering it.
            _settings.Enabled = OverlaySettings.Load(_settingsKey).Enabled;

            _settings.Left = Left;
            _settings.Top = Top;
            _settings.LockToWindow = _lockToWindow;
            _settings.LockOffsetX = _lockOffsetX;
            _settings.LockOffsetY = _lockOffsetY;
            _settings.ClickThrough = _clickThrough;
            _settings.AllowDragging = _allowDragging;
            _settings.ZoomFactor = _zoomFactor;
            _settings.ShowPreview = _showPreview;
            _settings.HideWhenUnfocused = _hideWhenUnfocused;
            _settings.DragHandleHeight = _dragHandleHeight;
            _settings.Save(_settingsKey);
            RecordSettingsFileWriteTime();
        }

        // Detects settings file changes made by another process (the ACT plugin's settings UI
        // edits this same file) and applies them live.
        private async void CheckForExternalSettingsChanges()
        {
            DateTime currentWriteUtc;
            try
            {
                var path = OverlaySettings.SettingsPath(_settingsKey);
                if (!File.Exists(path)) return;
                currentWriteUtc = File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return;
            }

            if (currentWriteUtc <= _settingsFileLastWriteUtc) return;

            var loaded = OverlaySettings.Load(_settingsKey);
            _settingsFileLastWriteUtc = currentWriteUtc;

            // Only handles the disable direction here - closing when Enabled turns false.
            // Re-enabling is handled by App.xaml.cs, which continuously reconciles enabled
            // overlays against open windows and constructs a new one when needed.
            if (!loaded.Enabled)
            {
                Close();
                return;
            }

            if (loaded.ClickThrough != _clickThrough)
            {
                SetClickThrough(loaded.ClickThrough);
            }

            if (loaded.LockToWindow != _lockToWindow)
            {
                ApplyLockToWindow(loaded.LockToWindow, recalculateOffset: true);
            }

            if (loaded.AllowDragging != _allowDragging)
            {
                ApplyAllowDragging(loaded.AllowDragging);
            }

            if (Math.Abs(loaded.ZoomFactor - _zoomFactor) > 0.001 && loaded.ZoomFactor > 0)
            {
                await ApplyZoom(loaded.ZoomFactor);
            }

            if (loaded.ShowPreview != _showPreview)
            {
                await ApplyPreviewMode(loaded.ShowPreview);
            }

            if (loaded.HideWhenUnfocused != _hideWhenUnfocused)
            {
                _hideWhenUnfocused = loaded.HideWhenUnfocused;
                if (!_hideWhenUnfocused) Visibility = Visibility.Visible;
            }

            if (Math.Abs(loaded.DragHandleHeight - _dragHandleHeight) > 0.5 && loaded.DragHandleHeight > 0)
            {
                ApplyDragHandleHeight(loaded.DragHandleHeight);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                var id = wParam.ToInt32();
                if (id == HOTKEY_CLICKTHROUGH)
                {
                    SetClickThrough(!_clickThrough);
                    SaveSettings();
                    handled = true;
                }
                else if (id == HOTKEY_ZOOM_IN)
                {
                    ZoomIn();
                    handled = true;
                }
                else if (id == HOTKEY_ZOOM_OUT)
                {
                    ZoomOut();
                    handled = true;
                }
                else if (id == HOTKEY_ZOOM_RESET)
                {
                    ZoomReset();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        // Mirrors OverlayPlugin's auto-hide-when-game-unfocused behavior - otherwise
        // Topmost="True" keeps the panel floating over every window, not just the game.
        // Also checks auto-exit-on-ACT-close on this same timer, which catches a crash too
        // since it doesn't depend on ACT calling DeInitPlugin.
        private void StartFocusWatcher()
        {
            _focusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _focusTimer.Tick += (_, _) =>
            {
                if (Process.GetProcessesByName(ActProcessName).Length == 0)
                {
                    Application.Current.Shutdown();
                    return;
                }

                UpdateVisibilityForForegroundWindow();
                if (_lockToWindow) TrackGameWindowPosition();
                CheckForExternalSettingsChanges();
            };
            _focusTimer.Start();
        }

        // Caches the EQ2 window handle instead of re-enumerating processes every tick;
        // re-validates with IsWindow() in case EQ2 was closed and relaunched.
        private IntPtr GetGameWindowHandle()
        {
            if (_cachedGameHwnd != IntPtr.Zero && IsWindow(_cachedGameHwnd))
                return _cachedGameHwnd;

            foreach (var proc in Process.GetProcessesByName(GameProcessName))
            {
                using (proc)
                {
                    if (proc.MainWindowHandle != IntPtr.Zero)
                    {
                        _cachedGameHwnd = proc.MainWindowHandle;
                        return _cachedGameHwnd;
                    }
                }
            }

            _cachedGameHwnd = IntPtr.Zero;
            return IntPtr.Zero;
        }

        // Fixes the overlay's current screen position relative to the game window's
        // top-left corner - called when "Lock to EQ2 window" is turned on, and again after
        // any manual drag while locked, so dragging while locked doesn't get fought by the
        // next tracking tick.
        private void RecalculateLockOffset()
        {
            var gameHwnd = GetGameWindowHandle();
            if (gameHwnd == IntPtr.Zero || !GetWindowRect(gameHwnd, out var rect)) return;

            _lockOffsetX = Left - rect.Left;
            _lockOffsetY = Top - rect.Top;
        }

        private void TrackGameWindowPosition()
        {
            var gameHwnd = GetGameWindowHandle();
            if (gameHwnd == IntPtr.Zero) return;

            // A minimized window's rect is not meaningful (often garbage/negative) -
            // leave the overlay where it is rather than flying it off-screen.
            if (IsIconic(gameHwnd)) return;

            if (!GetWindowRect(gameHwnd, out var rect)) return;

            Left = rect.Left + _lockOffsetX;
            Top = rect.Top + _lockOffsetY;
        }

        private void SetClickThrough(bool enabled)
        {
            _clickThrough = enabled;
            var exStyle = GetWindowLong(_selfHwnd, GWL_EXSTYLE);
            SetWindowLong(_selfHwnd, GWL_EXSTYLE,
                enabled ? exStyle | WS_EX_TRANSPARENT : exStyle & ~WS_EX_TRANSPARENT);
        }

        private void UpdateVisibilityForForegroundWindow()
        {
            if (!_hideWhenUnfocused)
            {
                Visibility = Visibility.Visible;
                return;
            }

            var foreground = GetForegroundWindow();

            if (foreground == _selfHwnd)
            {
                // The overlay itself is focused (e.g. dragging it) - stay visible rather
                // than hiding just because you clicked on it.
                Visibility = Visibility.Visible;
                return;
            }

            var isGameActive = false;
            GetWindowThreadProcessId(foreground, out var processId);
            try
            {
                using var proc = Process.GetProcessById((int)processId);
                isGameActive = string.Equals(proc.ProcessName, GameProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                // process may have exited between GetForegroundWindow and GetProcessById -
                // treat as "not the game" rather than crashing the timer tick.
            }

            Visibility = isGameActive ? Visibility.Visible : Visibility.Hidden;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            UnregisterHotKey(_selfHwnd, HOTKEY_CLICKTHROUGH);
            UnregisterHotKey(_selfHwnd, HOTKEY_ZOOM_IN);
            UnregisterHotKey(_selfHwnd, HOTKEY_ZOOM_OUT);
            UnregisterHotKey(_selfHwnd, HOTKEY_ZOOM_RESET);
            SaveSettings();

            // Doesn't shut down the process even if this was the last open window -
            // App.xaml.cs's ReconcileOverlays keeps ticking (ShutdownMode is
            // OnExplicitShutdown), so a disabled overlay can come back later without a
            // relaunch. The process exits via the ACT-process-exit check in
            // StartFocusWatcher, or the settings tab's "Close Overlay" button.
        }
    }
}