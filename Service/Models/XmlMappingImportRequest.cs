using System.ComponentModel.DataAnnotations;

namespace Service.Models;

public sealed record XmlSchemaAnalysisRequest(
    [property: Required, MinLength(1), MaxLength(16)] IReadOnlyList<string> Schemas);

public sealed record XmlMappingImportRequest(
    [property: Required, StringLength(1_000_000)] string MapXml,
    [property: Required, MinLength(1), MaxLength(16)] IReadOnlyList<string> SourceSchemas,
    [property: Required, MinLength(1), MaxLength(16)] IReadOnlyList<string> TargetSchemas);
