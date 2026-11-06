using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Resurgence.Services.Mocks;

namespace Resurgence.Services;

/// <summary>The balances of the signed-in player (the status bar) from the community server (<see cref="ServerSessionApi.Me"/>); the server knows no
/// premium service days, so those pills are not shown. The wallet page and the bank are still the mock's; the website hides them (<c>HostMode.Live</c>). The store buys from the server (<see cref="ServerStoreService"/>).</summary>
public sealed class ServerWalletService(HttpClient http, ServerCookie cookie, MockWalletService mock, ILogger<ServerWalletService> log) : IWalletService
{
    public async Task<Balances> BalancesAsync()
    {
        if (cookie.Value is not null)
        {
            try
            {
                using var request = cookie.Request(HttpMethod.Get, ServerSessionApi.Me);
                using var response = await http.SendAsync(request);
                if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerAccount>() is { } me)
                    return new Balances(me.Ems, me.Gms, null, null);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                log.LogWarning("The server did not answer {Path} ({Reason}): no balances", ServerSessionApi.Me, e.Message);
            }
        }
        return new Balances(0, 0, null, null);
    }

    public Task<AccountMembership> MembershipAsync() => mock.MembershipAsync();
    public Task<WalletSummary> SummaryAsync() => mock.SummaryAsync();
    public Task<IReadOnlyList<BankPayment>> BankAsync(int offset) => mock.BankAsync(offset);
    public Task<IReadOnlyList<WalletEntry>> EntriesAsync(bool gms, int offset) => mock.EntriesAsync(gms, offset);
}
