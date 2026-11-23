using Microsoft.AspNetCore.Components;
using Resurgence.Services;

namespace Resurgence.Ui.Modals;

/// <summary>The one open modal of the shell. Backdrop kinds: "page" (dark, for full-page modals) and "alert" (light, centred panel).</summary>
public sealed class ModalService
{
    public Type? Type { get; private set; }
    public IDictionary<string, object?>? Parameters { get; private set; }
    public string Backdrop { get; private set; } = "page";
    public event Action? Changed;

    /// <summary>The "Started without Steam" note (<see cref="SteamStart"/>) was clicked away or timed out: it stays away until the next start. Shared by the copy on the page and the one inside the dialog.</summary>
    public bool SteamNoteGone { get; set; }

    // a click on the backdrop closes these (as in the old app); the language and alert dialogs do not (D-SHELL-5), nor the game settings.
    // The join map has a full-window layer of its own inside the dialog.
    static readonly Type[] CloseOnBackdrop = [typeof(BankModal), typeof(BuyModal), typeof(VideoModal), typeof(GameStartedModal)];

    public bool ClosesOnBackdrop => Type is not null && CloseOnBackdrop.Contains(Type);

    public void Show<T>(IDictionary<string, object?>? parameters = null, string backdrop = "page") where T : IComponent
    {
        Release();
        (Type, Parameters, Backdrop) = (typeof(T), parameters, backdrop);
        Changed?.Invoke();
    }

    public void Close()
    {
        Release();
        Type = null;
        Changed?.Invoke();
    }

    // there is one slot: a dialog that is replaced or dismissed answers "no" to whoever waits for it (Confirm, the first-start setup)
    void Release()
    {
        if (Type is null || Parameters is null) return;
        foreach (var wait in Parameters.Values.OfType<TaskCompletionSource<bool>>()) wait.TrySetResult(false);
    }

    /// <summary>Before the first game start the game is set up (the "Initial setup" dialog). False when the start must not go on.</summary>
    public async Task<bool> SetUpFirst(IGameSettingsService settings)
    {
        // without readable game files there is nothing to set up, the start goes on
        if (!await settings.FirstStartAsync() || (await settings.ReadAsync()).Error is not null) return true;
        var done = new TaskCompletionSource<bool>();
        Show<GameSettingsModal>(new Dictionary<string, object?> { [nameof(GameSettingsModal.FirstStart)] = true, [nameof(GameSettingsModal.Done)] = done });
        return await done.Task;
    }

    /// <summary>At the launcher's start: the setup this PC still needs (the player kit, docs/launcher.md#the-player-kit), else what stops the forwarder. The dialog answers by itself; the start does not wait for it.</summary>
    public async Task OfferKitAsync(IKitService kit, ISteamOptionService steam)
    {
        if (kit.Available && await kit.StartAsync() is { } plan)
        {
            var done = new TaskCompletionSource();
            Show<KitSetupModal>(new Dictionary<string, object?> { [nameof(KitSetupModal.Plan)] = plan, [nameof(KitSetupModal.Done)] = done });
            _ = done.Task.ContinueWith(_ => OfferSteamAsync(steam), TaskScheduler.Default); // Steam's dialog opens only after this one is closed
        }
        else if (kit.Available && kit.Problem is { } problem) Alert(problem); // Steam's dialog would replace it: it comes at the next start
        else await OfferSteamAsync(steam);
    }

    /// <summary>Steam's Play button opens the launcher: the dialog opens when the launch option is not set (at most once a day, never after "don't ask again").</summary>
    async Task OfferSteamAsync(ISteamOptionService steam)
    {
        if (Type is null && await steam.DueAsync()) Show<SteamOptionModal>(); // not over another dialog (the kit's "done" notice): it comes at the next start
    }

    /// <summary>On the first start after the install: what an older setup left, if any (the "Older setup found" dialog), then the player kit as <see cref="OfferKitAsync"/>. The dialog answers by itself; the start does not wait for it.</summary>
    public async Task OfferCleanupAsync(ICleanupService cleanup, IKitService kit, ISteamOptionService steam)
    {
        if (await cleanup.FindAsync() is not { } found) { await OfferKitAsync(kit, steam); return; }
        var done = new TaskCompletionSource(); // not a TaskCompletionSource<bool>: Close must not answer it, the dialog sets it after it has closed
        Show<OldSetupModal>(new Dictionary<string, object?> { [nameof(OldSetupModal.Found)] = found, [nameof(OldSetupModal.Done)] = done });
        _ = done.Task.ContinueWith(_ => OfferKitAsync(kit, steam), TaskScheduler.Default); // the kit's dialog opens only after this one is closed
    }

    /// <summary>After a sign-in: the one-time Discord invitation (the "Join the Discord" dialog) the first time the account is asked, never over another dialog (the launcher's first-start dialogs come first).</summary>
    public async Task OfferDiscordAsync(IDiscordInviteService invite)
    {
        if (Type is null && await invite.FirstAsync() && Type is null) Show<DiscordInviteModal>();
    }

    /// <summary>After the Discord invitation is closed, or at once when there is none: the one-time "Do you have a backup of your profile?" dialog, never over another dialog.</summary>
    public async Task OfferRestoreAsync(IRestoreOfferService offer)
    {
        if (Type == typeof(DiscordInviteModal)) await WhenClosedAsync(() => Type != typeof(DiscordInviteModal));
        if (Type is null && await offer.FirstAsync() && Type is null) Show<RestoreOfferModal>();
    }

    /// <summary>After the other first-sign-in dialogs: the one-time "Send problem reports?" dialog (launcher only) while the player has not chosen yet. It waits for the dialog that is open, and never replaces one.</summary>
    public async Task OfferReportConsentAsync(ILauncherBridge bridge)
    {
        if (bridge.ReportConsent != "unset") return;
        while (Type is not null) await WhenClosedAsync(() => Type is null);
        Show<ReportConsentModal>();
    }

    Task WhenClosedAsync(Func<bool> closed)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch() { if (closed()) { Changed -= Watch; done.TrySetResult(); } }
        Changed += Watch;
        return done.Task;
    }

    public void Alert(string text) => Show<AlertDialog>(new Dictionary<string, object?> { ["Text"] = text }, "alert");

    /// <summary>A refused game start: the text of its error key and, when the install check that refused it has a fix, that fix's button (<see cref="StartFailedDialog"/>).</summary>
    public void StartFailed(Result r) =>
        Show<StartFailedDialog>(new Dictionary<string, object?> { [nameof(StartFailedDialog.Error)] = r.Error ?? "Launch.Failed" }, "alert");

    bool closingGame;

    /// <summary>The button of an install check (<see cref="InstallCheck.Fix"/>): the "Game folder" window, the player kit's setup, Steam, closing the running game
    /// (<paramref name="closed"/> runs once it is closed: a refused start's dialog tells so; the player starts again from the page), or the display's current mode written into the graphics file (a failure is shown like a refused start).</summary>
    public async Task FixAsync(string fix, ILauncherBridge bridge, IKitService kit, Action? closed = null)
    {
        switch (fix)
        {
            case "choose-folder": Show<GameFolderModal>(); break;
            case "open-folder": bridge.OpenFolder("logs"); break; // the exe's folder, where the DLLs of the dlls check are
            case "open-steam": bridge.OpenExternal("steam://open/main"); break;
            // the launcher puts its steam_app_id (settings.json) in place of the placeholder
            case "verify-files": bridge.OpenExternal("steam://nav/games/details/{steam_app_id}"); break;
            case "fix-display": if (bridge.FixDisplay() is { Ok: false } failed) StartFailed(failed); break;
            case "close-game" when !closingGame: // a second click while it closes would stop it twice
                closingGame = true;
                try
                {
                    var result = await bridge.CloseGameAsync();
                    if (!result.Ok) StartFailed(result);
                    else closed?.Invoke();
                }
                finally { closingGame = false; }
                break;
            case "setup" when kit.Available:
                try { Show<KitSetupModal>(new Dictionary<string, object?> { [nameof(KitSetupModal.Plan)] = await kit.StartAsync() ?? kit.Plan() }); }
                catch (InvalidOperationException e) { Alert(e.Message); } // settings.json has no usable server_url or *_host: the plan's translated text
                break;
        }
    }

    /// <summary>"Done hunting for now?" once the started game has closed (<see cref="ILauncherBridge.WhenGameExitsAsync"/>), whatever else is open then; none when the game never appeared.
    /// The old app opened it five seconds after the start: its texts then read like an error while the game loads, and players started it again.</summary>
    public void GameStarted(ILauncherBridge bridge) => _ = bridge.WhenGameExitsAsync().ContinueWith(ran => GameEnded(ran.Result), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    /// <summary>The game the launcher started has closed: after a real hunt "Done hunting for now?", within seconds of its start "The game closed right away" with the cause's one action (<see cref="GameExitDialog"/>); nothing when it never appeared (null).</summary>
    public void GameEnded(GameExit? exit)
    {
        if (exit is null) return;
        if (exit.Early) Show<GameExitDialog>(new Dictionary<string, object?> { [nameof(GameExitDialog.Exit)] = exit }, "alert");
        else Show<GameStartedModal>();
    }

    /// <summary>Yes/No dialog; true for Yes.</summary>
    public Task<bool> Confirm(string text)
    {
        var answer = new TaskCompletionSource<bool>();
        Show<AlertDialog>(new Dictionary<string, object?> { ["Text"] = text, ["Answer"] = answer }, "alert");
        return answer.Task;
    }
}
