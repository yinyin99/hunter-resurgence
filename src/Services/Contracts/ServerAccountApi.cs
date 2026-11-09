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
    public const long MaxZipBytes = 1L << 30;
    /// <summary>One request's body at most, under a proxy's 100 MB.</summary>
    public const int PartBytes = 90 << 20;
}
