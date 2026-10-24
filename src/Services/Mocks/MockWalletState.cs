namespace Resurgence.Services.Mocks;

/// <summary>
/// What one mock account owns: the balances, the items and memberships bought, the wallet rows and bank payments they made.
/// The wallet, store and bank mocks of one circuit share it, so a purchase shows in every one of them.
/// </summary>
public sealed class MockWalletState
{
    public int Ems { get; set; } = 100;
    public int Gms { get; set; } = 1000;
    public int EmsSpent { get; private set; }
    public int GmsSpent { get; private set; }
    public string Membership { get; set; } = "Basic";
    public DateTime? MembershipEnds { get; set; } = DateTime.UtcNow.Date.AddDays(62);

    /// <summary>Newest first, in front of the rows the wallet mock generates.</summary>
    public List<WalletEntry> EmRows { get; } = [];
    public List<WalletEntry> GmRows { get; } = [];
    public List<BankPayment> Payments { get; } = [];
    public HashSet<string> UsedCodes { get; } = [];

    readonly Dictionary<int, int> bought = [];

    public int Bought(int itemId) => bought.GetValueOrDefault(itemId);

    public void Add(int itemId, int count) => bought[itemId] = Bought(itemId) + count;

    /// <summary>Takes <paramref name="amount"/> from the balance and notes the purchase; false when the balance is too low.</summary>
    public bool Spend(bool gms, int amount, string item)
    {
        var before = gms ? Gms : Ems;
        if (amount > before) return false;
        var row = new WalletEntry(DateTime.UtcNow, amount, before, before - amount, item, "In-game payment.");
        if (gms) (Gms, GmsSpent) = (before - amount, GmsSpent + amount); else (Ems, EmsSpent) = (before - amount, EmsSpent + amount);
        (gms ? GmRows : EmRows).Insert(0, row);
        return true;
    }
}
