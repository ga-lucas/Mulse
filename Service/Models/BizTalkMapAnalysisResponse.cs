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
    string? GeneratedXsltSourceCode)
{
    /// <summary>
    /// Number of functoid instances found in the map, grouped by their numeric <c>Functoid-FID</c> type
    /// identifier (e.g. <c>{424: 1, 474: 1}</c>). Reported instead of guessed at because there is no
    /// authoritative, verifiable FID-to-functoid-name table bundled with this importer - misidentifying a
    /// functoid (e.g. a scalar string transform vs. a structural looping/record functoid) could silently
    /// produce a wrong translation, which is worse than not translating it. Look up each FID by opening the
    /// original .btm map in BizTalk Mapper and checking the corresponding functoid's Properties dialog.
    /// </summary>
    public IReadOnlyDictionary<int, int> FunctoidFidCounts { get; init; } = new Dictionary<int, int>();
}
