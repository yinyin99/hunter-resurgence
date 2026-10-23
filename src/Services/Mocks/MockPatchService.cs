namespace Resurgence.Services.Mocks;

/// <summary>Always idle unless a development state is asked for: mock = "patch:new-patch", "patch:verifying", ...</summary>
public sealed class MockPatchService(string? mock = null) : IPatchService
{
    const string File = "2026081503_2026081804";

    readonly PatchStatus status = (mock?.StartsWith("patch:") == true ? mock[6..] : "") switch
    {
        "new-patch" => new(PatchState.Downloading, DownloadKind.Patch, .42, File, 123_457_000, 2_349_000),
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
        _ => PatchStatus.Idle,
    };

    public Task<PatchStatus> StatusAsync() => Task.FromResult(status);
}
