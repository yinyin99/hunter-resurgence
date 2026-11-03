using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Backup;

namespace Resurgence.Tests.BackupTool;

public class TranslationsTests
{
    /// <summary>Every language resx has exactly the keys of Strings.resx and the same {0}/{1}... placeholders in each string (a mismatch is a FormatException at runtime).</summary>
    [Fact]
    public void EveryLanguageHasTheKeysAndPlaceholdersOfStrings()
    {
        static Dictionary<string, string> Read(CultureInfo c) =>
            (Loc.Rm.GetResourceSet(c, true, false) ?? throw new Exception($"FAIL no resources for '{c.Name}'")).Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Order());
        var en = Read(CultureInfo.InvariantCulture);
        var bad = new List<string>();
        foreach (var (code, culture, _) in Loc.Languages.Where(l => l.Code != "en"))
        {
            var tr = Read(CultureInfo.GetCultureInfo(culture));
            bad.AddRange(en.Keys.Except(tr.Keys).Select(k => $"{culture}: missing {k}"));
            bad.AddRange(tr.Keys.Except(en.Keys).Select(k => $"{culture}: extra {k}"));
            foreach (var (k, v) in tr.Where(p => en.ContainsKey(p.Key)))
            {
                if (Holes(v) != Holes(en[k])) bad.Add($"{culture}: {k} has placeholders {{{Holes(v)}}}, English has {{{Holes(en[k])}}}");
                try { string.Format(v, "", "", "", ""); } catch (FormatException) { bad.Add($"{culture}: {k} is not a valid format string"); }
            }
        }
        Assert.True(bad.Count == 0, "FAIL translations: " + string.Join("; ", bad));
    }

    /// <summary>Parity cannot see a key that no file holds: every key the engine and the window ask for exists, non-empty, in every language.</summary>
    [Fact]
    public void EveryKeyTheCodeAsksForExistsInEveryLanguage()
    {
        var keys = Engine.Groups.SelectMany(g => new[] { "Group_" + g.Key, "GroupDesc_" + g.Key })
            .Concat(Enumerable.Range(1, 6).Select(i => "NotCovered_" + i))
            .Concat(["Fail_Server", "Fail_NotData", "Fail_Refused", "Err_NoToken", "Err_Refused", "Err_Lookup", "Err_NoUid", "Tag_All", "Tag_Part", "Tip_All", "Tip_Part",
                "Slow_kills", "Slow_photos", "Slow_history", "Slow_older"])
            .Concat(new[] { "Nothing", "NoProfile", "NoSession" }.SelectMany(p => new[] { "Msg_" + p, "Msg_" + p + "Text" }));
        var bad = new List<string>();
        foreach (var (_, culture, _) in Loc.Languages)
            foreach (var k in keys)
                if (string.IsNullOrWhiteSpace(Loc.Rm.GetString(k, CultureInfo.GetCultureInfo(culture)))) bad.Add($"{culture}: {k}");
        Assert.True(bad.Count == 0, "FAIL missing or empty: " + string.Join("; ", bad));
        Assert.Equal(6, Engine.NotCovered.Length);
    }
}
