namespace Mulse.Modules.Abstractions;

/// <summary>
/// Opt-in contract for pure parse, render, or augment modules whose invocation can be repeated
/// with the same pinned input and settings after interruption. Implementations must not perform
/// external effects or mutate execution context (including responses, receipts, or checkpoints).
/// Fetch and deliver modules and native/ODX protocols never use this marker for recovery.
/// </summary>
public interface IReplaySafeModule : IModule;
