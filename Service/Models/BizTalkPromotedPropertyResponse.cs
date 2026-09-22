namespace Service.Models;

/// <summary>
/// One property promotion or write detected in a custom pipeline component's source (e.g. a call like
/// <c>context.Promote("MessageType", "http://schemas...", value)</c> found in <c>HL7Promotions.cs</c>). See
/// <see cref="Service.BizTalkImport.BizTalkPromotedPropertyAnalyzer"/>.
/// </summary>
public sealed record BizTalkPromotedPropertyResponse(
    string PropertyName,
    string? Namespace,
    bool IsPromoted,
    string SourceFileName);
