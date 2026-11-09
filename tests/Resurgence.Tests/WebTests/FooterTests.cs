using System.Globalization;
using Bunit;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

public sealed class FooterTests : UiContext
{
    /// <summary>The attribution README's AGPL-3.0 section 7(b) term requires: the same text and link in every language.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void TheCreditIsShownWithItsLink(string language)
    {
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new(language);
            var credit = Render<Footer>().Find("a.credit");
            Assert.Equal("Hunter Resurgence © yinyin99", credit.TextContent);
            Assert.Equal("https://github.com/yinyin99/hunter-resurgence", credit.GetAttribute("href"));
        }
        finally { CultureInfo.CurrentUICulture = ui; }
    }
}
