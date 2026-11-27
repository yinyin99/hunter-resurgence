namespace Resurgence.Services;

public sealed record Resolution(int Width, int Height, int Hz);

/// <summary>How the game shows: Borderless (windowed over the whole monitor, without title bar or border; the default), Fullscreen (the game's exclusive full screen) or Windowed.</summary>
public enum DisplayMode { Borderless, Fullscreen, Windowed }

/// <summary>Quality values are the list positions of the dialog: Scene and Shadow 0 Low to 3 Very High, Texture 0 to 2, Msaa 0 Off to 3 8x, Aniso 0 Off to 4 16x. A null switch is not offered by the game's files and is not shown.
/// In Borderless the resolution is the monitor's size. StartWithSteam is the launcher's box "Start the game using Steam".</summary>
public sealed record GameOptions(DisplayMode Display, bool Vsync, Resolution Resolution, int Scene, int Texture, int Shadow, int Msaa, int Aniso, bool? ContextualHelp, bool? ExtendedAudio, bool StartWithSteam = true);

/// <param name="Resolutions">What the display offers.</param>
/// <param name="Error">Localisation key when the settings cannot be read; the other values are then unusable.</param>
public sealed record GameSettings(GameOptions Options, IReadOnlyList<Resolution> Resolutions, string? Error = null);

/// <summary>The 22 languages the game ships (its localization file), by the code of the launch URL's <c>lang</c> segment and the language's own name; the server accepts the same codes (<c>GameTokens.GameLanguages</c>).</summary>
public static class GameLanguages
{
    public const string Default = "en_US";

    public static readonly IReadOnlyList<(string Code, string Name)> All =
    [
        ("en_US", "English"), ("de_DE", "Deutsch"), ("es_ES", "Español"), ("et_EE", "Eesti"), ("fr_FR", "Français"), ("hr_HR", "Hrvatski"), ("hu_HU", "Magyar"), ("nl_NL", "Nederlands"),
        ("pl_PL", "Polski"), ("pt_BR", "Português (Brasil)"), ("pt_PT", "Português (Portugal)"), ("ru_RU", "Русский"), ("sv_SE", "Svenska"), ("da_DK", "Dansk"), ("cs_CZ", "Čeština"),
        ("nb_NO", "Norsk (bokmål)"), ("it_IT", "Italiano"), ("fi_FI", "Suomi"), ("tr_TR", "Türkçe"), ("sl_SI", "Slovenščina"), ("ka_KA", "ქართული"), ("ro_RO", "Română"),
    ];

    public static bool Has(string? code) => All.Any(l => l.Code == code);

    /// <summary>The game's language for a language tag of the launcher or the PC ("fr", "pt-BR", "de-AT"): the code of that tag, else the first code of its language ("pt" is Brazilian, "no" and "nn" Norwegian bokmål), else null (the game lacks it).</summary>
    public static string? Match(string? tag)
    {
        var code = (tag ?? "").Replace('-', '_');
        var language = code.Split('_')[0] is "no" or "nn" ? "nb" : code.Split('_')[0];
        return All.Select(l => l.Code).FirstOrDefault(c => c.Equals(code, StringComparison.OrdinalIgnoreCase))
            ?? All.Select(l => l.Code).FirstOrDefault(c => c.StartsWith(language + "_", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><see cref="Match"/>, else English.</summary>
    public static string For(string? tag) => Match(tag) ?? Default;
}

public interface IGameSettingsService
{
    Task<GameSettings> ReadAsync();
    /// <summary>Fails while the game runs: it rewrites its files itself.</summary>
    Task<Result> SaveAsync(GameOptions options);
    /// <summary>True until the settings were saved once: the first game start then asks for them.</summary>
    Task<bool> FirstStartAsync();
    /// <summary>The language the game starts in, a code of <see cref="GameLanguages"/>: the player's choice, before one the launcher's language when the game has it, else English.</summary>
    Task<string> LanguageAsync();
    Task SetLanguageAsync(string code);
    /// <summary>The language the host chose last for its games, a code of the launcher's interface languages; null until the host has chosen one (the host form then preselects the launcher's language).</summary>
    Task<string?> HostLanguageAsync();
    Task SetHostLanguageAsync(string code);
}
