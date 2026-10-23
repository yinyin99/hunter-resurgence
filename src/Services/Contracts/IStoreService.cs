namespace Resurgence.Services;

public sealed record StoreItem(int Id, string Name, string Category, int Price);

public interface IStoreService
{
    /// <param name="category">all, sales, bundles or a category id</param>
    Task<IReadOnlyList<StoreItem>> ItemsAsync(string category);
    Task<StoreItem?> ItemAsync(int id);
}
