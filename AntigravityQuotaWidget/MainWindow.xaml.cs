using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using AntigravityQuotaWidget.Models;
using AntigravityQuotaWidget.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AntigravityQuotaWidget;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private readonly AntigravityService _antigravityService;
    private readonly NotificationService _notificationService;
    private readonly QuotaService _quotaService;
    private readonly ConversationMonitorService _conversationMonitorService;

    private NotifyIcon? _notifyIcon;
    private ToolStripMenuItem? _menuTopmost;
    private ToolStripMenuItem? _menuStartup;
    private ToolStripMenuItem? _menuMini;
    private bool _isClosing = false;
    private bool _isDragging = false;

    public MainWindow()
    {
        App.Log("MainWindow constructor started.");
        InitializeComponent();

        _settingsService = new SettingsService();
        _antigravityService = new AntigravityService();

        SetupTrayIcon();

        _notificationService = new NotificationService(_notifyIcon!);
        _quotaService = new QuotaService(_antigravityService, _settingsService, _notificationService);
        _conversationMonitorService = new ConversationMonitorService(_settingsService, _notificationService);

        ApplyInitialWindowPosition();

        Topmost = _settingsService.CurrentSettings.AlwaysOnTop;

        Loaded += (_, _) =>
        {
            ApplyWindowOpacity(_settingsService.CurrentSettings.Opacity);
        };

        MouseEnter += (_, _) =>
        {
            if (!_isSettingsOpen)
            {
                ApplyWindowOpacity(1.0);
            }
        };

        MouseLeave += (_, _) =>
        {
            ApplyWindowOpacity(_settingsService.CurrentSettings.Opacity);
        };

        if (_settingsService.CurrentSettings.IsMiniMode)
        {
            Width = 230;
            Height = 38;
        }
        else
        {
            Width = 320;
            Height = 310;
        }

        _quotaService.OnQuotaUpdated += OnQuotaUpdatedHandler;
        _conversationMonitorService.OnStatusChanged += OnConversationStatusChangedHandler;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        LocationChanged += MainWindow_LocationChanged;
        App.Log($"MainWindow constructor completed. Initial pos: Left={Left}, Top={Top}");
    }

    private bool _isSettingsOpen = false;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const uint LWA_ALPHA = 0x2;

    public void ApplyWindowOpacity(double opacity)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            opacity = Math.Max(0.2, Math.Min(1.0, opacity));

            Opacity = opacity;

            byte alpha = (byte)Math.Round(opacity * 255.0);
            bool ret = SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
            int errAttr = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            App.Log($"[OPACITY] hwnd={hwnd}, opacity={opacity:F2}, alpha={alpha}, ret={ret}, errAttr={errAttr}");
        }
        catch (Exception ex)
        {
            App.Log($"ApplyWindowOpacity error: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern int EmptyWorkingSet(IntPtr hwProc);

    public static void CompactMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        }
        catch { }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_ACRYLIC = 3;

    public void ApplyThemeToWindow(string? theme)
    {
        bool isLight = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase);

        Background = System.Windows.Media.Brushes.Transparent;
        if (rootGrid != null)
        {
            rootGrid.Background = System.Windows.Media.Brushes.Transparent;
        }
        if (webView != null)
        {
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        }

        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int darkMode = isLight ? 0 : 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                int backdrop = DWMSBT_ACRYLIC;
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            }
        }
        catch { }
    }

    public void SetMiniMode(bool isMini)
    {
        var s = _settingsService.CurrentSettings;
        s.IsMiniMode = isMini;
        _settingsService.SaveSettings(s);

        Dispatcher.Invoke(() =>
        {
            if (isMini)
            {
                Width = 230;
                Height = 38;
            }
            else
            {
                Width = 320;
                Height = 310;
            }

            if (_menuMini != null) _menuMini.Checked = isMini;
            ApplyMagneticSnap();
            BroadcastSettings();
        });
    }

    public void ApplyMagneticSnap()
    {
        if (!_settingsService.CurrentSettings.SnapToEdge) return;

        try
        {
            var centerPt = new System.Drawing.Point((int)(Left + Width / 2), (int)(Top + Height / 2));
            var screen = System.Windows.Forms.Screen.FromPoint(centerPt);
            var wa = screen.WorkingArea;

            const double snapThreshold = 18.0;

            if (Math.Abs(Left - wa.Left) < snapThreshold)
            {
                Left = wa.Left;
            }
            else if (Math.Abs((Left + Width) - wa.Right) < snapThreshold)
            {
                Left = wa.Right - Width;
            }

            if (Math.Abs(Top - wa.Top) < snapThreshold)
            {
                Top = wa.Top;
            }
            else if (Math.Abs((Top + Height) - wa.Bottom) < snapThreshold)
            {
                Top = wa.Bottom - Height;
            }

            _settingsService.UpdatePosition(Left, Top);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    public void BringToFront()
    {
        App.Log($"BringToFront called. WindowState={WindowState}, Visibility={Visibility}, Left={Left}, Top={Top}");
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Topmost = true;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        SetForegroundWindow(hwnd);
        Activate();
        Focus();
        App.Log($"BringToFront done. Handle={hwnd}");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_GETMINMAXINFO = 0x0024;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int corner = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref corner, sizeof(int));

            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_LAYERED));
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | 0x0020);
            }
            ApplyWindowOpacity(_settingsService.CurrentSettings.Opacity);

            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            source?.AddHook(WndProc);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCLBUTTONDBLCLK)
        {
            SetMiniMode(!_settingsService.CurrentSettings.IsMiniMode);
            handled = true;
            return IntPtr.Zero;
        }
        else if (msg == WM_GETMINMAXINFO)
        {
            try
            {
                var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMinTrackSize.x = 100;
                mmi.ptMinTrackSize.y = 30;
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
            }
            catch { }
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void SetupTrayIcon()
    {
        var menu = new ContextMenuStrip();

        var menuShowHide = new ToolStripMenuItem("显示 / 隐藏悬浮窗", null, (_, _) => ToggleVisibility());
        var menuPin = new ToolStripMenuItem("📌 固定到右上角", null, (_, _) => PinToTopRight());
        var menuRefresh = new ToolStripMenuItem("🔄 立即刷新", null, async (_, _) =>
        {
            SendToWeb("manualRefreshTriggered", new { });
            await _quotaService.RefreshAsync();
        });

        _menuTopmost = new ToolStripMenuItem("🔝 窗口置顶", null, (_, _) =>
        {
            var s = _settingsService.CurrentSettings;
            s.AlwaysOnTop = !s.AlwaysOnTop;
            _settingsService.SaveSettings(s);
            Topmost = s.AlwaysOnTop;
            _menuTopmost!.Checked = s.AlwaysOnTop;
            BroadcastSettings();
        })
        {
            Checked = _settingsService.CurrentSettings.AlwaysOnTop
        };

        _menuStartup = new ToolStripMenuItem("🚀 开机自启", null, (_, _) =>
        {
            var s = _settingsService.CurrentSettings;
            s.StartWithWindows = !s.StartWithWindows;
            _settingsService.SaveSettings(s);
            _menuStartup!.Checked = s.StartWithWindows;
            BroadcastSettings();
        })
        {
            Checked = _settingsService.CurrentSettings.StartWithWindows
        };

        var menuExit = new ToolStripMenuItem("❌ 退出", null, (_, _) =>
        {
            _isClosing = true;
            _notifyIcon?.Dispose();
            Application.Current.Shutdown();
        });

        _menuMini = new ToolStripMenuItem("💊 迷你胶囊模式", null, (_, _) =>
        {
            SetMiniMode(!_settingsService.CurrentSettings.IsMiniMode);
        })
        {
            Checked = _settingsService.CurrentSettings.IsMiniMode
        };

        menu.Items.Add(menuShowHide);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(menuPin);
        menu.Items.Add(menuRefresh);
        menu.Items.Add(_menuMini);
        menu.Items.Add(_menuTopmost);
        menu.Items.Add(_menuStartup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(menuExit);

        _notifyIcon = new NotifyIcon
        {
            Text = "Antigravity Quota Widget",
            Icon = CreateAppIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => ToggleVisibility();
    }

    private static Icon CreateAppIcon()
    {
        try
        {
            var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(icoPath))
            {
                return new Icon(icoPath);
            }
        }
        catch { }

        return SystemIcons.Application;
    }

    private void ToggleVisibility()
    {
        if (Visibility == Visibility.Visible)
        {
            Hide();
            CompactMemory();
        }
        else
        {
            BringToFront();
        }
    }

    private void ApplyInitialWindowPosition()
    {
        var s = _settingsService.CurrentSettings;

        if (s.WindowX.HasValue && s.WindowY.HasValue)
        {
            double x = s.WindowX.Value;
            double y = s.WindowY.Value;

            var pt = new System.Drawing.Point((int)(x + Width / 2), (int)(y + Height / 2));
            var screen = System.Windows.Forms.Screen.FromPoint(pt);
            var wa = screen.WorkingArea;

            // Validate that the saved position intersects with or is near the screen
            if (x >= wa.Left - 100 && x + Width <= wa.Right + 100 &&
                y >= wa.Top - 100 && y + Height <= wa.Bottom + 100)
            {
                Left = Math.Max(wa.Left, Math.Min(wa.Right - Width, x));
                Top = Math.Max(wa.Top, Math.Min(wa.Bottom - Height, y));
                return;
            }
        }

        // Default: Top-Right corner with 24px margin
        PinToTopRight();
    }

    private void PinToTopRight()
    {
        var centerPt = new System.Drawing.Point((int)(Left + Width / 2), (int)(Top + Height / 2));
        var screen = System.Windows.Forms.Screen.FromPoint(centerPt);
        var wa = screen.WorkingArea;
        Left = wa.Right - Width - 24;
        Top = wa.Top + 24;
        _settingsService.UpdatePosition(Left, Top);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.Log("MainWindow_Loaded started.");
        try
        {
            var udataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AntigravityQuotaWidget", "WebView2"
            );
            App.Log($"WebView2 udataDir: {udataDir}");

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--renderer-process-limit=1 " +
                    "--enable-low-end-device-mode " +
                    "--disable-features=msWebOOUI,msPdfOOUI,Translate,MediaRouter,SpareRendererForSitePerProcess " +
                    "--disable-background-networking " +
                    "--disable-component-update " +
                    "--disable-sync " +
                    "--disable-domain-reliability " +
                    "--disable-speech-api " +
                    "--disable-default-apps"
            };

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: udataDir, options: options);
            await webView.EnsureCoreWebView2Async(env);
            ApplyThemeToWindow(_settingsService.CurrentSettings.Theme);

            var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            if (!Directory.Exists(wwwroot))
            {
                // Fallback to project root if running from bin
                wwwroot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "wwwroot"));
            }
            App.Log($"wwwroot path: {wwwroot}, Exists={Directory.Exists(wwwroot)}");

            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local",
                wwwroot,
                CoreWebView2HostResourceAccessKind.Allow
            );

            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            webView.CoreWebView2.Navigate("http://app.local/index.html");

            App.Log("Starting quotaService and conversationMonitorService...");
            _quotaService.Start();
            _conversationMonitorService.Start();

            BringToFront();
            _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.Invoke(CompactMemory));
            App.Log("MainWindow_Loaded successfully completed.");
        }
        catch (Exception ex)
        {
            App.Log($"[ERROR] MainWindow_Loaded exception: {ex}");
            MessageBox.Show($"初始化 WebView2 失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var rawJson = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("action", out var actionElem))
            {
                var action = actionElem.GetString();
                switch (action)
                {
                    case "drag":
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                                ReleaseCapture();
                                SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                                ApplyMagneticSnap();
                                _settingsService.UpdatePosition(Left, Top);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Drag error: {ex.Message}");
                            }
                        });
                        break;

                    case "moveBy":
                        if (root.TryGetProperty("dx", out var dxElem) && root.TryGetProperty("dy", out var dyElem))
                        {
                            double dx = dxElem.GetDouble();
                            double dy = dyElem.GetDouble();
                            Dispatcher.Invoke(() =>
                            {
                                Left += dx;
                                Top += dy;
                                ApplyMagneticSnap();
                                _settingsService.UpdatePosition(Left, Top);
                            });
                        }
                        break;

                    case "toggleMiniMode":
                        SetMiniMode(!_settingsService.CurrentSettings.IsMiniMode);
                        break;

                    case "setMiniMode":
                        if (root.TryGetProperty("isMini", out var miniElem))
                        {
                            SetMiniMode(miniElem.GetBoolean());
                        }
                        break;

                    case "pinTopRight":
                        Dispatcher.Invoke(PinToTopRight);
                        break;

                    case "closeToTray":
                        Dispatcher.Invoke(Hide);
                        break;

                    case "refresh":
                        _ = _quotaService.RefreshAsync();
                        break;

                    case "ready":
                        BroadcastSettings();
                        if (_quotaService.LastQuota != null)
                        {
                            SendToWeb("quotaUpdate", _quotaService.LastQuota);
                        }
                        SendToWeb("conversationStatusUpdate", _conversationMonitorService.CurrentStatus);
                        break;

                    case "settingsOpened":
                        _isSettingsOpen = true;
                        Dispatcher.Invoke(() => ApplyWindowOpacity(_settingsService.CurrentSettings.Opacity));
                        break;

                    case "settingsClosed":
                        _isSettingsOpen = false;
                        Dispatcher.Invoke(() => ApplyWindowOpacity(_settingsService.CurrentSettings.Opacity));
                        break;

                    case "setOpacity":
                        if (root.TryGetProperty("opacity", out var opElem))
                        {
                            double op = opElem.GetDouble();
                            _settingsService.CurrentSettings.Opacity = op;
                            Dispatcher.Invoke(() => ApplyWindowOpacity(op));
                        }
                        break;

                    case "saveSettings":
                        if (root.TryGetProperty("settings", out var sElem))
                        {
                            var newSettings = JsonSerializer.Deserialize<WidgetSettings>(sElem.GetRawText());
                            if (newSettings != null)
                            {
                                bool miniChanged = newSettings.IsMiniMode != _settingsService.CurrentSettings.IsMiniMode;
                                newSettings.WindowX = Left;
                                newSettings.WindowY = Top;
                                _settingsService.SaveSettings(newSettings);

                                Dispatcher.Invoke(() =>
                                {
                                    Topmost = newSettings.AlwaysOnTop;
                                    ApplyWindowOpacity(newSettings.Opacity);
                                    if (_menuTopmost != null) _menuTopmost.Checked = newSettings.AlwaysOnTop;
                                    if (_menuStartup != null) _menuStartup.Checked = newSettings.StartWithWindows;
                                    if (_menuMini != null) _menuMini.Checked = newSettings.IsMiniMode;
                                    _quotaService.UpdatePollInterval(newSettings.RefreshIntervalMinutes);
                                    ApplyThemeToWindow(newSettings.Theme);
                                    if (miniChanged)
                                    {
                                        SetMiniMode(newSettings.IsMiniMode);
                                    }
                                    _conversationMonitorService.CheckStatus();
                                });
                            }
                        }
                        break;
                }

            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebMessage error: {ex.Message}");
        }
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        if (!_isDragging && IsLoaded)
        {
            _settingsService.UpdatePosition(Left, Top);
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosing)
        {
            e.Cancel = true;
            Hide();
            CompactMemory();
        }
        else
        {
            _conversationMonitorService.Stop();
        }
    }

    private void OnConversationStatusChangedHandler(ConversationStatusPayload status)
    {
        Dispatcher.Invoke(() =>
        {
            SendToWeb("conversationStatusUpdate", status);
        });
    }

    private void OnQuotaUpdatedHandler(QuotaPayload payload)
    {
        Dispatcher.Invoke(() =>
        {
            SendToWeb("quotaUpdate", payload);
            CompactMemory();
        });
    }

    private void BroadcastSettings()
    {
        SendToWeb("settings", _settingsService.CurrentSettings);
    }

    private void SendToWeb(string type, object payload)
    {
        if (webView.CoreWebView2 == null) return;
        var msg = JsonSerializer.Serialize(new { type, payload });
        webView.CoreWebView2.PostWebMessageAsJson(msg);
    }
}