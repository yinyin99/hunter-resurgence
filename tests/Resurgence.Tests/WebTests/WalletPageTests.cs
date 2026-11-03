using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class WalletPageTests : UiContext
{
    [Fact]
    public void TwoClicksOnShowMoreWithinTwoSecondsFetchOnePage()
    {
        var state = new MockWalletState();
        Services.AddSingleton<IWalletService>(new MockWalletService(state, "wallet:many"));
        var cut = Render<Wallet>(p => p.Add(w => w.Kind, "ems"));
        var rows = () => cut.FindAll("table.entries tbody tr").Count;
        Assert.Equal(IWalletService.PageSize, rows());

        var more = cut.Find(".load-more-button");
        more.Click();
        more.Click();

        Assert.Equal(2 * IWalletService.PageSize, rows());
    }
}
