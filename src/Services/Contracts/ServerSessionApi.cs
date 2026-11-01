namespace Resurgence.Services;

/// <summary>What the community server serves on the website's own host (docs/website.md#sign-in): the cookie and the three paths. Sign-in is a browser navigation to <see cref="SteamLogin"/>.</summary>
public static class ServerSessionApi
{
    public const string Cookie = "hr_session";
    /// <summary>GET with the cookie: 200 <see cref="ServerAccount"/>, or 401 when signed out.</summary>
    public const string Me = "/auth/me";
    /// <summary>POST with the cookie and <c>X-Requested-With: XMLHttpRequest</c>: 204, the session is deleted.</summary>
    public const string SignOut = "/auth/signout";
    /// <summary>Takes <c>?next=</c>, a local path to come back to.</summary>
    public const string SteamLogin = "/auth/steam/login";
}

/// <summary>The JSON of <see cref="ServerSessionApi.Me"/>.</summary>
public sealed record ServerAccount(long UserId, string Handle);
