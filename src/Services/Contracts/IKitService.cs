namespace Resurgence.Services;

/// <summary>What setup changes on this PC: the game's server names (<paramref name="Hosts"/>) in the hosts file (<paramref name="HostsFile"/>), the local certificate authority
/// <paramref name="CaName"/> in the user's trust list and the certificate in <paramref name="Folder"/>. <paramref name="Notice"/> says why setup is needed now (a translated text), or null.</summary>
public sealed record KitPlan(string[] Hosts, string CaName, string Folder, string HostsFile, string? Notice);

/// <summary>The player kit inside the launcher: it points the game at the community server (docs/launcher.md#the-player-kit). The website and the mock have none.</summary>
public interface IKitService
{
    /// <summary>True when this launcher sets up the PC for a community server; Settings offers Set up and Remove only then.</summary>
    bool Available { get; }

    /// <summary>Starts the forwarder when setup is in place. The plan when setup is needed now, else null; <see cref="Problem"/> tells why the forwarder does not run.</summary>
    Task<KitPlan?> StartAsync();

    /// <summary>A translated text: the forwarder could not start (a busy port 80 or 443) or settings.json is wrong. Null when all is well.</summary>
    string? Problem { get; }

    /// <summary>What setup would change; throws InvalidOperationException with a translated text when settings.json has no usable server_url or *_host.</summary>
    KitPlan Plan();

    /// <summary>Trusts the local authority, writes the hosts block (one administrator prompt) and starts the forwarder. A failure's <see cref="Result.Error"/> is a translated text, not a key.</summary>
    Task<Result> SetUpAsync();

    /// <summary>Undoes everything setup changed and stops the forwarder. A failure's <see cref="Result.Error"/> is a translated text, not a key.</summary>
    Task<Result> RemoveAsync();
}
