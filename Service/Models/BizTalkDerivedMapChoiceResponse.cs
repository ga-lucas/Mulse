namespace Service.Models;

/// <summary>
/// Outcome for one exact map-name string found in a policy. Status is Converted, ConvertedWithStubs, ExplicitChoice, NoMatchingMap, AmbiguousMap, SchemaUnresolved,
/// ConversionFailed or CapExceeded. Only Converted/ConvertedWithStubs/ExplicitChoice values get a render-dispatch case; every other value stays fail-closed at run time,
/// and a ConvertedWithStubs case fails closed when it is selected because its scripting functoids have no formula yet.
/// </summary>
public sealed record BizTalkDerivedMapChoiceResponse(
    string Value,
    string Status,
    string? MapPath,
    string Detail,
    IReadOnlyList<string> Policies,
    IReadOnlyList<string> Warnings);
