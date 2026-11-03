using System.Globalization;
using Resurgence.Ui;

namespace Resurgence.Tests.WebTests;

public class TermsTests
{
    [Fact]
    public void AnEnglishTermIsTranslatedAndOthersStayAsTheyAre()
    {
        var old = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
        try
        {
            var l = TestLoc.Make();
            Assert.Equal("Bouquetin des Alpes", l.Term("Alpine Ibex"));
            Assert.Equal("Bouquetin des Alpes", l.Term("species", 19, "X"));
            Assert.Equal("Not a term", l.Term("Not a term"));
            Assert.Equal("X", l.Term("species", 999999, "X"));
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }
}
