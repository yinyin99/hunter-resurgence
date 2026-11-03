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
        var en = Read(CultureInfo.InvariantCulture);
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\d+)").Select(m => m.Groups[1].Value).Order());
        static string Plain(string s) => Regex.Replace(s, "{(game|GAME|game_full)}", "x");
        var bad = new List<string>();
        foreach (var culture in Others)
        {
            var tr = Read(culture);
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

    /// <summary>Keys whose English text is right in every language: names.</summary>
    static readonly HashSet<string> NamesKeptInEnglish = ["App.Name", "Dialog.Tutorial.Msaa", "Board.LaneBandit", "Board.BustThrough", "Set.SteamTitle"];

    /// <summary>A key whose English text is in every translation was copied, not translated (#197). One language may share a word with English
    /// ("Details", "Online") or keep the original site's English, so only all of them together count.</summary>
    [Fact]
    public void NoKeyIsLeftInEnglishInEveryTranslation()
    {
        var en = Read(CultureInfo.InvariantCulture);
        var translations = Others.Select(Read).ToList();
        var bad = en.Where(p => !NamesKeptInEnglish.Contains(p.Key) && Regex.IsMatch(p.Value, "[A-Za-z]{3}")
                && translations.All(tr => tr.GetValueOrDefault(p.Key) == p.Value))
            .Select(p => p.Key).ToList();
        Assert.True(bad.Count == 0, "FAIL left in English in every translation: " + string.Join(", ", bad));
    }

    static readonly ResourceManager Rm = new("Resurgence.Ui.Strings", typeof(Resurgence.Ui.Strings).Assembly);
    static readonly CultureInfo[] Others = [.. Resurgence.Ui.Languages.All.Where(l => l.Code != "en").Select(l => CultureInfo.GetCultureInfo(l.Code))];

    static Dictionary<string, string> Read(CultureInfo c) =>
        (Rm.GetResourceSet(c, true, false) ?? throw new Exception($"FAIL no resources for '{c.Name}'")).Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
}
