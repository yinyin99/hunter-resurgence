using System.Text;

namespace Resurgence.Ui;

/// <summary>The operator's pictures for the asset slots: one file per slot name in a folder (IMAGES_DIR on the website, docs/images.md), served at /images/. None: every slot is its labelled box.</summary>
public sealed class AssetImages(string? dir)
{
    /// <summary>The picture types served; no SVG, because an SVG opened directly from our origin can run script.</summary>
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    readonly Dictionary<string, string> urls = Load(dir);

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

    // read once at startup: the operator restarts the website after changing the files
    static Dictionary<string, string> Load(string? dir)
    {
        var urls = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return urls;
        foreach (var f in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
        {
            if (!Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                continue;
            var file = Path.GetFileName(f);
            // first in ordinal order wins on a duplicate key; ?v= changes with the file, so it can be cached for good
            urls.TryAdd(Key(Path.GetFileNameWithoutExtension(f)), "/images/" + Uri.EscapeDataString(file) + "?v=" + File.GetLastWriteTimeUtc(f).Ticks.ToString("x"));
        }
        return urls;
    }
}
