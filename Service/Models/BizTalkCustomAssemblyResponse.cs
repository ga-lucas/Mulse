namespace Service.Models;

/// <summary>Represents one custom .NET assembly that participates in a BizTalk integration application.</summary>
public sealed record BizTalkCustomAssemblyResponse(
    string Name,
    string ProjectPath,
    int SourceFileCount,
    string SuggestedModuleKind,
    string MigrationApproach,
    bool WrapperRecommended,
    IReadOnlyList<string> UsedByProjects)
{
    /// <summary>
    /// Context properties this assembly's source promotes or writes (e.g. via a pipeline component like
    /// <c>HL7Promotions.cs</c> calling <c>context.Promote(...)</c>/<c>context.Write(...)</c>), extracted by
    /// <see cref="Service.BizTalkImport.BizTalkPromotedPropertyAnalyzer"/>. Empty when no such calls were found.
    /// </summary>
    public IReadOnlyList<BizTalkPromotedPropertyResponse> PromotedProperties { get; init; } = [];
}
