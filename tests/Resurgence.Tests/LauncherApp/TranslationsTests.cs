using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;

namespace Resurgence.Tests.LauncherApp;

public class TranslationsTests
{
    /// <summary>Keys whose English text is right in every language: names.</summary>
    static readonly HashSet<string> NamesKeptInEnglish = ["App.Name", "Dialog.Tutorial.Msaa", "Board.LaneBandit", "Board.BustThrough", "Store.Sub.57", "Store.Cat.76", "Store.Cat.104", "Store.Cat.111"];

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
