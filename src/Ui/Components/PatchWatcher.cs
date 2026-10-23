using Microsoft.AspNetCore.Components;
using Resurgence.Services;

namespace Resurgence.Ui.Components;

/// <summary>A component that follows the launcher state: <see cref="Status"/> is refreshed twice a second and <see cref="Tick"/> counts the refreshes (the animated dots).</summary>
public abstract class PatchWatcher : ComponentBase, IDisposable
{
    readonly CancellationTokenSource stop = new();

    [Inject] IPatchService Patch { get; set; } = null!;

    protected PatchStatus Status { get; private set; } = PatchStatus.Idle;
    protected int Tick { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        Status = await Patch.StatusAsync();
        _ = Watch();
    }

    async Task Watch()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                Status = await Patch.StatusAsync();
                Tick++;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException) { }
    }

    public virtual void Dispose() => stop.Cancel();
}
