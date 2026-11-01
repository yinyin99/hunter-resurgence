// The names the game client itself requires on the wire or in its pages and that carry the game's name. This is the
// only source file besides README.md that may hold them (CONTRIBUTING.md#rules); everything else refers to these constants.
namespace Resurgence.Server;

public static class ProtocolLiterals
{
    /// <summary>Path prefix of the game server's RPC class: heartbeat, disconnect and connect hang below it.</summary>
    public const string RpcBase = "/json/rgc-01-00/Hunter_GameClient";

    /// <summary>Path prefix of the old launcher's own calls to the game server: token, initialise, compatibleHardware, ping.</summary>
    public const string LauncherBase = "/json/rgc-01-00/EmoteLauncher";

    /// <summary>The scheme of the launch URL the old launcher gives the game. The client's environment check looks for the
    /// game's name anywhere in that URL, so a host name of the operator's choice cannot make it fail.</summary>
    public const string LaunchScheme = "thehunter";

    /// <summary>The image the client shows in its toasts (a path on the static host).</summary>
    public const string IconPath = "/static/img/huntericon.png";

    /// <summary>Header image (a name below /static/img/scoring/) of the score sheets that carry the game's own logo.</summary>
    public const string SheetLogo = "thehunterlogo";

    /// <summary>Prefix of the texture constant names in the game's web script: `PREFIX_NAME:id` values and `case n("PREFIX_NAME"):return"name"`.</summary>
    public const string TexturePrefix = "HUNTER_GAME_TEXTURE_";
}
