namespace Service.Models;

/// <summary>
/// Outcome for one exact map-name string found in a policy. Status is Converted, ExplicitChoice, NoMatchingMap, AmbiguousMap, SchemaUnresolved,
/// ConversionFailed or CapExceeded. Only Converted/ExplicitChoice values get a render-dispatch case; every other value stays fail-closed at run time.
/// </summary>
public sealed record BizTalkDerivedMapChoiceResponse(
    string Value,
    string Status,
    string? MapPath,
    string Detail,
    IReadOnlyList<string> Policies,
    IReadOnlyList<string> Warnings);
