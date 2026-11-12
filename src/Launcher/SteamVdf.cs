using System.Text;

namespace Resurgence.Launcher;

/// <summary>
/// Reads and edits the one <c>LaunchOptions</c> value of an app in Steam's <c>localconfig.vdf</c> as a text edit: every byte outside that value stays as it was.
/// A file that is not well-formed (quotes, braces, key/value pairs) is refused with a <see cref="FormatException"/>, never guessed at.
/// </summary>
static class SteamVdf
{
    static readonly string[] AppsPath = ["UserLocalConfigStore", "Software", "Valve", "Steam", "apps"];
    const string Key = "LaunchOptions";

    /// <summary>The launch option of <paramref name="appId"/>; null when there is none. FormatException: the file cannot be parsed.</summary>
    public static string? Get(string text, string appId) => Find(text, appId).Value is { } v ? Unquote(text, v) : null;

    /// <summary>The text with the launch option of <paramref name="appId"/> set to <paramref name="value"/>, or removed when null. FormatException: the file cannot be parsed, or has no apps block.</summary>
    public static string Set(string text, string appId, string? value)
    {
        var f = Find(text, appId);
        if (f.Key is { } k && f.Value is { } v)
        {
            if (value is not null) return text[..v.Start] + Quote(value) + text[v.End..];
            var (start, end) = (k.Start, v.End);
            var ls = LineStart(text, start);
            if (Blank(text, ls, start)) // the pair is alone on its line: the whole line goes
            {
                start = ls;
                while (end < text.Length && text[end] is ' ' or '\t') end++;
                if (text.AsSpan(end).StartsWith("\r\n")) end += 2; else if (end < text.Length && text[end] == '\n') end++;
            }
            return text[..start] + text[end..];
        }
        if (value is null) return text;
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var pair = $"\"{Key}\"\t\t{Quote(value)}";
        if (f.Block is { } b) return Insert(text, b.Close, [pair], nl);
        var apps = f.Apps ?? throw new FormatException("no apps block"); // the app has no block yet: a new one at the end of apps
        return Insert(text, apps.Close, [$"\"{appId}\"", "{", "\t" + pair, "}"], nl);
    }

    // the lines go before the closing brace at 'close', one tab deeper than the brace: on the lines before it when it stands alone on its line, else inline
    static string Insert(string text, int close, string[] lines, string nl)
    {
        var ls = LineStart(text, close);
        if (!Blank(text, ls, close)) return text[..close] + string.Join(" ", lines) + " " + text[close..];
        var indent = text[ls..close] + "\t";
        return text[..ls] + string.Concat(lines.Select(l => indent + l + nl)) + text[ls..];
    }

    static int LineStart(string t, int i) => i == 0 ? 0 : t.LastIndexOf('\n', i - 1) + 1;
    static bool Blank(string t, int a, int b) => t.AsSpan(a, b - a).Trim(" \t").IsEmpty;

    static string Quote(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static string Unquote(string text, Tok t)
    {
        var sb = new StringBuilder();
        for (var i = t.Start + 1; i < t.End - 1; i++)
        {
            if (text[i] == '\\' && i + 2 < t.End)
            {
                var n = text[i + 1];
                if (n is '\\' or '"') { sb.Append(n); i++; continue; }
                if (n == 'n') { sb.Append('\n'); i++; continue; }
                if (n == 't') { sb.Append('\t'); i++; continue; }
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    readonly record struct Tok(int Start, int End);
    readonly record struct Blk(int Open, int Close);
    sealed record Found(Blk? Apps = null, Blk? Block = null, Tok? Key = null, Tok? Value = null);

    /// <summary>One pass over the whole file (so all of it is validated) that notes the apps block, the app's block and its LaunchOptions pair.</summary>
    static Found Find(string text, string appId)
    {
        var path = new List<string>();   // the keys of the open blocks
        var opens = new Stack<int>();
        Blk? apps = null, block = null;
        Tok? key = null, value = null, pending = null; // pending: a key waiting for its value or its block
        // inside apps (n = 5) or inside the app's block (n = 6): the path is UserLocalConfigStore/Software/Valve/Steam/apps[/appId], in Steam's own spelling of case
        bool At(int n) => path.Count == n && n >= AppsPath.Length
            && AppsPath.Select((p, i) => path[i].Equals(p, StringComparison.OrdinalIgnoreCase)).All(x => x) && (n == AppsPath.Length || path[n - 1] == appId);
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '\uFEFF') { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '}')
            {
                if (pending is not null || opens.Count == 0) throw new FormatException("unexpected }");
                var open = opens.Pop();
                if (At(AppsPath.Length)) apps = new(open, i);
                else if (At(AppsPath.Length + 1)) block = new(open, i);
                path.RemoveAt(path.Count - 1);
                i++;
                continue;
            }
            if (c == '{')
            {
                if (pending is not { } k) throw new FormatException("unexpected {");
                path.Add(Unquote(text, k));
                opens.Push(i++);
                pending = null;
                continue;
            }
            if (c != '"') throw new FormatException("unquoted text");
            var tok = Quoted(text, i);
            i = tok.End;
            if (pending is not { } k0) { pending = tok; continue; }
            if (At(AppsPath.Length + 1) && Unquote(text, k0).Equals(Key, StringComparison.OrdinalIgnoreCase)) (key, value) = (k0, tok);
            pending = null;
        }
        if (opens.Count > 0 || pending is not null) throw new FormatException("unbalanced");
        return new(apps, block, key, value);
    }

    static Tok Quoted(string text, int start)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\') i++;
            else if (text[i] == '"') return new(start, i + 1);
        }
        throw new FormatException("unterminated string");
    }
}
