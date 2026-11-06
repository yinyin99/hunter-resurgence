using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Ui;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class BankPageTests : UiContext
{
    [Theory]
    [InlineData(true, typeof(BankModal))]
    [InlineData(false, null)]
    public void TheBankDialogOpensOnlyWhenPurchasesAreOn(bool purchases, Type? opened)
    {
        Services.GetRequiredService<HostMode>().Purchases = purchases;

        Render<Bank>(p => p.Add(b => b.Tab, "ems"));

        Assert.Equal(opened, Services.GetRequiredService<ModalService>().Type);
    }
}
