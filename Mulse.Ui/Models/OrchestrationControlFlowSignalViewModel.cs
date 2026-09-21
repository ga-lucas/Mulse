namespace Mulse.Ui.Models;

/// <summary>A heuristic orchestration control-flow complexity signal (convoy, correlation, parallel, etc.).</summary>
public sealed record OrchestrationControlFlowSignalViewModel(string ShapeType, string Description, int Count);
