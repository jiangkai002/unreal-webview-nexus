using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DigitalTwin.Host.Configuration;
using DigitalTwin.Host.Interop;
using DigitalTwin.Host.Protocol;
using DigitalTwin.Host.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DigitalTwin.Host.Windows;

public partial class MainWindow : Window
{
    private static readonly BridgeConnectionInfo EditorDebugConnection = new(
        52317,
        "remviewer-editor-debug-token-v1",
        Guid.Parse("8f5d9a44-469e-4f9a-bc43-ff719644a501"));

    private readonly ClientOptions _options;
    private readonly LaunchOptions _launch;
    private readonly UnrealProcessManager _unrealProcessManager = new();
    private readonly HitRegionService _hitRegionService;
    private readonly CancellationTokenSource _shutdown = new();
    private OverlayWindowManager? _overlayWindowManager;
    private BridgeWebSocketServer? _bridgeServer;
    private BridgeMessageRouter? _bridgeRouter;
    private BridgeConnectionInfo? _bridgeConnection;
    private DispatcherTimer? _editorWindowMonitor;
    private IntPtr _editorStandaloneHwnd;
    private Rect _editorWaitingBounds;
    private WindowState _editorWaitingWindowState;
    private IReadOnlyList<WebHitRegion> _lastWebRegions = [];
    private bool _hasWebRegionSnapshot;
    private bool _webPointerCaptured;
    private bool _webViewInitialized;
    private bool _overlayAttached;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    public MainWindow(ClientOptions options, LaunchOptions launch)
    {
        _options = options;
        _launch = launch;

        InitializeComponent();
        _hitRegionService = new HitRegionService(HitRegionBacking);

        var useUnrealOverlay = options.Unreal.Enabled;
        var startsHiddenForPackagedUnreal = useUnrealOverlay && !options.Unreal.EditorDebugMode;
        Title = options.Window.Title;
        Width = startsHiddenForPackagedUnreal ? 1 : Math.Max(640, options.Window.Width);
        Height = startsHiddenForPackagedUnreal ? 1 : Math.Max(480, options.Window.Height);
        WindowState = startsHiddenForPackagedUnreal || options.Unreal.EditorDebugMode
            ? WindowState.Normal
            : options.Window.StartMaximized ? WindowState.Maximized : WindowState.Normal;
        ShowInTaskbar = !startsHiddenForPackagedUnreal;
        Opacity = startsHiddenForPackagedUnreal ? 0 : 1;
        ControlBar.Visibility = useUnrealOverlay && !options.Unreal.EditorDebugMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        DevToolsButton.Visibility = options.Web.EnableDevTools
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!useUnrealOverlay)
        {
            AllowsTransparency = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            ShowActivated = true;
        }
        else if (options.Unreal.EditorDebugMode)
        {
            ShowActivated = true;
            SetEditorWaitingPresentation(isWaiting: true);
        }

        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeClientAsync();
    }

    private async Task InitializeClientAsync()
    {
        RetryButton.Visibility = Visibility.Collapsed;

        if (_launch.StartUri is null)
        {
            Opacity = 1;
            ShowError("无法打开网页", _launch.ErrorMessage ?? "未提供有效的网址。", canRetry: false);
            return;
        }

        try
        {
            await InitializeWebViewAsync();

            if (_options.Unreal.Enabled)
            {
                ShowLoading("正在启动本机通信桥…");
                await InitializeBridgeAsync();
            }

            // Navigation and Unreal startup are independent. Starting the web request
            // here lets its network/JavaScript work overlap the native process startup.
            WebView.Source = _launch.StartUri;

            if (_options.Unreal.Enabled && _options.Unreal.EditorDebugMode)
            {
                StartEditorWindowMonitoring();
            }
            else if (_options.Unreal.Enabled)
            {
                ShowLoading("正在启动 Unreal…");
                var configuredPath = _launch.UnrealExecutablePath ?? _options.Unreal.ExecutablePath;
                var executablePath = ResolveExecutablePath(configuredPath);
                var unrealHwnd = await _unrealProcessManager.StartAsync(
                    executablePath,
                    _options.Unreal,
                    _bridgeConnection,
                    _shutdown.Token);

                EnsureOverlayWindowManager();
                _overlayWindowManager!.Attach(unrealHwnd);
                _overlayAttached = true;
                FullScreenButton.IsEnabled = true;
                if (_options.Web.EnableAutomaticHitRegions && _hasWebRegionSnapshot)
                {
                    ApplyCurrentRegions();
                }
                else
                {
                    _hitRegionService.ApplyFull(this);
                }

                Opacity = 1;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Opacity = 1;
            ShowError(
                "缺少 WebView2 Runtime",
                "请安装 Microsoft Edge WebView2 Evergreen Runtime 后重试。",
                canRetry: true);
        }
        catch (Exception exception)
        {
            Opacity = 1;
            ShowError("客户端启动失败", exception.Message, canRetry: false);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webViewInitialized)
        {
            return;
        }

        ShowLoading($"正在初始化 WebView2：{_launch.StartUri!.Host}…");
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DigitalTwinClient",
            "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);

        await WebView.EnsureCoreWebView2Async(environment);
        ConfigureWebView(WebView.CoreWebView2);
        if (_options.Web.EnableAutomaticHitRegions)
        {
            await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                HitRegionScript.CreateSource(_options.Web.TransparentBackground));
        }

        _webViewInitialized = true;
        DevToolsButton.IsEnabled = _options.Web.EnableDevTools;
    }

    private async Task InitializeBridgeAsync()
    {
        if (_bridgeConnection is not null)
        {
            return;
        }

        var server = new BridgeWebSocketServer();
        try
        {
            var fixedConnection = _options.Unreal.EditorDebugMode
                ? EditorDebugConnection
                : null;
            _bridgeConnection = await server.StartAsync(_shutdown.Token, fixedConnection);
            _bridgeServer = server;
            _bridgeRouter = new BridgeMessageRouter(server, PostBridgeMessageToWebAsync);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    private void ConfigureWebView(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = _options.Web.EnableDevTools;
        core.Settings.AreDefaultContextMenusEnabled = _options.Web.EnableDefaultContextMenus;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = true;
        WebView.ZoomFactor = 1.0;

        core.NavigationStarting += Core_OnNavigationStarting;
        core.NavigationCompleted += Core_OnNavigationCompleted;
        core.NewWindowRequested += Core_OnNewWindowRequested;
        core.ProcessFailed += Core_OnProcessFailed;
        core.DocumentTitleChanged += Core_OnDocumentTitleChanged;
        core.WebMessageReceived += Core_OnWebMessageReceived;
    }

    private void Core_OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsAllowedWebUri(e.Uri))
        {
            e.Cancel = true;
            ShowError("已阻止不安全的导航", $"仅允许打开 http:// 或 https:// 地址：\n{e.Uri}", canRetry: false);
            return;
        }

        _lastWebRegions = [];
        _hasWebRegionSnapshot = false;
        _webPointerCaptured = false;

        if (_overlayAttached)
        {
            _hitRegionService.ApplyFull(this);
        }

        ShowLoading("网页加载中…");
    }

    private void Core_OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            ApplyCurrentRegions();
            return;
        }

        ShowError(
            "网页加载失败",
            $"无法加载 {_launch.StartUri}。\n错误：{e.WebErrorStatus}",
            canRetry: true);
    }

    private async void Core_OnWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var type = typeElement.GetString() ?? string.Empty;
            if (type == "host.hitRegionsChanged")
            {
                if (_options.Web.EnableAutomaticHitRegions)
                {
                    HandleHitRegionMessage(root);
                }

                return;
            }

            if (type == "host.pointerCaptureChanged")
            {
                HandlePointerCaptureMessage(root);
                return;
            }

            if (type == "host.cameraConsumed")
            {
                if (_bridgeRouter is not null)
                {
                    await _bridgeRouter.AcknowledgeCameraMessageAsync();
                }
                return;
            }

            if (type.StartsWith("host.", StringComparison.Ordinal))
            {
                return;
            }

            if (_bridgeRouter is not null)
            {
                await _bridgeRouter.RouteWebMessageAsync(json, _shutdown.Token);
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages from an untrusted page.
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
    }

    private void HandleHitRegionMessage(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("regions", out var regionElements) ||
            regionElements.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var regions = new List<WebHitRegion>();
        foreach (var item in regionElements.EnumerateArray())
        {
            if (TryReadRegion(item, out var region))
            {
                regions.Add(region);
            }
        }

        _lastWebRegions = regions;
        _hasWebRegionSnapshot = true;
        ApplyCurrentRegions();
    }

    private void HandlePointerCaptureMessage(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("captured", out var capturedElement) ||
            capturedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }

        _webPointerCaptured = capturedElement.GetBoolean();
        ApplyCurrentRegions();
    }

    private async Task PostBridgeMessageToWebAsync(string json)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (_webViewInitialized && !_shutdown.IsCancellationRequested)
            {
                WebView.CoreWebView2.PostWebMessageAsJson(json);
            }
        });
    }

    private static bool TryReadRegion(JsonElement element, out WebHitRegion region)
    {
        region = default;
        if (!element.TryGetProperty("x", out var x) ||
            !element.TryGetProperty("y", out var y) ||
            !element.TryGetProperty("width", out var width) ||
            !element.TryGetProperty("height", out var height) ||
            !x.TryGetDouble(out var xValue) ||
            !y.TryGetDouble(out var yValue) ||
            !width.TryGetDouble(out var widthValue) ||
            !height.TryGetDouble(out var heightValue) ||
            !double.IsFinite(xValue) ||
            !double.IsFinite(yValue) ||
            !double.IsFinite(widthValue) ||
            !double.IsFinite(heightValue))
        {
            return false;
        }

        region = new WebHitRegion(xValue, yValue, widthValue, heightValue);
        return true;
    }

    private void Core_OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsAllowedWebUri(e.Uri))
        {
            WebView.CoreWebView2.Navigate(e.Uri);
        }
    }

    private void Core_OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Dispatcher.Invoke(() => ShowError(
            "网页渲染进程异常",
            $"WebView2 进程发生错误：{e.ProcessFailedKind}",
            canRetry: true));
    }

    private void Core_OnDocumentTitleChanged(object? sender, object e)
    {
        var documentTitle = WebView.CoreWebView2.DocumentTitle;
        Title = string.IsNullOrWhiteSpace(documentTitle)
            ? _options.Window.Title
            : $"{documentTitle} - {_options.Window.Title}";
    }

    private static bool IsAllowedWebUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string ResolveExecutablePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    private void ShowLoading(string message)
    {
        StatusTitle.Text = "正在加载";
        StatusMessage.Text = message;
        RetryButton.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void ShowError(string title, string message, bool canRetry)
    {
        StatusTitle.Text = title;
        StatusMessage.Text = message;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        if (_overlayAttached)
        {
            _hitRegionService.ApplyFull(this);
        }
    }

    private async void RetryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized)
        {
            WebView.CoreWebView2.Reload();
            return;
        }

        await InitializeClientAsync();
    }

    private void FullScreenButton_OnClick(object sender, RoutedEventArgs e)
    {
        _overlayWindowManager?.ToggleFullScreen();
    }

    private void DevToolsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized && _options.Web.EnableDevTools)
        {
            WebView.CoreWebView2.OpenDevToolsWindow();
        }
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized)
        {
            WebView.CoreWebView2.Reload();
        }
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_overlayAttached)
        {
            _overlayWindowManager?.Minimize();
        }
        else
        {
            WindowState = WindowState.Minimized;
        }
    }

    private void ExitButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyCurrentRegions()
    {
        if (!_overlayAttached)
        {
            return;
        }

        if (_webPointerCaptured)
        {
            _hitRegionService.ApplyFull(this);
            return;
        }

        if (!_options.Web.EnableAutomaticHitRegions || !_hasWebRegionSnapshot)
        {
            _hitRegionService.ApplyFull(this);
            return;
        }

        var regions = new List<WebHitRegion>(_lastWebRegions);
        if (StatusPanel.Visibility == Visibility.Visible &&
            StatusPanel.ActualWidth > 0 &&
            StatusPanel.ActualHeight > 0)
        {
            var topLeft = StatusPanel.TranslatePoint(new System.Windows.Point(0, 0), this);
            regions.Add(new WebHitRegion(
                topLeft.X,
                topLeft.Y,
                StatusPanel.ActualWidth,
                StatusPanel.ActualHeight));
        }

        if (ControlBar.Visibility == Visibility.Visible &&
            ControlBar.ActualWidth > 0 &&
            ControlBar.ActualHeight > 0)
        {
            var topLeft = ControlBar.TranslatePoint(new System.Windows.Point(0, 0), this);
            regions.Add(new WebHitRegion(
                topLeft.X,
                topLeft.Y,
                ControlBar.ActualWidth,
                ControlBar.ActualHeight));
        }

        _hitRegionService.ApplyRegions(this, regions);
    }

    private async void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        Hide();

        try
        {
            await ShutdownAsync();
        }
        finally
        {
            _shutdownCompleted = true;
            Close();
        }
    }

    private async Task ShutdownAsync()
    {
        _shutdown.Cancel();
        _bridgeRouter?.Dispose();
        _editorWindowMonitor?.Stop();
        _editorWindowMonitor = null;

        if (_overlayWindowManager is not null)
        {
            _overlayWindowManager.UnrealWindowClosed -= OverlayWindowManager_OnUnrealWindowClosed;
            _overlayWindowManager.FullScreenChanged -= OverlayWindowManager_OnFullScreenChanged;
            if (_options.Unreal.EditorDebugMode)
            {
                _overlayWindowManager.CloseUnrealWindow();
            }
        }
        _overlayWindowManager?.Dispose();
        WebView.Dispose();

        if (_bridgeServer is not null)
        {
            await _bridgeServer.DisposeAsync();
        }

        await Task.Run(_unrealProcessManager.Dispose);
        _shutdown.Dispose();
    }

    private void OverlayWindowManager_OnUnrealWindowClosed(object? sender, EventArgs e)
    {
        if (!_options.Unreal.EditorDebugMode)
        {
            Close();
            return;
        }

        DetachEditorStandaloneWindow();
    }

    private void OverlayWindowManager_OnFullScreenChanged(object? sender, EventArgs e)
    {
        FullScreenButton.Content = _overlayWindowManager?.IsFullScreen == true ? "窗口化" : "全屏";
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ApplyCurrentRegions);
    }

    private void EnsureOverlayWindowManager()
    {
        if (_overlayWindowManager is not null)
        {
            return;
        }

        _overlayWindowManager = new OverlayWindowManager(this);
        _overlayWindowManager.UnrealWindowClosed += OverlayWindowManager_OnUnrealWindowClosed;
        _overlayWindowManager.FullScreenChanged += OverlayWindowManager_OnFullScreenChanged;
    }

    private void StartEditorWindowMonitoring()
    {
        if (_editorWindowMonitor is not null)
        {
            return;
        }

        _editorWindowMonitor = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _editorWindowMonitor.Tick += EditorWindowMonitor_OnTick;
        _editorWindowMonitor.Start();
        FindAndAttachEditorStandaloneWindow();
    }

    private void EditorWindowMonitor_OnTick(object? sender, EventArgs e)
    {
        if (_editorStandaloneHwnd == IntPtr.Zero)
        {
            FindAndAttachEditorStandaloneWindow();
        }
    }

    private void FindAndAttachEditorStandaloneWindow()
    {
        var hwnd = WindowEnumerator.FindUnrealStandaloneWindow(
            _options.Unreal.EditorStandaloneWindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        _editorWaitingBounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        _editorWaitingWindowState = WindowState;
        _editorStandaloneHwnd = hwnd;

        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        SetEditorWaitingPresentation(isWaiting: false);
        EnsureOverlayWindowManager();
        // Do not make the overlay an owned window in Editor mode. Windows destroys
        // owned windows with their owner, while Standalone Game is expected to be
        // closed and relaunched repeatedly during one WPF debugging session.
        _overlayWindowManager!.Attach(hwnd, setUnrealAsOwner: false);
        _overlayAttached = true;
        FullScreenButton.IsEnabled = true;
        ApplyCurrentRegions();
    }

    private void DetachEditorStandaloneWindow()
    {
        _overlayWindowManager?.Detach();
        _editorStandaloneHwnd = IntPtr.Zero;
        _overlayAttached = false;
        FullScreenButton.IsEnabled = false;
        FullScreenButton.Content = "全屏";
        ShowInTaskbar = true;
        SetEditorWaitingPresentation(isWaiting: true);

        WindowState = WindowState.Normal;
        if (!_editorWaitingBounds.IsEmpty)
        {
            Left = _editorWaitingBounds.Left;
            Top = _editorWaitingBounds.Top;
            Width = Math.Max(640, _editorWaitingBounds.Width);
            Height = Math.Max(480, _editorWaitingBounds.Height);
        }
        if (_editorWaitingWindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        _hitRegionService.ApplyFull(this);
        Show();
        Activate();
    }

    private void SetEditorWaitingPresentation(bool isWaiting)
    {
        EditorWaitingBackground.Visibility = isWaiting ? Visibility.Visible : Visibility.Collapsed;
        EditorWaitingChrome.Visibility = isWaiting ? Visibility.Visible : Visibility.Collapsed;
        ControlBar.Visibility = isWaiting ? Visibility.Collapsed : Visibility.Visible;
        var contentMargin = isWaiting ? new Thickness(1, 36, 1, 1) : new Thickness(0);
        WebView.Margin = contentMargin;
        StatusPanel.Margin = contentMargin;
        ResizeMode = isWaiting ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
    }

    private void EditorWaitingChrome_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

}
