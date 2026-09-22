namespace Mulse.Ui.Models;

/// <summary>A single scaffolded starter module (a C# augment module or a best-effort XSLT rendering
/// stylesheet) whose source logic was too complex, or otherwise not fully coverable, to auto-translate.</summary>
public sealed record BizTalkScaffoldedModuleViewModel(
    string FileName,
    string ModuleId,
    string SourceArtifactName,
    string SourceCode);
