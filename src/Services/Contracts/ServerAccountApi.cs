namespace Resurgence.Services;

/// <summary>What the community server serves under <c>/account/</c> to the website's own server (the reverse proxy routes only <c>/account/restore/*</c>, for the backup tool): the session cookie of <see cref="ServerSessionApi"/> authenticates all of them.</summary>
public static class ServerAccountApi
{
    /// <summary>GET: <c>{restoredUserId, review: [{ts, note}]}</c>. POST with <c>X-Requested-With</c> to <c>/start</c>: <c>{status: started, id}</c>, then each part of at most
    /// <see cref="PartBytes"/> to <c>/&lt;id&gt;/&lt;part&gt;</c> (<c>{status: received}</c>), then <c>/&lt;id&gt;/finish</c>: <c>{status, message, report}</c>.</summary>
    public const string Restore = "/account/restore";
    /// <summary>POST with <c>X-Requested-With</c> and the form <c>confirm=delete</c>: deletes the account and all its data.</summary>
    public const string Delete = "/account/delete";
    /// <summary>POST with <c>X-Requested-With</c> and the form <c>id</c>, <c>count</c>, <c>bundle=1</c> for a bundle: buys with em$ at the store's price; 200 <c>{status, ems}</c>,
    /// or 409 for something not for sale or a balance too low.</summary>
    public const string Buy = "/account/buy";
    /// <summary>GET with the cookie: 200 <see cref="ServerFriends"/> (the friends, the requests and the pending requests of the player); POST with <c>X-Requested-With</c> to
    /// <c>/add</c> (asks, or accepts the other's request) or <c>/remove</c> (declines, cancels or removes) and the form <c>handle</c>: 200, or 404 for a handle that is not another player.</summary>
    public const string Friends = "/account/friends";
    /// <summary>GET with the cookie and <c>?text=</c>: 200 <see cref="ServerPlayers"/>, the players whose handle contains the text.</summary>
    public const string FriendSearch = "/account/friends/search";
    public const long MaxZipBytes = 1L << 30;
    /// <summary>One request's body at most, under a proxy's 100 MB.</summary>
    public const int PartBytes = 90 << 20;
}
