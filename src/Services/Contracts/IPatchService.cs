namespace Resurgence.Services;

/// <summary>Done is the moment after patching ("READY" in the bar); Ready is the idle state with nothing to show.</summary>
public enum PatchState { Initialising, Downloading, Verifying, Extracting, Patching, Done, Ready }
public enum DownloadKind { Full, Patch, Launcher }
public enum PatchStep { Checksum, Files, Finishing, Error }
/// <summary>Reasons besides a download that block the window with a message. LauncherReady: the launcher has updated itself and has to restart.</summary>
public enum LauncherNotice { None, SteamUpdate, SteamOnly, Error, LauncherReady }

/// <summary>What a patch changes: <see cref="Version"/> is the one it leads to, <see cref="Message"/> the notes.</summary>
public sealed record PatchNote(string Version, string Message);

/// <param name="Progress">0 to 1; below 0 when the size is not known yet.</param>
/// <param name="Bytes">Size of the download.</param>
/// <param name="Rate">Bytes per second.</param>
/// <param name="Done">Files already done while patching.</param>
/// <param name="Total">Files to do while patching.</param>
/// <param name="Item">The file being checked.</param>
/// <param name="Notes">The notes of the patches being downloaded, newest last; null when the list is not known.</param>
public sealed record PatchStatus(PatchState State, DownloadKind Kind = DownloadKind.Full, double Progress = 0, string File = "", long Bytes = 0, long Rate = 0,
    PatchStep Step = PatchStep.Checksum, int Done = 0, int Total = 0, string Item = "", LauncherNotice Notice = LauncherNotice.None, int ErrorCode = 0,
    IReadOnlyList<PatchNote>? Notes = null)
{
    public static readonly PatchStatus Idle = new(PatchState.Ready);
}

public interface IPatchService
{
    /// <summary>Polled by the patch panel (bar and overlays) twice a second.</summary>
    Task<PatchStatus> StatusAsync();
}
