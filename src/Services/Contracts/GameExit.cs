namespace Resurgence.Services;

/// <summary>How the game the launcher watched ended (<see cref="ILauncherBridge.WhenGameExitsAsync"/>).</summary>
/// <param name="Seconds">How long it ran, from its start.</param>
/// <param name="Mode">"steam" or "nosteam": how that process was started.</param>
/// <param name="Early">true when the game ended within 20 s of its start</param>
/// <param name="Cause">only when Early: steam (a start with Steam and the no-Steam retry after it both quit with 16010), blocked (Windows app control), runtime (a DLL missing), dll (a game file replaced: STATUS_ENTRYPOINT_NOT_FOUND), closed (exit 0), crash (anything else); null when not Early</param>
/// <param name="ClockAhead">Only for an early exit 0: how far this PC's clock is ahead of the server's (negative: behind) when it differs by more than a few minutes; null when it is in step or was not measured</param>
/// <param name="Display">Only for an early exit that is not a cause of its own (closed or crash) and not the clock's: the <c>display</c> install check's warning of the start it followed (its text, values and fix), which the dialog names instead of the generic text; null when it did not warn</param>
public sealed record GameExit(int ExitCode, int Seconds, string Mode, bool Early, string? Cause, TimeSpan? ClockAhead = null, InstallCheck? Display = null);
