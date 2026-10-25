namespace Resurgence.Services.Mocks;

/// <summary>Always idle unless a development state is asked for: mock = "patch:new-patch", "patch:verifying", ... (the list is in D-PATCH-7).</summary>
public sealed class MockPatchService(string? mock = null) : IPatchService
{
    const string File = "2026081503_2026081804";

    readonly PatchStatus status = (mock?.StartsWith("patch:") == true ? mock[6..] : "") switch
    {
        "new-patch" => new(PatchState.Downloading, DownloadKind.Patch, .42, File, 123_457_000, 2_349_000),
        // the same with the list of notes the old launcher asked its server for
        "new-patch-notes" => new(PatchState.Downloading, DownloadKind.Patch, .42, File, 123_457_000, 2_349_000,
            Notes: [new("2026081503", "Fixed a crash when a game starts.\nThe map loads faster."), new("2026081804", "New animal behaviour at dawn.\nSeveral small fixes.")]),
        "full-download" => new(PatchState.Downloading, DownloadKind.Full, .42, File, 12_587_000_000, 8_500_000),
        "launcher-update" => new(PatchState.Downloading, DownloadKind.Launcher, .8, "827", 3_995_000, 1_500_000),
        "verifying" => new(PatchState.Verifying),
        "extracting" => new(PatchState.Extracting),
        "checksum" => new(PatchState.Patching, Progress: .33, Step: PatchStep.Checksum, Done: 12, Total: 87, Item: @"data\file_12.bin"),
        "files" => new(PatchState.Patching, Progress: .25, Step: PatchStep.Files, Done: 3, Total: 9),
        "finishing" => new(PatchState.Patching, Step: PatchStep.Finishing),
        "done" => new(PatchState.Done),
        "steam-update" => new(PatchState.Ready, Notice: LauncherNotice.SteamUpdate),
        "steam-only" => new(PatchState.Ready, Notice: LauncherNotice.SteamOnly),
        "error" => new(PatchState.Ready, Notice: LauncherNotice.Error, ErrorCode: 6),
        "launcher-ready" => new(PatchState.Ready, Notice: LauncherNotice.LauncherReady),
        // the game ended with a code: patch:exit-16001 ... patch:exit-16009
        var s when s.StartsWith("exit-") && int.TryParse(s[5..], out var code) => new(PatchState.Ready, Notice: LauncherNotice.Error, ErrorCode: code),
        _ => PatchStatus.Idle,
    };

    public Task<PatchStatus> StatusAsync() => Task.FromResult(status);
}
