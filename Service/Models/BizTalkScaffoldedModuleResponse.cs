namespace Service.Models;

/// <summary>
/// A downloadable starter artifact: fail-explicit C# for untranslated orchestration/pipeline behavior,
/// or a possibly incomplete starter map stylesheet. Compilation is not implementation or execution approval.
/// </summary>
public sealed record BizTalkScaffoldedModuleResponse(
    string FileName,
    string ModuleId,
    string SourceArtifactName,
    string SourceCode);
