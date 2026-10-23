namespace Resurgence.Services;

public sealed record Resolution(int Width, int Height, int Hz);

/// <summary>Quality values are the list positions of the dialog: Scene and Shadow 0 Low to 3 Very High, Texture 0 to 2, Msaa 0 Off to 3 8x, Aniso 0 Off to 4 16x. A null switch is not offered by the game's files and is not shown.</summary>
public sealed record GameOptions(bool Fullscreen, bool Vsync, Resolution Resolution, int Scene, int Texture, int Shadow, int Msaa, int Aniso, bool? ContextualHelp, bool? ExtendedAudio);

/// <param name="Resolutions">What the display offers.</param>
/// <param name="VideoMemoryMb">0 when unknown.</param>
/// <param name="Error">Localisation key when the settings cannot be read; the other values are then unusable.</param>
public sealed record GameSettings(GameOptions Options, IReadOnlyList<Resolution> Resolutions, int VideoMemoryMb, string? Error = null);

public interface IGameSettingsService
{
    Task<GameSettings> ReadAsync();
    /// <summary>Fails while the game runs: it rewrites its files itself.</summary>
    Task<Result> SaveAsync(GameOptions options);
    /// <summary>True until the settings were saved once: the first game start then asks for them.</summary>
    Task<bool> FirstStartAsync();
}
