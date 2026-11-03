using Resurgence.Services.Mocks;

namespace Resurgence.Tests.ServicesTests;

/// <summary>PL25: the public website runs on these mocks; a purchase shows in the wallet, store and bank alike.</summary>
public class MockWalletTests
{
    [Fact]
    public async Task BuyingAnItemSpendsEmsAndShowsInTheWalletRows()
    {
        var state = new MockWalletState();
        var wallet = new MockWalletService(state);
        Assert.True((await new MockStoreService(state).BuyAsync(1227, false, false)).Ok);
        Assert.Equal((98, 2, 1), (state.Ems, state.EmsSpent, state.Bought(1227)));
        var row = (await wallet.EntriesAsync(false, 0))[0];
        Assert.Equal((2, 100, 98), (row.Amount, row.Before, row.After));
        Assert.Equal(98, (await wallet.BalancesAsync()).Ems);
    }

    [Fact]
    public async Task BuyingWithoutEnoughEmsFailsAndChangesNothing()
    {
        var state = new MockWalletState { Ems = 0 };
        var r = await new MockStoreService(state).BuyAsync(1227, false, false);
        Assert.Equal("Store.Oops", r.Error);
        Assert.Equal((0, 0, 0), (state.Ems, state.EmsSpent, state.Bought(1227)));
        Assert.Empty(state.EmRows);
    }

    [Fact]
    public async Task AGiftCodeIsRedeemedOnce()
    {
        var state = new MockWalletState();
        var bank = new MockBankService(state);
        Assert.True((await bank.RedeemAsync("gift")).Ok);
        Assert.Equal(600, state.Ems);
        Assert.Equal("Bank.Invalid", (await bank.RedeemAsync("gift")).Error);
        Assert.Equal(600, state.Ems);
    }

    [Fact]
    public async Task ADiscountCodeTakesItsPercentOffThePayment()
    {
        var state = new MockWalletState();
        var bank = new MockBankService(state);
        Assert.Equal(20, (await bank.CheckCodeAsync("SAVE20"))!.Percent);
        Assert.True((await bank.BuyAsync("ems", 0, code: "SAVE20")).Ok);
        Assert.Equal(3.99m, state.Payments[0].Amount);
    }
}
