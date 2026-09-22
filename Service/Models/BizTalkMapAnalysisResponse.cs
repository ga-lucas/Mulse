namespace Service.Models;

/// <summary>
/// Best-effort translation result for a single BizTalk map (.btm) file: how many of its links/constants were
/// direct field-to-field assignments (auto-translatable) versus functoid-involved (not auto-translatable), and
/// the generated XSLT 1.0 stylesheet covering everything that could be translated. See
/// <see cref="Service.BizTalkImport.BizTalkMapAnalyzer"/> for the translation rules and their limits.
/// </summary>
public sealed record BizTalkMapAnalysisResponse(
    string MapName,
    string MapFilePath,
    string SourceSchemaReference,
    string TargetSchemaReference,
    int DirectLinkCount,
    int ConstantValueCount,
    int FunctoidInvolvedLinkCount,
    string? ScaffoldedModuleFileName,
    string? ScaffoldedModuleId,
    string? GeneratedXsltSourceCode);
