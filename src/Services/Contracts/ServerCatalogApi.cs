namespace Resurgence.Services;

/// <summary>What the community server serves under <c>/catalog/</c> on the website's host (docs/server.md): the same for everyone. The store, its items and the games need the session cookie (401 <c>signed_out</c> without one),
/// the reserves do not. The website's server calls it over <c>SERVER_URL</c>, the launcher on the public domain.</summary>
public static class ServerCatalogApi
{
    /// <summary>GET: 200 <see cref="ServerStore"/>, or 404 when the server holds no catalogue.</summary>
    public const string Store = "/catalog/store";
    /// <summary>GET: 200 <see cref="ServerLaunchCatalogue"/>, or 404 when the server holds no reserves.</summary>
    public const string Reserves = "/catalog/reserves";
    /// <summary>GET: this path and an item's id: 200 <see cref="ServerItemPage"/> of an item the store lists, or 404.</summary>
    public const string Item = "/catalog/item/";
    /// <summary>GET: 200 the open multiplayer games, a JSON array of <see cref="ServerGame"/> (empty without one).</summary>
    public const string Games = "/catalog/games";
    /// <summary>GET with the cookie: this path is a game's <see cref="ServerGame.Id"/> and <c>/placed</c>: 200 <see cref="ServerPlaced"/>, the host's placed items, only for a player the game would let in (403 otherwise, 404 for a game that is gone).</summary>
    public static string GamePlaced(long id) => $"{Games}/{id}/placed";
    /// <summary>The header that carries the entered password of a game with one to <see cref="GamePlaced"/>, percent-encoded.</summary>
    public const string GamePassword = "X-Game-Password";
}

/// <summary>What the launcher starts a hunt from: <c>POST</c> with the cookie and <c>X-Requested-With</c> to <see cref="Launch"/> a form of <c>reserve</c> and <c>lodge</c> (a <see cref="ServerStart"/> id)
/// or <c>tent</c> (the id of a tent of <see cref="ServerAccountApi.Placed"/>), <c>time</c> (minutes) and <c>loadout</c> (an id, 0 for the last equipment), or of <c>tutorial</c>: 200 <see cref="ServerLaunch"/>, 400 for a refused start, 401 when signed out.
/// A multiplayer hunt adds <c>mp=host</c> with <c>name</c>, <c>description</c>, <c>language</c> (one of the launcher's interface language codes), <c>public_game</c> (1 anyone, 0 friends only), <c>free_players</c> (1), <c>hs_min</c> and <c>hs_max</c> (only when the Hunter Score is limited),
/// <c>password</c> (only when set) and <c>max_players</c>; or <c>mp=join</c> with <c>network_id</c> (<see cref="ServerGame.Id"/>), <c>lodge</c> or <c>tent</c>, <c>loadout</c> and <c>password</c> (only when set; no reserve, no time: the game has them).
/// A refused join is 400 <see cref="ServerRefusal"/> with <c>password</c>, <c>full</c>, <c>not_allowed</c>, <c>host_not_ready</c>, <c>host_network</c> or <c>banned</c>.</summary>
public static class ServerLaunchApi
{
    public const string Launch = "/auth/launch";
}

public sealed record ServerLaunch(string Url);

public sealed record ServerRefusal(string? Error);

/// <summary>The 403 of <see cref="ServerCatalogApi.GamePlaced"/>: <c>Why</c> is the join's reason (<c>own</c>, <c>banned</c>, <c>friends</c>, <c>hunterscore</c>, <c>starting</c>, <c>network</c>, <c>password</c>, <c>full</c>).</summary>
public sealed record ServerPlacedRefusal(string? Why);

/// <summary>A game of <see cref="ServerCatalogApi.Games"/>, in the shape of the game's own list (more fields are not read): <c>Id</c> is the network id the join takes, <c>Max</c> the game's size,
/// <c>Min</c> and <c>MaxScore</c> the Hunter Score range (null without one; a host can set any number, so they are wide), <c>Country</c> is missing on a community server, <c>Why</c> the server's reason the signed-in player cannot join
/// (<c>own</c>, <c>friends</c>, <c>hunterscore</c>, <c>starting</c>, <c>password</c>, <c>full</c>; null when nothing stops the player; missing on an older server), <c>Language</c> the host's language code (<c>fr</c>, <c>pt-BR</c>; null for none and on an older server).</summary>
public sealed record ServerGame(
    long Id, string? Name, string? Handle, string? Description, bool Password,
    [property: System.Text.Json.Serialization.JsonPropertyName("hs_min")] long? Min,
    [property: System.Text.Json.Serialization.JsonPropertyName("hs_max")] long? MaxScore,
    [property: System.Text.Json.Serialization.JsonPropertyName("max_players")] long Max,
    [property: System.Text.Json.Serialization.JsonPropertyName("start_ts")] long Started,
    [property: System.Text.Json.Serialization.JsonPropertyName("country_code")] string? Country,
    ServerGamePlayer[]? Clients, ServerGameReserve? Reserve, string? Why = null, string? Language = null);

/// <param name="MapX">The player's place as percent of the reserve's map picture from the left (<c>mapX</c>), <paramref name="MapY"/> from the top; null before the host's heartbeat gave a position, and for a game the player may not join.</param>
public sealed record ServerGamePlayer(string? Handle, long Hunterscore, string? Avatar, double? MapX = null, double? MapY = null);

public sealed record ServerGameReserve(long Id);

/// <summary>The JSON of <see cref="ServerCatalogApi.Reserves"/>.</summary>
public sealed record ServerLaunchCatalogue(ServerReserve[] Reserves, ServerTutorial[] Tutorials);

public sealed record ServerReserve(int Id, string Name, ServerStart[] Starts);

/// <param name="Kind">lodge, campsite, tower or interest.</param>
/// <param name="X">Percent of the reserve's map picture, from the left (two decimals).</param>
/// <param name="Y">Percent of the reserve's map picture, from the top (two decimals).</param>
public sealed record ServerStart(int Id, string Kind, string Name, double X, double Y);

public sealed record ServerTutorial(int Id, string Name, int Reserve);

/// <summary>The JSON of <see cref="ServerCatalogApi.Store"/>. A category's <c>Key</c> is the store address part, a top-level one the key of its first sub-category.</summary>
public sealed record ServerStore(ServerCategory[] Categories, ServerItem[] Items, ServerBundle[] Bundles);

public sealed record ServerCategory(string Key, string Name, ServerCategory[]? Children = null);

/// <summary><see cref="Ems"/> and <see cref="Gms"/> are today's prices (the discount of the day taken off), <see cref="Was"/> the em$ price before an active discount (null without one); <see cref="Category"/> the key of its sub-category; <see cref="Unlimited"/> an ammunition every player has all of (not sold); <see cref="Fuel"/> a vehicle fuel while the server gives every player unlimited fuel (not sold); <see cref="Limit"/> the most copies a player owns, 0 for no limit.</summary>
public sealed record ServerItem(int Id, string Name, string Variant, string Description, string Category, int Ems, int? Was, int Gms, bool Buyable, bool Unlimited = false, bool Fuel = false, int Limit = 0);

/// <summary><see cref="Buyable"/>: the server sells it (a price of at least 1, only items of its catalogue).</summary>
public sealed record ServerBundle(int Id, string Name, string Description, int Price, bool Buyable, ServerBundleItem[] Items);

public sealed record ServerBundleItem(int Id, string Name);

/// <summary>A bundle of <see cref="ServerAccountApi.Bundles"/>, as it costs the signed-in player now: <see cref="Price"/> is the em$ the server charges (the list price less the share of the items the player already owns up to their limit),
/// <see cref="Owned"/> of its <see cref="Of"/> items are not given again, <see cref="Buyable"/> is false when the store does not sell it or it would give nothing.</summary>
public sealed record ServerBundleOffer(int Id, int Price, int Owned, int Of, bool Buyable);

public sealed record ServerBundleOffers(ServerBundleOffer[] Bundles);

/// <summary>The JSON of <see cref="ServerCatalogApi.Item"/>: what an item's page shows beyond the list. <see cref="Capacity"/> is the inventory weight as the store shows it, with no unit (0 for none); <see cref="Fits"/> what fits it, under
/// its category's title; <see cref="Ethical"/> the permitted species of a weapon's ammunition or of an ammunition, by the rule the server judges kills by.</summary>
public sealed record ServerItemPage(double Capacity, ServerContents? Contents, ServerFits[]? Fits, ServerEthical? Ethical);

/// <summary>An item sold by the pack: <c>10 cartridges</c>.</summary>
public sealed record ServerContents(int Count, string Name);

public sealed record ServerFits(string Title, string Key, ServerFit[] Items);

public sealed record ServerFit(int Id, string Name, string Variant);

/// <summary><see cref="Species"/> have one flag per name of <see cref="Ammo"/>.</summary>
public sealed record ServerEthical(ServerFit[] Ammo, ServerEthicalSpecies[] Species);

public sealed record ServerEthicalSpecies(int Id, string Name, bool[] Permitted);
