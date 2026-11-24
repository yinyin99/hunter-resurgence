using System.Globalization;
using Backup;

namespace Resurgence.Tests.BackupTool;

public class TranslationsTests
{
    /// <summary>The key and placeholder parity of the language files is checked by tools/checks/translations.mjs; it cannot see a key that no file holds: every key the engine and the window ask for exists, non-empty, in every language.</summary>
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
