namespace Resurgence.Services;

/// <summary>Outcome of an action; <see cref="Error"/> is a localisation key the page shows.</summary>
public sealed record Result(bool Ok, string? Error = null)
{
    public static readonly Result Success = new(true);
    public static Result Fail(string key) => new(false, key);
}

/// <summary>An outcome that carries what the action returned (<see cref="Value"/>, set when <see cref="Ok"/>).</summary>
public sealed record Result<T>(bool Ok, T? Value = default, string? Error = null)
{
    public static Result<T> Success(T value) => new(true, value);
    public static Result<T> Fail(string key) => new(false, default, key);
}
