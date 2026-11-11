namespace Resurgence.Services;

/// <summary>What the community server serves under <c>/catalog/</c> on the website's host (docs/server.md): the same for everyone, so no cookie. The website's server calls it over <c>SERVER_URL</c>, the launcher on the public domain.</summary>
public static class ServerCatalogApi
{
    /// <summary>GET: 200 <see cref="ServerStore"/>, or 404 when the server holds no catalogue.</summary>
    public const string Store = "/catalog/store";
    /// <summary>GET: 200 <see cref="ServerLaunchCatalogue"/>, or 404 when the server holds no reserves.</summary>
    public const string Reserves = "/catalog/reserves";
}

/// <summary>What the launcher starts a hunt from: <c>POST</c> with the cookie and <c>X-Requested-With</c> to <see cref="Launch"/> a form of <c>reserve</c> and <c>lodge</c> (a <see cref="ServerStart"/> id),
/// <c>time</c> (minutes) and <c>loadout</c> (an id, 0 for the last equipment), or of <c>tutorial</c>: 200 <see cref="ServerLaunch"/>, 400 for a refused start, 401 when signed out.</summary>
public static class ServerLaunchApi
{
    public const string Launch = "/auth/launch";
}

public sealed record ServerLaunch(string Url);

/// <summary>The JSON of <see cref="ServerCatalogApi.Reserves"/>.</summary>
public sealed record ServerLaunchCatalogue(ServerReserve[] Reserves, ServerTutorial[] Tutorials);

public sealed record ServerReserve(int Id, string Name, ServerStart[] Starts);

/// <param name="Kind">lodge, campsite, tower or interest.</param>
/// <param name="X">Percent of the reserve's map, from the left.</param>
/// <param name="Y">Percent of the reserve's map, from the top.</param>
public sealed record ServerStart(int Id, string Kind, string Name, int X, int Y);

public sealed record ServerTutorial(int Id, string Name, int Reserve);

/// <summary>The JSON of <see cref="ServerCatalogApi.Store"/>. A category's <c>Key</c> is the store address part, a top-level one the key of its first sub-category.</summary>
public sealed record ServerStore(ServerCategory[] Categories, ServerItem[] Items, ServerBundle[] Bundles);

public sealed record ServerCategory(string Key, string Name, ServerCategory[]? Children = null);

/// <summary><see cref="Ems"/> and <see cref="Gms"/> are today's prices (the discount of the day taken off), <see cref="Was"/> the em$ price before an active discount (null without one); <see cref="Category"/> the key of its sub-category.</summary>
public sealed record ServerItem(int Id, string Name, string Variant, string Description, string Category, int Ems, int? Was, int Gms, bool Buyable);

/// <summary><see cref="Buyable"/>: the server sells it (a price of at least 1, only items of its catalogue).</summary>
public sealed record ServerBundle(int Id, string Name, string Description, int Price, bool Buyable, ServerBundleItem[] Items);

public sealed record ServerBundleItem(int Id, string Name);
