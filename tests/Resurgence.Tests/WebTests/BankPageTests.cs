using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Ui;
using Resurgence.Ui.Modals;
using Resurgence.Ui.Pages;

namespace Resurgence.Tests.WebTests;

public sealed class BankPageTests : UiContext
{
    [Theory]
    [InlineData(false, typeof(BankModal))]
    [InlineData(true, null)]
    public void TheBankDialogOpensOnlyOffTheLiveSite(bool live, Type? opened)
    {
        Services.GetRequiredService<HostMode>().Live = live;

        Render<Bank>(p => p.Add(b => b.Tab, "ems"));

        Assert.Equal(opened, Services.GetRequiredService<ModalService>().Type);
    }
}
