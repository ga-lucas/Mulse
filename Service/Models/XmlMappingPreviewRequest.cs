using System.ComponentModel.DataAnnotations;

namespace Service.Models;

public sealed record XmlMappingPreviewRequest(
    [property: Required, StringLength(262_144)] string MappingJson,
    [property: Required, StringLength(1_000_000)] string SampleXml);

public sealed record XmlMappingPreviewResponse(string OutputXml, string Xslt);
