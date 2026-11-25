using System.Runtime.InteropServices;

namespace Resurgence.Launcher;

/// <summary>The Win32 side of the borderless display mode; a test uses a fake.</summary>
public interface IGameWindow
{
    /// <summary>The process's game window (its biggest visible top-level window without an owner); 0 while it has none.</summary>
    nint Find(int pid);
    /// <summary>True while the window has a title bar or a sizing border or does not cover its monitor exactly; false while it is minimized.</summary>
    bool Framed(nint window);
    /// <summary>Removes WS_CAPTION and WS_THICKFRAME from the window's style and places it over its whole monitor (SWP_FRAMECHANGED), and tells the shell it is a full-screen window.</summary>
    void Unframe(nint window);
    /// <summary>True while the window, or another window of its process (a message box), is the foreground window.</summary>
    bool Foreground(nint window);
    /// <summary>The game is in the background: the window is not topmost and the shell no longer takes it for a full-screen window, so the taskbar shows over it. Its size does not change.</summary>
    void Background(nint window);
    /// <summary>The game is back in the foreground: the shell takes the window for a full-screen window again and hides the taskbar under it.</summary>
    void Fullscreen(nint window);
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
    /// While the window is not the foreground window the taskbar shows over it (<see cref="IGameWindow.Background"/>) and the window is left alone, so a frame it gets back is dealt with when it returns.
    /// </summary>
    public static async Task KeepAsync(IGameWindow windows, int pid, Func<bool> running, TimeSpan step)
    {
        nint seen = 0;
        var back = false; // the window is in the background and the taskbar shows over it
        while (running())
        {
            if (windows.Find(pid) is var w and not 0)
            {
                var first = w != seen;
                if (first) back = false;
                seen = w;
                if (windows.Foreground(w))
                {
                    if (windows.Framed(w)) windows.Unframe(w);
                    else if (back) windows.Fullscreen(w);
                    back = false;
                }
                else
                {
                    if (first && windows.Framed(w)) windows.Unframe(w); // a window that appears behind another one is made borderless too
                    if (!back) windows.Background(w);
                    back = true;
                }
            }
            await Task.Delay(step);
        }
    }

    sealed class Win32Window : IGameWindow
    {
        const int GwlStyle = -16, GwlExStyle = -20, GwOwner = 4;
        const long Frame = 12845056; // WS_CAPTION | WS_THICKFRAME
        const long Topmost = 8; // WS_EX_TOPMOST
        const uint Nearest = 2; // MONITOR_DEFAULTTONEAREST
        const uint Flags = 4 | 16 | 32 | 512; // SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER
        const uint InPlace = 1 | 2 | 16; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE
        static readonly nint NoTopmost = -2; // HWND_NOTOPMOST
        static readonly nint PerMonitorV2 = -4; // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2: monitor and window in physical pixels, whatever the launcher's own awareness

        public nint Find(int pid)
        {
            (nint Window, long Area) best = default;
            var name = new char[8];
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var owner);
                // a message box of the game (a start error before its window) is never stretched
                if (owner == pid && IsWindowVisible(h) && GetWindow(h, GwOwner) == 0 && !name.AsSpan(0, GetClassName(h, name, name.Length)).SequenceEqual("#32770")
                    && GetWindowRect(h, out var r) && (long)(r.Right - r.Left) * (r.Bottom - r.Top) is var area && area > best.Area)
                    best = (h, area);
                return true;
            }, 0);
            return best.Window;
        }

        // a monitor Windows does not describe leaves the window alone (it would be sized to nothing)
        public bool Framed(nint window) => PerMonitor(() =>
            Monitor(window) is var m && m != default && !IsIconic(window) && ((GetWindowLongPtr(window, GwlStyle) & Frame) != 0 || !GetWindowRect(window, out var r) || r != m));

        public void Unframe(nint window)
        {
            PerMonitor(() =>
            {
                SetWindowLongPtr(window, GwlStyle, GetWindowLongPtr(window, GwlStyle) & ~Frame);
                var m = Monitor(window);
                return SetWindowPos(window, 0, m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, Flags);
            });
            MarkFullscreen(window, true);
        }

        public bool Foreground(nint window)
        {
            var front = GetForegroundWindow();
            if (front == 0) return false;
            GetWindowThreadProcessId(window, out var pid);
            GetWindowThreadProcessId(front, out var frontPid);
            return pid == frontPid;
        }

        public void Background(nint window)
        {
            if ((GetWindowLongPtr(window, GwlExStyle) & Topmost) != 0) SetWindowPos(window, NoTopmost, 0, 0, 0, 0, InPlace);
            MarkFullscreen(window, false);
        }

        public void Fullscreen(nint window) => MarkFullscreen(window, true);

        // the shell's list of full-screen windows: it hides the taskbar under a window on it. A shell that does not answer (it restarts) leaves the window as it is; the next change tries again.
        static void MarkFullscreen(nint window, bool fullscreen)
        {
            ITaskbarList2? list = null;
            try
            {
                list = (ITaskbarList2)new TaskbarList();
                list.HrInit();
                list.MarkFullscreenWindow(window, fullscreen);
            }
            catch (Exception e) when (e is COMException or InvalidCastException) { }
            finally { if (list != null) Marshal.ReleaseComObject(list); }
        }

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

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] class TaskbarList { }

        // ITaskbarList2 with the methods of ITaskbarList it extends first, in vtable order
        [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList2
        {
            void HrInit();
            void AddTab(nint window);
            void DeleteTab(nint window);
            void ActivateTab(nint window);
            void SetActiveAlt(nint window);
            void MarkFullscreenWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        }

        delegate bool EnumWindowsProc(nint window, nint param);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, nint param);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint pid);
        [DllImport("user32.dll")] static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] static extern nint GetWindow(nint window, int cmd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")] static extern int GetClassName(nint window, char[] name, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern long GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern long SetWindowLongPtr(nint window, int index, long value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    }
}
