using Resurgence.Ui;

namespace Resurgence.Launcher;

// ponytail: in-memory sample state shared by pages and the status strip; real wallet/server come from the community server later
public sealed class LauncherState
{
    public ServerInfo SelectedServer { get; private set; } = SampleData.Servers[0];
    public int Wallet { get; private set; } = SampleData.WalletCredits;
    public HashSet<string> OwnedItems { get; } = SampleData.StoreItems.Where(i => i.Owned).Select(i => i.Name).ToHashSet();
    public event Action? Changed;

    public void SelectServer(ServerInfo server)
    {
        SelectedServer = server;
        Changed?.Invoke();
    }

    public void Buy(StoreItem item)
    {
        if (OwnedItems.Contains(item.Name) || item.Price > Wallet) return;
        Wallet -= item.Price;
        OwnedItems.Add(item.Name);
        Changed?.Invoke();
    }
}
