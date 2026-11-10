using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The balances of the signed-in player (the status bar, <see cref="ServerSessionApi.Me"/>) and the wallet page's totals and em$ and gm$ history
/// (<see cref="ServerAccountApi.Wallet"/>) from the community server; the server knows no premium service days, so those pills are not shown. The bank and the
/// membership are still the mock's; the website hides the bank (<c>HostMode.Live</c>). The store buys from the server (<see cref="ServerStoreService"/>).</summary>
public sealed class ServerWalletService(HttpClient http, ServerCookie cookie, MockWalletService mock, ILogger<ServerWalletService> log) : IWalletService
{
    public async Task<Balances> BalancesAsync() =>
        await GetAsync<ServerAccount>(ServerSessionApi.Me) is { } me ? new Balances(me.Ems, me.Gms, null, null) : new Balances(0, 0, null, null);

    public async Task<WalletSummary> SummaryAsync()
    {
        var (balances, spent) = (await BalancesAsync(), await GetAsync<ServerSpent>($"{ServerAccountApi.Wallet}/spent"));
        return new(balances.Ems, balances.Gms, (decimal)(spent?.Spent ?? 0), (decimal)(spent?.SilverSpent ?? 0));
    }

    public async Task<IReadOnlyList<WalletEntry>> EntriesAsync(bool gms, int offset) =>
        [.. (await GetAsync<ServerWalletRow[]>($"{ServerAccountApi.Wallet}/{(gms ? "gms" : "ems")}?offset={offset}"))?.Select(ToEntry) ?? []];

    public Task<AccountMembership> MembershipAsync() => mock.MembershipAsync();
    public Task<IReadOnlyList<BankPayment>> BankAsync(int offset) => mock.BankAsync(offset);

    static int Whole(double d) => (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue);

    // a bought item is its name, with the count when more than one; a reward has none
    static WalletEntry ToEntry(ServerWalletRow r)
    {
        var names = string.Join(", ", (r.Items ?? []).Select(i => i.Name).Where(n => !string.IsNullOrEmpty(n)));
        return new(DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(r.Created, 0, 253402300799)).UtcDateTime, Whole(r.Amount), Whole(r.PreBalance), Whole(r.PostBalance),
            r.Qty > 1 && names.Length > 0 ? $"{r.Qty} × {names}" : names, r.Summary ?? "");
    }

    async Task<T?> GetAsync<T>(string path) where T : class
    {
        if (cookie.Value is null) return null;
        try
        {
            using var request = cookie.Request(HttpMethod.Get, path);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return await response.Content.ReadFromJsonAsync<T>();
            log.LogWarning("The server answered {Path} with {Status}: nothing of the wallet", path.Split('?')[0], (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): nothing of the wallet", path.Split('?')[0], e.Message);
        }
        return null;
    }
}
