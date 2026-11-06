namespace Resurgence.Services;

/// <summary>What the community server serves under <c>/catalog/</c> on the website's host (docs/server.md): the same for everyone, so no cookie. The browser never calls it.</summary>
public static class ServerCatalogApi
{
    /// <summary>GET: 200 <see cref="ServerStore"/>, or 404 when the server holds no catalogue.</summary>
    public const string Store = "/catalog/store";
}

/// <summary>The JSON of <see cref="ServerCatalogApi.Store"/>. A category's <c>Key</c> is the store address part, a top-level one the key of its first sub-category.</summary>
public sealed record ServerStore(ServerCategory[] Categories, ServerItem[] Items, ServerBundle[] Bundles);

public sealed record ServerCategory(string Key, string Name, ServerCategory[]? Children = null);

/// <summary><see cref="Ems"/> is today's price, <see cref="Was"/> the price before an active discount (null without one); <see cref="Category"/> the key of its sub-category.</summary>
public sealed record ServerItem(int Id, string Name, string Variant, string Description, string Category, int Ems, int? Was, int Gms, bool Buyable, int Capacity);

public sealed record ServerBundle(int Id, string Name, string Description, int Price, ServerBundleItem[] Items);

public sealed record ServerBundleItem(int Id, string Name);
