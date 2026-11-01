using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Play;

/// <summary>The marked block of the Windows hosts file that points the game's names at this PC. Add and Remove are pure text functions: Remove(Add(t)) is t, byte for byte.</summary>
static class Hosts
{
    public const string Begin = "# >>> hunter-resurgence play (undo: play setup --remove)", End = "# <<< hunter-resurgence play";

    public static string DefaultPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    static string[] Lines(string text) => Regex.Split(text, "(?<=\n)").Where(l => l.Length > 0).ToArray();

    public static bool Has(string text) => Lines(text).Any(l => l.Trim() == Begin);

    public static string Remove(string text)
    {
        var kept = new List<string>();
        var inside = false;
        foreach (var l in Lines(text))
        {
            var t = l.Trim();
            if (t == Begin) inside = true;
            else if (inside && t == End)
            {
                inside = false;
                // the block was appended after a last line without a newline (Add added one): take it away again
                if (!l.EndsWith('\n') && kept.Count > 0) kept[^1] = kept[^1].TrimEnd('\r', '\n');
            }
            else if (!inside || !t.StartsWith("127.0.0.1 ")) kept.Add(l); // a line of the user's inside the block stays
        }
        return string.Concat(kept);
    }

    public static string Add(string text, IEnumerable<string> hosts)
    {
        text = Remove(text);
        var nl = text.Contains("\r\n") || !text.Contains('\n') ? "\r\n" : "\n";
        var glue = text.Length > 0 && !text.EndsWith('\n') ? nl : "";
        var block = string.Join(nl, new[] { Begin }.Concat(hosts.Select(h => "127.0.0.1 " + h)).Append(End));
        return text + glue + block + (glue.Length == 0 ? nl : "");
    }

    /// <summary>Adds the block (<paramref name="hosts"/>) or removes it (null) in the hosts file at <paramref name="path"/>. False when nothing had to change; throws UnauthorizedAccessException without administrator rights.</summary>
    public static bool Apply(string path, IReadOnlyCollection<string>? hosts)
    {
        // Latin1 maps every byte to one char and back (File.ReadAllText would drop a BOM), so the player's own lines survive byte for byte
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        var next = hosts is null ? Remove(text) : Add(text, hosts);
        if (next == text) return false;
        File.WriteAllText(path, next, Encoding.Latin1);
        return true;
    }

    /// <summary>Apply, then make Windows forget the old answers. Needs administrator rights.</summary>
    public static void ApplyAndFlush(string path, IReadOnlyCollection<string>? hosts)
    {
        if (Apply(path, hosts)) Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
    }
}
