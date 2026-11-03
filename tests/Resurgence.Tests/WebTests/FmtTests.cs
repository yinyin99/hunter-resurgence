using System.Globalization;
using Resurgence.Ui;

namespace Resurgence.Tests.WebTests;

/// <summary>Statistics use a point in every language; sizes and times use the unit texts of the UI language.</summary>
public class FmtTests
{
    // the culture is set for this thread only and put back
    static void In(string culture, string ui, Action a)
    {
        var (c, u) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (CultureInfo.GetCultureInfo(culture), CultureInfo.GetCultureInfo(ui));
        try { a(); }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (c, u); }
    }

    [Fact]
    public void NumbersKeepTheirPointInAComaCulture() => In("fr-FR", "en", () =>
    {
        Assert.Equal("79.31%", Fmt.Pct(79.314));
        Assert.Equal("12.3456", Fmt.Score(12.3456));
        Assert.Equal("124.53m (408.56ft)", Fmt.Metres(124.53));
        Assert.Equal("508.8m (1669.3ft)", Fmt.Board("range", 508.8));
        Assert.Equal("13", Fmt.Board("hunterscore", 12.7));
        Assert.Equal("12.7000", Fmt.Board("other", 12.7));
    });

    [Theory]
    [InlineData(1, "Jul 1st")]
    [InlineData(2, "Jul 2nd")]
    [InlineData(3, "Jul 3rd")]
    [InlineData(4, "Jul 4th")]
    [InlineData(11, "Jul 11th")]
    [InlineData(12, "Jul 12th")]
    [InlineData(13, "Jul 13th")]
    [InlineData(21, "Jul 21st")]
    [InlineData(22, "Jul 22nd")]
    [InlineData(23, "Jul 23rd")]
    public void MonthDayIsEnglishWithItsSuffix(int day, string expected) => In("fr-FR", "fr", () => Assert.Equal(expected, Fmt.MonthDay(new DateTime(2026, 7, day))));

    [Fact]
    public void DaysLeftIsLessThanOneUpToADay()
    {
        Assert.Equal("< 1", Fmt.DaysLeft(0.5));
        Assert.Equal("2", Fmt.DaysLeft(2.4));
        Assert.Equal("0", Fmt.DaysLeft(0));
    }

    [Fact]
    public void SizesAndTimesUseTheUnitTextsOfTheLanguage() => In("en", "en", () =>
    {
        var l = TestLoc.Make();
        Assert.Equal("1 KB", l.Size(1536));
        Assert.Equal("9m 05s", l.Elapsed(9 * 60 + 5));
        Assert.Equal("1d 1h 1m 01s", l.Duration(90061));
        Assert.Equal("a few seconds ago", l.Ago(TimeSpan.FromSeconds(30)));
        Assert.Equal("2 minutes ago", l.Ago(TimeSpan.FromSeconds(100)));
    });
}
