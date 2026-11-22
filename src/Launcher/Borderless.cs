using System.Runtime.InteropServices;

namespace Resurgence.Launcher;

/// <summary>The Win32 side of the borderless display mode; a test uses a fake.</summary>
public interface IGameWindow
{
    /// <summary>The process's game window (its biggest visible top-level window without an owner); 0 while it has none.</summary>
    nint Find(int pid);
    /// <summary>True while the window has a title bar or a sizing border or does not cover its monitor exactly; false while it is minimized.</summary>
    bool Framed(nint window);
    /// <summary>Removes WS_CAPTION and WS_THICKFRAME from the window's style and places it over its whole monitor (SWP_FRAMECHANGED).</summary>
    void Unframe(nint window);
}

/// <summary>
/// The borderless display mode: the game starts windowed at the monitor's size (<see cref="GameSettingsService.PrepareStart"/>), and its window loses its title bar and border once it appears.
/// The game has no borderless mode of its own: after a device reset, a resolution change or its own Alt+Enter it may put its frame back, so the window is checked again while the game runs.
/// </summary>
public static class Borderless
{
    public static readonly IGameWindow Win32 = new Win32Window();

    /// <summary>
    /// Every <paramref name="step"/> while <paramref name="running"/>: makes the game's window borderless when it has appeared or has its frame or size back. ponytail: a check every second, not a WinEvent hook:
    /// no WinEvent reports a style change, and an out-of-context hook needs a message loop and the filtering of every cursor move; the cost is a framed window for up to a second after the game restores its frame.
    /// </summary>
    public static async Task KeepAsync(IGameWindow windows, int pid, Func<bool> running, TimeSpan step)
    {
        while (running())
        {
            if (windows.Find(pid) is var w and not 0 && windows.Framed(w)) windows.Unframe(w);
            await Task.Delay(step);
        }
    }

    sealed class Win32Window : IGameWindow
    {
        const int GwlStyle = -16, GwOwner = 4;
        const long Frame = 12845056; // WS_CAPTION | WS_THICKFRAME
        const uint Nearest = 2; // MONITOR_DEFAULTTONEAREST
        const uint Flags = 4 | 16 | 32 | 512; // SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER
        static readonly nint PerMonitorV2 = -4; // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2: monitor and window in physical pixels, whatever the launcher's own awareness

        public nint Find(int pid)
        {
            (nint Window, long Area) best = default;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var owner);
                if (owner == pid && IsWindowVisible(h) && GetWindow(h, GwOwner) == 0 && GetWindowRect(h, out var r) && (long)(r.Right - r.Left) * (r.Bottom - r.Top) is var area && area > best.Area)
                    best = (h, area);
                return true;
            }, 0);
            return best.Window;
        }

        public bool Framed(nint window) => PerMonitor(() =>
            !IsIconic(window) && ((GetWindowLongPtr(window, GwlStyle) & Frame) != 0 || !GetWindowRect(window, out var r) || r != Monitor(window)));

        public void Unframe(nint window) => PerMonitor(() =>
        {
            SetWindowLongPtr(window, GwlStyle, GetWindowLongPtr(window, GwlStyle) & ~Frame);
            var m = Monitor(window);
            return SetWindowPos(window, 0, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, Flags);
        });

        static Rect Monitor(nint window)
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            return GetMonitorInfo(MonitorFromWindow(window, Nearest), ref info) ? info.Bounds : default;
        }

        static T PerMonitor<T>(Func<T> f)
        {
            var before = SetThreadDpiAwarenessContext(PerMonitorV2);
            try { return f(); }
            finally { if (before != 0) SetThreadDpiAwarenessContext(before); }
        }

        [StructLayout(LayoutKind.Sequential)] record struct Rect(int Left, int Top, int Right, int Bottom);
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public uint Size; public Rect Bounds, Work; public uint Flags; }

        delegate bool EnumWindowsProc(nint window, nint param);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint param);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] static extern nint GetWindow(nint window, int cmd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern long GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern long SetWindowLongPtr(nint window, int index, long value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    }
}
