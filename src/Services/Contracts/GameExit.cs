namespace Resurgence.Services;

/// <summary>How the game the launcher watched ended (<see cref="ILauncherBridge.WhenGameExitsAsync"/>).</summary>
/// <param name="Seconds">How long it ran, from its start.</param>
/// <param name="Mode">"steam" or "nosteam": how that process was started.</param>
/// <param name="Early">true when the game ended within 20 s of its start</param>
/// <param name="Cause">only when Early: steam (16010 and the no-Steam retry failed too), blocked (Windows app control), runtime (a DLL missing), closed (exit 0: clock/server), crash (anything else); null when not Early</param>
public sealed record GameExit(int ExitCode, int Seconds, string Mode, bool Early, string? Cause);
