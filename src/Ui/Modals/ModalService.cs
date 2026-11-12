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

    // a click on the backdrop closes these (as in the old app); the language, alert and legal dialogs do not (D-SHELL-5), nor the game settings.
    // The join map has a full-window layer of its own inside the dialog.
    static readonly Type[] CloseOnBackdrop = [typeof(BankModal), typeof(BuyModal), typeof(VideoModal)];

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
        if (await steam.DueAsync()) Show<SteamOptionModal>();
    }

    /// <summary>On the first start after the install: what an older setup left, if any (the "Older setup found" dialog), then the player kit as <see cref="OfferKitAsync"/>. The dialog answers by itself; the start does not wait for it.</summary>
    public async Task OfferCleanupAsync(ICleanupService cleanup, IKitService kit, ISteamOptionService steam)
    {
        if (await cleanup.FindAsync() is not { } found) { await OfferKitAsync(kit, steam); return; }
        var done = new TaskCompletionSource(); // not a TaskCompletionSource<bool>: Close must not answer it, the dialog sets it after it has closed
        Show<OldSetupModal>(new Dictionary<string, object?> { [nameof(OldSetupModal.Found)] = found, [nameof(OldSetupModal.Done)] = done });
        _ = done.Task.ContinueWith(_ => OfferKitAsync(kit, steam), TaskScheduler.Default); // the kit's dialog opens only after this one is closed
    }

    public void Alert(string text) => Show<AlertDialog>(new Dictionary<string, object?> { ["Text"] = text }, "alert");

    /// <summary>The old app opens "Done hunting for now?" five seconds after a game was started or joined, whatever else is open then.</summary>
    public void GameStarted() => _ = Task.Delay(5000).ContinueWith(_ => Show<GameStartedModal>(), TaskScheduler.Default);

    /// <summary>Yes/No dialog; true for Yes.</summary>
    public Task<bool> Confirm(string text)
    {
        var answer = new TaskCompletionSource<bool>();
        Show<AlertDialog>(new Dictionary<string, object?> { ["Text"] = text, ["Answer"] = answer }, "alert");
        return answer.Task;
    }
}
