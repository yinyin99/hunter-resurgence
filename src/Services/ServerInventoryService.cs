using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The signed-in player's items from the community server (<see cref="ServerAccountApi.Inventory"/>), and their sale back to the store (<see cref="ServerAccountApi.Sell"/>). Without a session, or when the server does not answer, there are none.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerInventoryService(HttpClient http, ServerCookie cookie, ILogger<ServerInventoryService> log) : IInventoryService
{
    public async Task<IReadOnlyList<InventoryItem>> ItemsAsync()
    {
        if (cookie.Value is not { } sent) return [];
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.Inventory);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerInventory>() is { Items: { } items })
                return [.. items.Select(i => new InventoryItem(i.Id, i.Name, i.Variant, i.Category, i.CategoryName, i.Units, i.Equipped, i.Expires, i.Guid, i.Sell is { } s ? new SellOffer(s.Currency, s.Refund) : null))];
            log.LogWarning("The server answered {Path} with {Status}: no items", ServerAccountApi.Inventory, (int)response.StatusCode);
            cookie.Refused(response, sent);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no items", ServerAccountApi.Inventory, e.Message);
        }
        return [];
    }

    public async Task<Result> SellAsync(long guid)
    {
        if (cookie.Value is null) return Result.Fail("Inv.SellFailed");
        try
        {
            using var request = cookie.Request(HttpMethod.Post, ServerAccountApi.Sell);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Content = new FormUrlEncodedContent([KeyValuePair.Create("guid", $"{guid}")]);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return Result.Success;
            log.LogWarning("The server refused {Path} for a copy ({Status})", ServerAccountApi.Sell, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason})", ServerAccountApi.Sell, e.Message);
        }
        return Result.Fail("Inv.SellFailed");
    }
}
