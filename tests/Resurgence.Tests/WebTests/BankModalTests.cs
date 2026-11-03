using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Resurgence.Services;
using Resurgence.Services.Mocks;
using Resurgence.Ui.Modals;

namespace Resurgence.Tests.WebTests;

public sealed class BankModalTests : UiContext
{
    /// <summary>A bank whose payment stays open until the test lets it end.</summary>
    sealed class SlowBank : IBankService
    {
        readonly MockBankService inner = new(new MockWalletState());
        public readonly TaskCompletionSource Payment = new();
        public int Buys;
        public Task<BankCatalog> CatalogAsync() => inner.CatalogAsync();
        public Task<BankCode?> CheckCodeAsync(string code) => inner.CheckCodeAsync(code);
        public Task<Result> RedeemAsync(string code) => inner.RedeemAsync(code);
        public async Task<Result> BuyAsync(string kind, int index, bool gift = false, string? code = null) { Buys++; await Payment.Task; return Result.Success; }
        public Task<BankTransaction> VerifyTransactionAsync(string reference) => inner.VerifyTransactionAsync(reference);
        public Task<string?> OfferwallUrlAsync() => inner.OfferwallUrlAsync();
    }

    [Fact]
    public void AClickWhileAPaymentRunsStartsNoSecondPayment()
    {
        var bank = new SlowBank();
        Services.AddSingleton<IBankService>(bank);
        var cut = Render<BankModal>(p => p.Add(b => b.Tab, "ems"));
        var buy = cut.Find(".pack .buy");

        buy.Click();
        Assert.True(cut.Find(".pack .buy").HasAttribute("disabled"));
        cut.Find(".pack .buy").Click();

        Assert.Equal(1, bank.Buys);
        bank.Payment.SetResult();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".done")));
        Assert.False(cut.Find(".pack .buy").HasAttribute("disabled"));
    }
}
