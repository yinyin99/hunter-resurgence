namespace Resurgence.Services;

/// <summary>One page of a list; <see cref="Number"/> counts from 1. <see cref="Total"/> is the length of the whole list when the page needs it (0 when not given).</summary>
public sealed record Page<T>(IReadOnlyList<T> Rows, int Number, int Pages, int Total = 0);
