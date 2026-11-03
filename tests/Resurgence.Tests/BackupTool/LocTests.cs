using System.Globalization;
using Backup;

namespace Resurgence.Tests.BackupTool;

/// <summary>The backup tool's language choice: the saved code, the Windows language (pt maps to pt-BR), English as the fallback. Loc is process-global: this runs alone.</summary>
[Collection("ProcessState")]
public class LocTests
{
    static void With(string? saved, string uiCulture, Action check)
    {
        using var t = new TempDir("backup-loc-");
        var path = Path.Combine(t.Path, "language.txt");
        if (saved != null) File.WriteAllText(path, saved + "\n");
        var old = Thread.CurrentThread.CurrentUICulture;
        try
        {
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(uiCulture);
            Loc.Init(path);
            check();
        }
        finally { Thread.CurrentThread.CurrentUICulture = old; Loc.Set("en"); }
    }

    [Fact]
    public void ASavedLanguageWinsOverTheWindowsOne() => With("de", "ja", () => Assert.Equal("de", Loc.Code));

    [Fact]
    public void WithoutAFileTheWindowsLanguageIsUsedAndPortugueseIsMapped() => With(null, "pt-BR", () => Assert.Equal("pt", Loc.Code));

    [Fact]
    public void AnUnknownSavedCodeFallsBackToTheWindowsLanguageThenEnglish() => With("xx", "ja", () => Assert.Equal("en", Loc.Code));

    [Fact]
    public void TheLanguageChangesTheWords()
    {
        try
        {
            Loc.Set("en");
            var en = Loc.S("Start");
            Loc.Set("de");
            Assert.NotEqual(en, Loc.S("Start"));
        }
        finally { Loc.Set("en"); }
    }
}
