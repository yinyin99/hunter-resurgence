using Microsoft.AspNetCore.Components;

namespace Resurgence.Ui.Modals;

/// <summary>The one open modal of the shell. Backdrop kinds: "page" (dark, for full-page modals) and "alert" (light, centred panel).</summary>
public sealed class ModalService
{
    public Type? Type { get; private set; }
    public IDictionary<string, object?>? Parameters { get; private set; }
    public string Backdrop { get; private set; } = "page";
    public event Action? Changed;

    public void Show<T>(IDictionary<string, object?>? parameters = null, string backdrop = "page") where T : IComponent
    {
        (Type, Parameters, Backdrop) = (typeof(T), parameters, backdrop);
        Changed?.Invoke();
    }

    public void Close()
    {
        Type = null;
        Changed?.Invoke();
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
