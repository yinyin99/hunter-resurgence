using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Resurgence.Services;

/// <summary>The signed-in player's items from the community server (<see cref="ServerAccountApi.Inventory"/>). Without a session, or when the server does not answer, there are none.</summary>
/// <param name="http">Base address is the server, <c>UseCookies</c> off.</param>
public sealed class ServerInventoryService(HttpClient http, ServerCookie cookie, ILogger<ServerInventoryService> log) : IInventoryService
{
    public async Task<IReadOnlyList<InventoryItem>> ItemsAsync()
    {
        if (cookie.Value is null) return [];
        try
        {
            using var request = cookie.Request(HttpMethod.Get, ServerAccountApi.Inventory);
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<ServerInventory>() is { Items: { } items })
                return [.. items.Select(i => new InventoryItem(i.Id, i.Name, i.Variant, i.Category, i.CategoryName, i.Units, i.Equipped, i.Expires))];
            log.LogWarning("The server answered {Path} with {Status}: no items", ServerAccountApi.Inventory, (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("The server did not answer {Path} ({Reason}): no items", ServerAccountApi.Inventory, e.Message);
        }
        return [];
    }
}
