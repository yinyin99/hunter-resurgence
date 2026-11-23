using System.Runtime.InteropServices;

namespace Resurgence.Launcher;

/// <summary>One display adapter as <c>EnumDisplayDevices</c> lists it.</summary>
/// <param name="Name">The adapter's description (<c>DeviceString</c>).</param>
/// <param name="Primary"><c>DISPLAY_DEVICE_PRIMARY_DEVICE</c>: the adapter the game's Direct3D 9 setup uses.</param>
/// <param name="Attached"><c>DISPLAY_DEVICE_ATTACHED_TO_DESKTOP</c>: part of the desktop.</param>
/// <param name="Monitor">A monitor of this adapter is active (<c>EnumDisplayDevices</c> of the adapter's name lists a child with <c>DISPLAY_DEVICE_ACTIVE</c>).</param>
public sealed record DisplayAdapter(string Name, bool Primary, bool Attached, bool Monitor);

/// <summary>What this PC says of its screen, for the <c>display</c> install check (docs/launcher.md#the-install-check); a test passes fakes.</summary>
/// <param name="Modes">The primary display's modes (<see cref="GameFiles.DisplayModes"/>).</param>
/// <param name="Current">The primary display's current mode; null when Windows does not say.</param>
/// <param name="Adapters">The display adapters.</param>
/// <param name="GraphicsFiles">The full paths of the graphics files the game may start with.</param>
public sealed record ScreenProbes(Func<IReadOnlyList<(int W, int H, int Hz)>> Modes, Func<(int W, int H, int Hz)?> Current, Func<IReadOnlyList<DisplayAdapter>> Adapters, Func<IEnumerable<string>> GraphicsFiles)
{
    /// <summary>This PC's screen; the graphics files are those of the mode the game is planned to start in (<see cref="Settings.StartGraphicsFiles"/>).</summary>
    public static ScreenProbes Real(Settings settings, Func<string?> plannedMode) =>
        new(GameFiles.DisplayModes, GameFiles.CurrentDisplayMode, ReadAdapters, () => settings.StartGraphicsFiles(plannedMode()).Select(f => Path.Combine(GameFiles.AppData, f)));

    const int AttachedToDesktop = 1, Primary = 4, MonitorActive = 1;

    static IReadOnlyList<DisplayAdapter> ReadAdapters()
    {
        var found = new List<DisplayAdapter>();
        var adapter = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++)
        {
            var monitor = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            var active = false;
            for (uint j = 0; !active && EnumDisplayDevices(adapter.DeviceName, j, ref monitor, 0); j++) active = (monitor.StateFlags & MonitorActive) != 0;
            found.Add(new(adapter.DeviceString, (adapter.StateFlags & Primary) != 0, (adapter.StateFlags & AttachedToDesktop) != 0, active));
        }
        return found;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint devNum, ref DisplayDevice displayDevice, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
}
