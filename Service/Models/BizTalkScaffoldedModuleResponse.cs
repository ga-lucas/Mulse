namespace Service.Models;

/// <summary>
/// A starter C# module source file scaffolded for a BizTalk orchestration whose control flow (complex decision
/// branches, convoys, correlation sets, atomic transactions, or loops) could not be auto-translated into
/// declarative augment rules. The source compiles as-is (it passes payloads through unchanged) so it's always
/// safe to save and build; the embedded TODO comments summarize exactly what manual logic still needs to be
/// implemented, quoting the original BizTalk branch expressions and control-flow shape counts.
/// </summary>
public sealed record BizTalkScaffoldedModuleResponse(
    string FileName,
    string ModuleId,
    string SourceArtifactName,
    string SourceCode);
