using System.Text;

namespace Resurgence.Ui;

/// <summary>The operator's pictures for the asset slots: one file per slot name, served at /images/ by the website (IMAGES_DIR, docs/images.md); the launcher gets the website's list from /images.json. None: every slot is its labelled box.</summary>
public sealed class AssetImages
{
    /// <summary>The picture types served; no SVG, because an SVG opened directly from our origin can run script.</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    readonly Dictionary<string, string> urls = [];

    /// <summary>The pictures of <paramref name="index"/> (file name → version, as <see cref="Index"/> gives it) at <paramref name="origin"/>/images/; "" is this site.</summary>
    public AssetImages(IReadOnlyDictionary<string, string> index, string origin = "")
    {
        // the launcher's index comes from the network: only picture names, and name and version escaped, so a URL stays one /images/ file
        // and cannot leave the url('...') of MapPlaceholder
        foreach (var (file, version) in index.OrderBy(e => e.Key, StringComparer.Ordinal))
            if (version is not null && Picture(file))
                // first in ordinal order wins on a duplicate key; ?v= changes with the file, so it can be cached for good
                urls.TryAdd(Key(Path.GetFileNameWithoutExtension(file)), origin + "/images/" + Uri.EscapeDataString(file) + "?v=" + Uri.EscapeDataString(version));
    }

    /// <summary>The URL of the picture of slot <paramref name="name"/>, or null (the box).</summary>
    public string? Url(string name) => urls.GetValueOrDefault(Key(name));

    /// <summary>Lower case; every run of characters outside a-z and 0-9 becomes one '-'; no '-' at either end. "species-Alpine Ibex" → "species-alpine-ibex".</summary>
    public static string Key(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        if (sb.Length > 0 && sb[^1] == '-')
            sb.Length--;
        return sb.ToString();
    }

    /// <summary>The pictures in <paramref name="dir"/>: file name → version (the file's time, hex ticks). Null or missing folder: none. Read once at start: the operator restarts the website after changing the files.</summary>
    public static Dictionary<string, string> Index(string? dir) =>
        string.IsNullOrEmpty(dir) || !Directory.Exists(dir) ? []
        : Directory.EnumerateFiles(dir).Where(f => Picture(Path.GetFileName(f)))
            .ToDictionary(f => Path.GetFileName(f), f => File.GetLastWriteTimeUtc(f).Ticks.ToString("x"));

    // a dot file is never served (PhysicalFileProvider hides it); no SVG (see Extensions)
    static bool Picture(string file) => !file.StartsWith('.') && Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);
}
