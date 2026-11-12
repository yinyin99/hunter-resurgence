using Resurgence.Services;

namespace Resurgence.Tests;

/// <summary>A signed-in player who owns nothing, for the tests that build a <see cref="ServerStoreService"/>.</summary>
sealed class NoItems : IInventoryService
{
    public Task<IReadOnlyList<InventoryItem>> ItemsAsync() => Task.FromResult<IReadOnlyList<InventoryItem>>([]);
}
