namespace Resurgence.Services;

/// <summary>One result of the install check (docs/launcher.md#the-install-check).</summary>
/// <param name="Id">game, version, files, path, steam, setup, windows, disk, running</param>
/// <param name="Level">ok, warn or block</param>
/// <param name="Text">a Strings key: <c>Check.Ok</c> for ok, else the problem's</param>
/// <param name="Args">values for the text's {placeholders}; an ok result may carry facts for the problem report</param>
/// <param name="Fix">choose-folder, open-steam, verify-files, setup, close-game or null</param>
public sealed record InstallCheck(string Id, string Level, string Text, IReadOnlyDictionary<string, string>? Args = null, string? Fix = null);
