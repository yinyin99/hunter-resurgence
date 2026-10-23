namespace Resurgence.Services;

/// <summary>One page of a list; <see cref="Number"/> counts from 1.</summary>
public sealed record Page<T>(IReadOnlyList<T> Rows, int Number, int Pages);
