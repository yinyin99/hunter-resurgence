using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Ui;
using Resurgence.Ui.Shell;

namespace Resurgence.Tests.WebTests;

public sealed class StatusBarTests : UiContext
{
    [Fact]
    public void TheLauncherLabelIsNeverTranslated()
    {
        Services.GetRequiredService<HostMode>().Launcher = true;
        var ui = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new("fr");
            var cut = Render<StatusBar>(p => p.Add(b => b.Minimal, true));
            Assert.Equal("Launcher", cut.Find(".launcher-label").TextContent);
        }
        finally { CultureInfo.CurrentUICulture = ui; }
    }

    [Theory]
    [InlineData(false, 1, 3)]
    [InlineData(true, 1, 3)]
    public void OnTheLiveSiteTheWalletAndMessagesLinksAndTheFriendsAndBalancesStay(bool live, int links, int walletLinks)
    {
        Services.GetRequiredService<HostMode>().Live = live;
        var cut = Render<StatusBar>(p => p.Add(b => b.Session, new Resurgence.Services.Session("Fox", 5)));
        Assert.Equal(links, cut.FindAll("a[href='/messages']").Count);
        Assert.Single(cut.FindAll("button.friends"));
        Assert.Equal(walletLinks, cut.FindAll("a[href^='/wallet']").Count);
        Assert.Single(cut.FindAll(".pill.ems"));
    }

    // a purchase's dialog closes after it: the bar shows the new balance at once, not at the next poll
    [Fact]
    public void TheBalancesAreReadAgainWhenADialogCloses()
    {
        var cut = Render<StatusBar>(p => p.Add(b => b.Session, new Resurgence.Services.Session("Fox", 5)));
        var state = Services.GetRequiredService<Resurgence.Services.Mocks.MockWalletState>();
        Assert.Equal($"{state.Ems}", cut.Find(".pill.ems").TextContent);

        state.Ems -= 30;
        Services.GetRequiredService<Resurgence.Ui.Modals.ModalService>().Close();

        cut.WaitForAssertion(() => Assert.Equal($"{state.Ems}", cut.Find(".pill.ems").TextContent));
    }
}
