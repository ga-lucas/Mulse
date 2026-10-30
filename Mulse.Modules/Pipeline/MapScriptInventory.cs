namespace Mulse.Modules.Pipeline;

public sealed record MapScriptInventory(int TotalScripts, int OmittedCount, IReadOnlyList<MapScriptEvidence> Scripts);

public sealed record MapScriptEvidence(
    int SourceLine,
    string FunctoidId,
    string Language,
    int CharacterCount,
    string Sha256,
    IReadOnlyList<string> BizTalkApiReferences);
