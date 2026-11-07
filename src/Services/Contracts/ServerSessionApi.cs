namespace Resurgence.Services;

/// <summary>What the community server serves on the website's own host (docs/website.md#sign-in): the cookie and its paths. Sign-in is a browser navigation to <see cref="SteamLogin"/>.</summary>
public static class ServerSessionApi
{
    public const string Cookie = "hr_session";
    /// <summary>GET with the cookie: 200 <see cref="ServerAccount"/>, or 401 when signed out.</summary>
    public const string Me = "/auth/me";
    /// <summary>POST with the cookie and <c>X-Requested-With: XMLHttpRequest</c>: 204, the session is deleted.</summary>
    public const string SignOut = "/auth/signout";
    /// <summary>Takes <c>?next=</c>, a local path to come back to.</summary>
    public const string SteamLogin = "/auth/steam/login";
    /// <summary>GET <c>?page=n</c>: 200 <see cref="ServerBoard"/>; GET <c>/hunters/&lt;handle&gt;</c>: 200 <see cref="ServerHunter"/> or 404. Public: no cookie needed.</summary>
    public const string Hunters = "/hunters";
    /// <summary>GET <c>/leaderboards/&lt;lanebandit|bustthrough&gt;?page=n</c>: 200 <see cref="ServerGameBoard"/>. Public: no cookie needed.</summary>
    public const string Leaderboards = "/leaderboards";
}

/// <summary>The JSON of <see cref="ServerSessionApi.Me"/>: <c>ems</c> and <c>gms</c> are the wallet.</summary>
public sealed record ServerAccount(long UserId, string Handle, int Hunterscore = 0, int Rank = 0, int Ems = 0, int Gms = 0);

/// <summary>A hunter on the hunterscore board of <see cref="ServerSessionApi.Hunters"/>.</summary>
public sealed record ServerHunter(int Rank, string Handle, int Hunterscore);

/// <summary>One page of the hunterscore board of <see cref="ServerSessionApi.Hunters"/>.</summary>
public sealed record ServerBoard(int Page, int Pages, int Total, ServerHunter[] Rows);

/// <summary>A hunter's best score on a minigame board of <see cref="ServerSessionApi.Leaderboards"/>.</summary>
public sealed record ServerGameRow(int Rank, string Handle, long Value);

/// <summary>One page of a minigame board of <see cref="ServerSessionApi.Leaderboards"/>.</summary>
public sealed record ServerGameBoard(int Page, int Pages, int Total, ServerGameRow[] Rows);
