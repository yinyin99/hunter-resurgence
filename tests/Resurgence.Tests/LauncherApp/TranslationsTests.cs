using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

namespace Resurgence.Tests.LauncherApp;

public class TranslationsTests
{
    /// <summary>Every language resx has exactly the keys of Strings.resx and the same {0}/{1}... placeholders in each string (a mismatch is a FormatException at runtime); {game} tokens are not holes.</summary>
    [Fact]
    public void EveryLanguageHasTheKeysAndPlaceholdersOfStrings()
    {
        var rm = new ResourceManager("Resurgence.Ui.Strings", typeof(Resurgence.Ui.Strings).Assembly);
        Dictionary<string, string> Read(CultureInfo c) =>
            (rm.GetResourceSet(c, true, false) ?? throw new Exception($"FAIL no resources for '{c.Name}'")).Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Order());
        static string Plain(string s) => Regex.Replace(s, "{(game|GAME|game_full)}", "x");
        var en = Read(CultureInfo.InvariantCulture);
        var bad = new List<string>();
        foreach (var culture in Resurgence.Ui.Languages.All.Where(l => l.Code != "en").Select(l => l.Code))
        {
            var tr = Read(CultureInfo.GetCultureInfo(culture));
            bad.AddRange(en.Keys.Except(tr.Keys).Select(k => $"{culture}: missing {k}"));
            bad.AddRange(tr.Keys.Except(en.Keys).Select(k => $"{culture}: extra {k}"));
            foreach (var (k, v) in tr.Where(p => en.ContainsKey(p.Key)))
            {
                if (Holes(v) != Holes(en[k])) bad.Add($"{culture}: {k} has placeholders {{{Holes(v)}}}, English has {{{Holes(en[k])}}}");
                try { string.Format(Plain(v), "", "", "", ""); } catch (FormatException) { bad.Add($"{culture}: {k} is not a valid format string"); }
            }
        }
        Assert.True(bad.Count == 0, "FAIL translations: " + string.Join("; ", bad));
    }
}
