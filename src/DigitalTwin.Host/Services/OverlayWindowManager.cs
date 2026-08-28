using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DigitalTwin.Host.Interop;

namespace DigitalTwin.Host.Services;

public sealed class OverlayWindowManager : IDisposable
{
    private readonly Window _overlay;
    private readonly DispatcherTimer _timer;
    private IntPtr _overlayHwnd;
    private IntPtr _unrealHwnd;
    private WindowBounds? _lastBounds;
    private bool _isAttached;
    private bool _isHidden;
    private bool _setUnrealAsOwner;
    private bool _isTopMost;
    private NativeMethods.Rect _windowedRect;
    private IntPtr _windowedStyle;
    private IntPtr _windowedExStyle;

    public event EventHandler? UnrealWindowClosed;

    public event EventHandler? FullScreenChanged;

    public bool IsFullScreen { get; private set; }

    public OverlayWindowManager(Window overlay)
    {
        _overlay = overlay;
        _timer = new DispatcherTimer(DispatcherPriority.Render, overlay.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += Timer_OnTick;
    }

    public void Attach(IntPtr unrealHwnd, bool setUnrealAsOwner = true)
    {
        _overlay.Dispatcher.VerifyAccess();
        Detach();
        _overlayHwnd = new WindowInteropHelper(_overlay).Handle;
        _unrealHwnd = unrealHwnd;
        _setUnrealAsOwner = setUnrealAsOwner;
        CaptureWindowedState();
        if (_setUnrealAsOwner)
        {
            NativeMethods.SetWindowLongPtr(_overlayHwnd, NativeMethods.GwlpHwndParent, unrealHwnd);
        }
        _isAttached = true;
        SyncBounds();
        _timer.Start();
    }

    public void Detach()
    {
        DetachCore(showOverlay: true);
    }

    private void DetachCore(bool showOverlay)
    {
        _overlay.Dispatcher.VerifyAccess();
        _timer.Stop();
        if (IsFullScreen && NativeMethods.IsWindow(_unrealHwnd))
        {
            RestoreWindowed();
        }
        SetEditorOverlayTopMost(enabled: false);
        if (_setUnrealAsOwner && _overlayHwnd != IntPtr.Zero && NativeMethods.IsWindow(_overlayHwnd))
        {
            NativeMethods.SetWindowLongPtr(_overlayHwnd, NativeMethods.GwlpHwndParent, IntPtr.Zero);
        }

        _isAttached = false;
        _setUnrealAsOwner = false;
        _unrealHwnd = IntPtr.Zero;
        _lastBounds = null;
        if (showOverlay && _isHidden && _overlayHwnd != IntPtr.Zero && NativeMethods.IsWindow(_overlayHwnd))
        {
            NativeMethods.ShowWindow(_overlayHwnd, NativeMethods.SwShowNoActivate);
        }
        _isHidden = false;
        IsFullScreen = false;
    }

    public void CloseUnrealWindow()
    {
        _overlay.Dispatcher.VerifyAccess();
        if (_isAttached && NativeMethods.IsWindow(_unrealHwnd))
        {
            NativeMethods.PostMessage(_unrealHwnd, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void ToggleFullScreen()
    {
        _overlay.Dispatcher.VerifyAccess();
        if (!_isAttached || !NativeMethods.IsWindow(_unrealHwnd))
        {
            return;
        }

        var changed = IsFullScreen ? RestoreWindowed() : EnterFullScreen();
        if (!changed)
        {
            return;
        }

        IsFullScreen = !IsFullScreen;
        _lastBounds = null;
        SyncBounds();
        FullScreenChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Minimize()
    {
        _overlay.Dispatcher.VerifyAccess();
        if (_isAttached && NativeMethods.IsWindow(_unrealHwnd))
        {
            NativeMethods.ShowWindow(_unrealHwnd, NativeMethods.SwMinimize);
        }
    }

    public void Dispose()
    {
        DetachCore(showOverlay: false);
        _timer.Tick -= Timer_OnTick;
    }

    private void Timer_OnTick(object? sender, EventArgs e)
    {
        if (!_isAttached)
        {
            return;
        }

        if (!NativeMethods.IsWindow(_unrealHwnd))
        {
            _timer.Stop();
            _isAttached = false;
            UnrealWindowClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        SyncBounds();
    }

    private void SyncBounds()
    {
        if (!_setUnrealAsOwner)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != _unrealHwnd && foreground != _overlayHwnd)
            {
                SetEditorOverlayTopMost(enabled: false);
                if (!_isHidden)
                {
                    NativeMethods.ShowWindow(_overlayHwnd, NativeMethods.SwHide);
                    _isHidden = true;
                }
                return;
            }

            SetEditorOverlayTopMost(enabled: true);
        }

        if (NativeMethods.IsIconic(_unrealHwnd))
        {
            if (!_isHidden)
            {
                NativeMethods.ShowWindow(_overlayHwnd, NativeMethods.SwHide);
                _isHidden = true;
            }

            return;
        }

        if (_isHidden)
        {
            NativeMethods.ShowWindow(_overlayHwnd, NativeMethods.SwShowNoActivate);
            _isHidden = false;
            _lastBounds = null;
        }

        if (!NativeMethods.GetClientRect(_unrealHwnd, out var clientRect))
        {
            return;
        }

        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(_unrealHwnd, ref origin))
        {
            return;
        }

        var bounds = new WindowBounds(origin.X, origin.Y, clientRect.Width, clientRect.Height);
        if (_lastBounds == bounds)
        {
            // Unreal can restore its own Z order after loading or changing maps.
            // Keep the owned web overlay above packaged Unreal without making it
            // globally topmost; Editor mode still needs the topmost band.
            NativeMethods.SetWindowPos(
                _overlayHwnd,
                _setUnrealAsOwner ? NativeMethods.HwndTop : NativeMethods.HwndTopMost,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoMove |
                NativeMethods.SwpNoSize |
                NativeMethods.SwpNoActivate |
                NativeMethods.SwpShowWindow);
            return;
        }

        NativeMethods.SetWindowPos(
            _overlayHwnd,
            _setUnrealAsOwner ? NativeMethods.HwndTop : NativeMethods.HwndTopMost,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        _lastBounds = bounds;
    }

    private void SetEditorOverlayTopMost(bool enabled)
    {
        if (_setUnrealAsOwner || _overlayHwnd == IntPtr.Zero ||
            !NativeMethods.IsWindow(_overlayHwnd) || _isTopMost == enabled)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _overlayHwnd,
            enabled ? NativeMethods.HwndTopMost : NativeMethods.HwndNoTopMost,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoActivate);
        _isTopMost = enabled;
    }

    private void CaptureWindowedState()
    {
        NativeMethods.GetWindowRect(_unrealHwnd, out _windowedRect);
        _windowedStyle = NativeMethods.GetWindowLongPtr(_unrealHwnd, NativeMethods.GwlStyle);
        _windowedExStyle = NativeMethods.GetWindowLongPtr(_unrealHwnd, NativeMethods.GwlExStyle);
    }

    private bool EnterFullScreen()
    {
        CaptureWindowedState();
        var monitor = NativeMethods.MonitorFromWindow(_unrealHwnd, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        var fullScreenStyle = (_windowedStyle.ToInt64() & ~NativeMethods.WsOverlappedWindow) |
                              NativeMethods.WsPopup;
        NativeMethods.SetWindowLongPtr(_unrealHwnd, NativeMethods.GwlStyle, new IntPtr(fullScreenStyle));
        NativeMethods.SetWindowPos(
            _unrealHwnd,
            IntPtr.Zero,
            monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Top,
            monitorInfo.Monitor.Width,
            monitorInfo.Monitor.Height,
            NativeMethods.SwpFrameChanged | NativeMethods.SwpShowWindow);
        return true;
    }

    private bool RestoreWindowed()
    {
        NativeMethods.SetWindowLongPtr(_unrealHwnd, NativeMethods.GwlStyle, _windowedStyle);
        NativeMethods.SetWindowLongPtr(_unrealHwnd, NativeMethods.GwlExStyle, _windowedExStyle);
        NativeMethods.SetWindowPos(
            _unrealHwnd,
            IntPtr.Zero,
            _windowedRect.Left,
            _windowedRect.Top,
            _windowedRect.Width,
            _windowedRect.Height,
            NativeMethods.SwpFrameChanged | NativeMethods.SwpShowWindow);
        return true;
    }

    private sealed record WindowBounds(int X, int Y, int Width, int Height);
}
