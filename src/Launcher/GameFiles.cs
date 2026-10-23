using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Resurgence.Launcher;

public enum FileProblem { None, NotConfigured, Missing, BadFormat }

/// <summary>The graphics file as the client reads it (line numbers are the file's, line 1 is the format marker).</summary>
public sealed record GraphicsSettings
{
    public bool Fullscreen { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Hz { get; set; }
    /// <summary>0 Low .. 3 Very high.</summary>
    public int Scene { get; set; }
    /// <summary>0 Low .. 2 High.</summary>
    public int Texture { get; set; }
    /// <summary>0 Low .. 3 Very high.</summary>
    public int Shadow { get; set; }
    /// <summary>0 Off, 1 2x, 2 4x, 3 8x.</summary>
    public int Msaa { get; set; }
    /// <summary>0 Default, 1 Off, 2 2x, 3 4x, 4 8x, 5 16x.</summary>
    public int Aniso { get; set; }
    public bool Vsync { get; set; }
}

/// <summary>The integer options of the options file that exist in it. A key whose line is missing is simply absent: lines are never added.</summary>
public sealed class OptionsSettings(Dictionary<string, int> values)
{
    public IReadOnlyDictionary<string, int> Values => values;
    public bool Has(string key) => values.ContainsKey(key);
    public int Get(string key) => values.GetValueOrDefault(key);
    public void Set(string key, int v) { if (values.ContainsKey(key)) values[key] = v; }
    public OptionsSettings Clone() => new(new(values));
    public bool SameAs(OptionsSettings o) => values.Count == o.Values.Count && values.All(kv => o.Values.TryGetValue(kv.Key, out var v) && v == kv.Value);
}

/// <summary>
/// Reads and writes the two settings files of the game in %APPDATA%. Only the edited values change; every other byte stays (files are handled as Latin-1,
/// which maps every byte to one char). The client reads them once at start, so nothing is written while it runs.
/// </summary>
public static class GameFiles
{
    public const string BackupSuffix = ".resurgence-backup";
    const int Marker = 129;

    public static readonly string[] OptionKeys =
    [
        "MasterVolume", "Gamma", "LookSpeed", "AimSpeed", "BinocularSpeed", "InvertedMouse", "UseGamepad", "ShowPlayerNames", "ShowStatusBar",
        "ShowPlayerSpotting", "ShowAnimalSpotting", "ShowChat", "ShowMapPopups", "ContextualHelp", "AutoReload", "ShowGlassesShader", "ScopeQuality",
    ];

    static readonly Encoding Latin1 = Encoding.Latin1;
    static readonly Regex Int = new(@"-?\d+");

    public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>True while a process named like the client exe (without extension) runs.</summary>
    public static bool ClientRunning(string? clientExe)
    {
        if (string.IsNullOrEmpty(clientExe)) return false;
        var all = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(clientExe));
        foreach (var p in all) p.Dispose();
        return all.Length > 0;
    }

    // ---------- graphics file ----------

    public static (GraphicsSettings? Value, FileProblem Problem) ReadGraphics(string path)
    {
        if (!File.Exists(path)) return (null, FileProblem.Missing);
        return ParseGraphics(Latin1.GetString(File.ReadAllBytes(path)).Split('\n')) is { } g ? (g, FileProblem.None) : (null, FileProblem.BadFormat);
    }

    /// <summary>Throws InvalidDataException when the file is not in the known format (marker line); the file is then left alone.</summary>
    public static void WriteGraphics(string path, GraphicsSettings g)
    {
        var text = Latin1.GetString(File.ReadAllBytes(path));
        var lines = text.Split('\n');
        if (ParseGraphics(lines) is null) throw new InvalidDataException("graphics file format");
        int[][] want = [[g.Fullscreen ? 1 : 0], [g.Width, g.Height, g.Hz], [g.Scene], [g.Texture], [g.Shadow], [g.Msaa], [g.Aniso], [g.Vsync ? 1 : 0]];
        for (var i = 0; i < want.Length; i++) lines[i + 1] = SetInts(lines[i + 1], want[i]);
        Save(path, text, string.Join('\n', lines));
    }

    static GraphicsSettings? ParseGraphics(string[] lines)
    {
        if (lines.Length < 9) return null;
        int[]? Ints(int line, int n)
        {
            var m = Int.Matches(lines[line]);
            return m.Count >= n && m.Take(n).All(x => int.TryParse(x.Value, out _)) ? [.. m.Take(n).Select(x => int.Parse(x.Value))] : null;
        }
        if (Ints(0, 1) is not [Marker, ..] || Ints(1, 1) is not [var fs] || Ints(2, 3) is not [var w, var h, var hz] || Ints(3, 1) is not [var scene] ||
            Ints(4, 1) is not [var tex] || Ints(5, 1) is not [var shadow] || Ints(6, 1) is not [var msaa] || Ints(7, 1) is not [var aniso] || Ints(8, 1) is not [var vsync])
            return null;
        return fs is 0 or 1 && vsync is 0 or 1 && w > 0 && h > 0 && hz > 0 && scene is >= 0 and <= 3 && tex is >= 0 and <= 2 && shadow is >= 0 and <= 3 && msaa is >= 0 and <= 3 && aniso is >= 0 and <= 5
            ? new() { Fullscreen = fs == 1, Width = w, Height = h, Hz = hz, Scene = scene, Texture = tex, Shadow = shadow, Msaa = msaa, Aniso = aniso, Vsync = vsync == 1 }
            : null;
    }

    // replaces the n-th integer of the line only when its value differs: spacing and everything else stay
    static string SetInts(string line, int[] vals)
    {
        var k = 0;
        return Int.Replace(line, m =>
        {
            var i = k++;
            return i < vals.Length && int.Parse(m.Value) != vals[i] ? vals[i].ToString() : m.Value;
        });
    }

    // ---------- options file ----------

    static Regex ValueLine(string prefix, string key) => new($"""(<value name="{Regex.Escape(prefix + key)}" type="int">)(-?\d+)(</value>)""");

    public static (OptionsSettings? Value, FileProblem Problem) ReadOptions(string path, string prefix)
    {
        if (!File.Exists(path)) return (null, FileProblem.Missing);
        var text = Latin1.GetString(File.ReadAllBytes(path));
        var d = new Dictionary<string, int>();
        foreach (var k in OptionKeys)
            if (ValueLine(prefix, k).Match(text) is { Success: true } m && int.TryParse(m.Groups[2].Value, out var v)) d[k] = v;
        return d.Count == 0 ? (null, FileProblem.BadFormat) : (new(d), FileProblem.None);
    }

    public static void WriteOptions(string path, string prefix, OptionsSettings o)
    {
        var text = Latin1.GetString(File.ReadAllBytes(path));
        var result = text;
        foreach (var (k, v) in o.Values)
            result = ValueLine(prefix, k).Replace(result, m => int.TryParse(m.Groups[2].Value, out var cur) && cur == v ? m.Value : m.Groups[1].Value + v + m.Groups[3].Value, 1);
        Save(path, text, result);
    }

    // ---------- common ----------

    /// <summary>Nothing to do when the text is unchanged. Otherwise: one-time copy beside the file (never overwritten), then temp file + replace.</summary>
    static void Save(string path, string before, string after)
    {
        if (before == after) return;
        var backup = path + BackupSuffix;
        if (!File.Exists(backup)) File.Copy(path, backup);
        var tmp = path + ".resurgence-tmp";
        try
        {
            File.WriteAllBytes(tmp, Latin1.GetBytes(after));
            File.Replace(tmp, path, null);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ---------- display modes ----------

    /// <summary>Distinct width x height @ refresh modes of the primary display, sorted.</summary>
    public static IReadOnlyList<(int W, int H, int Hz)> DisplayModes()
    {
        var set = new SortedSet<(int, int, int)>();
        var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
        for (var i = 0; EnumDisplaySettings(null, i, ref dm); i++)
            if (dm.dmDisplayFrequency > 1) set.Add(((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency));
        return [.. set];
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency, dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
}
