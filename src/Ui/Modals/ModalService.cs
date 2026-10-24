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

    public void Alert(string text) => Show<AlertDialog>(new Dictionary<string, object?> { ["Text"] = text }, "alert");

    /// <summary>Yes/No dialog; true for Yes.</summary>
    public Task<bool> Confirm(string text)
    {
        var answer = new TaskCompletionSource<bool>();
        Show<AlertDialog>(new Dictionary<string, object?> { ["Text"] = text, ["Answer"] = answer }, "alert");
        return answer.Task;
    }
}
