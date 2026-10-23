namespace Resurgence.Services;

/// <summary>Outcome of an action; <see cref="Error"/> is a localisation key the page shows.</summary>
public sealed record Result(bool Ok, string? Error = null)
{
    public static readonly Result Success = new(true);
    public static Result Fail(string key) => new(false, key);
}
