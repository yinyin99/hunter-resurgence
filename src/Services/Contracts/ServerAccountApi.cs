namespace Resurgence.Services;

/// <summary>What the community server serves under <c>/account/</c> to the website's own server (the browser never calls it, so the reverse proxy need not route it): the session cookie of <see cref="ServerSessionApi"/> authenticates all of them.</summary>
public static class ServerAccountApi
{
    /// <summary>GET: <c>{restoredUserId, review: [{ts, note}]}</c>. POST with <c>X-Requested-With</c>: the zip is the body (at most <see cref="MaxZipBytes"/>); answers <c>{status, message, report}</c>.</summary>
    public const string Restore = "/account/restore";
    /// <summary>POST with <c>X-Requested-With</c> and the form <c>confirm=delete</c>: deletes the account and all its data.</summary>
    public const string Delete = "/account/delete";
    /// <summary>POST with <c>X-Requested-With</c> and the form <c>id</c>, <c>count</c>, <c>bundle=1</c> for a bundle: buys with em$ at the store's price; 200 <c>{status, ems}</c>,
    /// or 409 for something not for sale or a balance too low.</summary>
    public const string Buy = "/account/buy";
    public const long MaxZipBytes = 64 << 20;
}
