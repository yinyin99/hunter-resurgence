namespace Resurgence.Services;

public enum PatchState { Checking, Downloading, Patching, Ready, Error }

public sealed record PatchStatus(PatchState State, int Percent);

public interface IPatchService
{
    /// <summary>Polled by the patch bar while it is visible.</summary>
    Task<PatchStatus> StatusAsync();
}
