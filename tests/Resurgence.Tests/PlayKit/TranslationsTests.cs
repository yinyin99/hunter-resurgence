using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Play;

namespace Resurgence.Tests.PlayKit;

public class TranslationsTests
{
    /// <summary>Every language resx has exactly the keys of Strings.resx and the same {0}/{1}... placeholders in each string.</summary>
    [Fact]
    public void EveryLanguageHasTheKeysAndPlaceholdersOfStrings()
    {
        static Dictionary<string, string> Read(CultureInfo c) =>
            (Loc.Rm.GetResourceSet(c, true, false) ?? throw new Exception($"FAIL no resources for '{c.Name}'")).Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Distinct().Order());
        var en = Read(CultureInfo.InvariantCulture);
        var bad = new List<string>();
        foreach (var culture in Loc.Cultures)
        {
            var tr = Read(CultureInfo.GetCultureInfo(culture));
            bad.AddRange(en.Keys.Except(tr.Keys).Select(k => $"{culture}: missing {k}"));
            bad.AddRange(tr.Keys.Except(en.Keys).Select(k => $"{culture}: extra {k}"));
            foreach (var (k, v) in tr.Where(p => en.ContainsKey(p.Key)))
                if (Holes(v) != Holes(en[k])) bad.Add($"{culture}: {k} has placeholders {{{Holes(v)}}}, English has {{{Holes(en[k])}}}");
        }
        Assert.True(bad.Count == 0, "FAIL translations: " + string.Join("; ", bad));
    }
}
