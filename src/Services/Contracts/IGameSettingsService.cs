namespace Resurgence.Services;

public sealed record GameOptions(bool Fullscreen, bool Vsync, int Width, int Height, int Hz);

public interface IGameSettingsService
{
    Task<GameOptions> ReadAsync();
    Task<Result> SaveAsync(GameOptions options);
}
