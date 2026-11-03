namespace Resurgence.Tests;

/// <summary>Tests that change process-global state (Loc, default cultures, Console output) run one at a time in this collection.</summary>
[CollectionDefinition("ProcessState", DisableParallelization = true)]
public sealed class ProcessStateCollection;
