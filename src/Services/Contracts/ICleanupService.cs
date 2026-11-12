namespace Resurgence.Services;

/// <summary>What an older setup left on this PC. <paramref name="Programs"/>: full paths of old programs that still run; <paramref name="Folders"/>: old unpacked folders;
/// <paramref name="Logs"/>: stray game logs in the game's install folder; <paramref name="KitKept"/>: an older player kit setup is on the PC (hosts block, authority or certificate) and still fits
/// this version (true: kept) or does not (false: setup replaces it); null when there is none.</summary>
public sealed record OldSetup(IReadOnlyList<string> Programs, IReadOnlyList<string> Folders, IReadOnlyList<string> Logs, bool? KitKept)
{
    public bool Any => Programs.Count + Folders.Count + Logs.Count > 0 || KitKept is not null;
}

/// <summary>The clean-up of an older setup on the first start after the install (docs/launcher.md#install-and-uninstall). Only what <see cref="OldSetup"/> lists is ever touched.</summary>
public interface ICleanupService
{
    /// <summary>What was found, on the first start after the install only (and once); null at every other start and when there is nothing.</summary>
    Task<OldSetup?> FindAsync();

    /// <summary>Stops the listed programs, then deletes the listed folders and logs. A failure's <see cref="Result.Error"/> is a message of the system.</summary>
    Task<Result> CleanAsync(OldSetup found);
}
