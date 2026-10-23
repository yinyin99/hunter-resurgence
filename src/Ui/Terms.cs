using System.Collections;
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;

namespace Resurgence.Ui;

/// <summary>
/// Game vocabulary (species, categories, ...) shown from service data. The texts are the resx keys <c>Term.&lt;kind&gt;.&lt;old id&gt;</c>, one per old id and language
/// (imported from the old app's own per-language table). A real service passes the id; the mocks pass the English name, which is found again through the English text.
/// A text without a term stays as it is.
/// </summary>
public static class Terms
{
    // English text -> resx key, from the neutral resources only (the same in every language)
    static readonly Lazy<Dictionary<string, string>> ByEnglish = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var set = new ResourceManager(typeof(Strings).FullName!, typeof(Strings).Assembly).GetResourceSet(CultureInfo.InvariantCulture, true, false);
        foreach (DictionaryEntry e in set ?? throw new InvalidOperationException("neutral resources missing"))
            if (e.Key is string k && k.StartsWith("Term.", StringComparison.Ordinal) && e.Value is string v) map.TryAdd(v, k);
        return map;
    });

    /// <summary>The term of an old id, <paramref name="english"/> when the language has none.</summary>
    public static string Term(this IStringLocalizer l, string kind, int id, string english) => l[$"Term.{kind}.{id}"] is { ResourceNotFound: false } s ? s.Value : english;

    /// <summary>The term for an English text, the text itself when it is not a known term.</summary>
    public static string Term(this IStringLocalizer l, string english) => ByEnglish.Value.TryGetValue(english, out var key) ? l[key].Value : english;
}
