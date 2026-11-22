namespace Resurgence.Services;

/// <summary>One page of a list; <see cref="Number"/> counts from 1. <see cref="Total"/> is the length of the whole list when the page needs it (0 when not given).
/// <see cref="Around"/>, for a board: the rows of the player's own place and the 50 above and below it that are past the board's top 100 (null or empty when none).</summary>
public sealed record Page<T>(IReadOnlyList<T> Rows, int Number, int Pages, int Total = 0, IReadOnlyList<T>? Around = null);
