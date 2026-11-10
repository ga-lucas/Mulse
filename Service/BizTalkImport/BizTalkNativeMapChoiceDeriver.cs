using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.Mapping;
using Service.Mapping;
using Service.Models;

namespace Service.BizTalkImport;

internal sealed record DerivedMapChoices(IReadOnlyList<BizTalkDerivedMapChoiceResponse> Report, string CasesJson);

/// <summary>
/// Links exact policy map-name strings (assembly-qualified BizTalk map type names) to analyzed .btm maps and converts the ones the native map
/// converter can prove into render-dispatch cases. Anything unmatched, ambiguous or unconvertible is reported and left without a case.
/// Class identity is source evidence only (namespace + type name, assembly simple name); version and public key are not verifiable from source.
/// </summary>
internal static class BizTalkNativeMapChoiceDeriver
{
    private const int MaximumCases = 128;
    private const int MaximumCasesJsonLength = 262144;
    private const string Limitation = "Native replacement choice derived from the map links; not a verified equivalent of the deployed BizTalk map. Preview synthetic inputs against trusted expected output.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record MapClass(string ClassName, string Assembly, string MapPath);

    public static DerivedMapChoices Derive(IEnumerable<string> projectPaths, IReadOnlyList<PolicyMapNameLiteral> literals,
        BizTalkSchemaSourceCatalog schemas, string? explicitCasesJson, CancellationToken cancellationToken)
    {
        var cases = explicitCasesJson is null ? [] : RenderDispatchDefinition.Read(explicitCasesJson, allowEmpty: true).ToList();
        var explicitValues = cases.Select(static entry => entry.Value).ToHashSet(StringComparer.Ordinal);
        var maps = ReadMapClasses(projectPaths);
        var report = new List<BizTalkDerivedMapChoiceResponse>();
        foreach (var group in literals.GroupBy(static literal => literal.Value, StringComparer.Ordinal).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var policies = group.Select(static literal => $"{literal.Policy} {literal.Version} ({literal.Field})").Distinct(StringComparer.Ordinal).Take(8).ToArray();
            BizTalkDerivedMapChoiceResponse Result(string status, string? path, string detail, IReadOnlyList<string>? warnings = null)
                => new(group.Key, status, path, detail, policies, warnings ?? []);
            if (explicitValues.Contains(group.Key))
            {
                report.Add(Result("ExplicitChoice", null, "An explicit native choice already exists for this value and was kept unchanged."));
                continue;
            }
            var parts = group.Key.Split(',', StringSplitOptions.TrimEntries);
            var typeName = parts[0];
            var assembly = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
            var byClass = maps.Where(map => string.Equals(map.ClassName, typeName, StringComparison.Ordinal)).ToArray();
            var matches = assembly is null ? byClass : byClass.Where(map => string.Equals(map.Assembly, assembly, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                report.Add(Result("NoMatchingMap", null, byClass.Length > 0
                    ? $"Map class '{typeName}' exists only in assembl{(byClass.Length == 1 ? "y" : "ies")} {string.Join(", ", byClass.Select(static map => map.Assembly).Distinct())}, not '{assembly}'."
                    : $"No analyzed project declares map class '{typeName}'. Add the owning project to the source paths or supply a manual choice."));
                continue;
            }
            if (matches.Select(static map => map.MapPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                report.Add(Result("AmbiguousMap", null, $"{matches.Length} analyzed maps declare '{typeName}'; supply a manual choice."));
                continue;
            }
            var map = matches[0];
            NativeMapConversion conversion;
            try
            {
                var document = XmlMappingSchemas.ReadXmlFile(map.MapPath);
                var source = SchemaDocuments(document, "SrcTree", schemas, out var sourceProblem);
                var target = SchemaDocuments(document, "TrgTree", schemas, out var targetProblem);
                if (source is null || target is null)
                {
                    report.Add(Result("SchemaUnresolved", map.MapPath, sourceProblem ?? targetProblem!));
                    continue;
                }
                conversion = BizTalkNativeMapConverter.Convert(document.ToString(SaveOptions.DisableFormatting), source, target, cancellationToken, embedSchemas: false);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or XmlException)
            {
                report.Add(Result("ConversionFailed", map.MapPath, Truncate(exception.Message)));
                continue;
            }
            var entry = new RenderDispatchCase
            {
                Value = group.Key, Module = "xml-mapping-render", Settings = new() { ["mappingJson"] = conversion.MappingJson }
            };
            if (cases.Count >= MaximumCases || JsonSerializer.Serialize(cases.Append(entry), Json).Length > MaximumCasesJsonLength)
            {
                report.Add(Result("CapExceeded", map.MapPath, "Adding this choice would exceed the 128-case or 262,144-character render-dispatch limit; supply it manually or narrow the selected policy versions."));
                continue;
            }
            cases.Add(entry);
            report.Add(conversion.PendingFunctions.Count == 0
                ? Result("Converted", map.MapPath, Limitation, conversion.Warnings)
                : Result("ConvertedWithStubs", map.MapPath, $"{Limitation} {conversion.PendingFunctions.Count} scripting functoid(s) are unimplemented stubs ({Truncate(string.Join(", ", conversion.PendingFunctions))}); this choice fails closed until each has a formula.", conversion.Warnings));
        }
        return new(report, JsonSerializer.Serialize(cases, Json));
    }

    private static IReadOnlyList<string>? SchemaDocuments(XDocument map, string tree, BizTalkSchemaSourceCatalog schemas, out string? problem)
    {
        problem = null;
        var location = map.Root?.Elements(tree).SingleOrDefault()?.Elements("Reference").SingleOrDefault()?.Attribute("Location")?.Value;
        if (string.IsNullOrWhiteSpace(location))
        {
            problem = $"The map has no single {tree} schema reference.";
            return null;
        }
        var evidence = schemas.Resolve(location);
        if (evidence.Status != "SourceCandidate" || evidence.Candidates.Count != 1)
        {
            problem = $"Schema '{location}' is {evidence.Status} ({evidence.Candidates.Count} source candidates); add the owning project to the source paths.";
            return null;
        }
        try { return schemas.ReadSchemaDocuments(evidence.Candidates[0].SourcePath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or XmlException)
        {
            problem = $"Schema '{location}' could not be read: {Truncate(exception.Message)}";
            return null;
        }
    }

    private static List<MapClass> ReadMapClasses(IEnumerable<string> projectPaths)
    {
        var result = new List<MapClass>();
        foreach (var project in projectPaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = XmlMappingSchemas.ReadXmlFile(project);
                var roots = document.Descendants().Where(static element => element.Name.LocalName == "RootNamespace").ToArray();
                var rootNamespace = roots.Length == 1 && !BizTalkSchemaSourceCatalog.Conditional(roots[0]) ? roots[0].Value.Trim() : null;
                var assemblies = document.Descendants().Where(static element => element.Name.LocalName == "AssemblyName").ToArray();
                var assembly = assemblies.Length == 1 && !BizTalkSchemaSourceCatalog.Conditional(assemblies[0]) && BizTalkSchemaSourceCatalog.Literal(assemblies[0].Value)
                    ? assemblies[0].Value.Trim() : Path.GetFileNameWithoutExtension(project);
                foreach (var item in document.Descendants().Where(static element => element.Name.LocalName == "Map"
                    && element.Parent?.Name.LocalName == "ItemGroup" && !element.Ancestors().Any(static parent => parent.Name.LocalName == "ItemDefinitionGroup")))
                {
                    var include = (string?)item.Attribute("Include");
                    var namespaces = item.Elements().Where(static element => element.Name.LocalName == "Namespace").ToArray();
                    var types = item.Elements().Where(static element => element.Name.LocalName == "TypeName").ToArray();
                    if (BizTalkSchemaSourceCatalog.Conditional(item) || !BizTalkSchemaSourceCatalog.Literal(include) || namespaces.Length > 1 || types.Length > 1
                        || namespaces.Concat(types).Any(BizTalkSchemaSourceCatalog.Conditional)) continue;
                    var relative = include!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    var parts = relative.Split(Path.DirectorySeparatorChar);
                    var conventional = !Path.IsPathRooted(relative) && !parts.Any(static part => part is ".." or "." or "") && !item.Elements().Any(static element => element.Name.LocalName == "Link");
                    var mapNamespace = namespaces.Length == 1 ? namespaces[0].Value.Trim()
                        : conventional && rootNamespace is not null ? string.Join('.', new[] { rootNamespace }.Concat(parts.SkipLast(1))) : null;
                    var typeName = types.Length == 1 ? types[0].Value.Trim() : Path.GetFileNameWithoutExtension(relative);
                    if (mapNamespace is null || !BizTalkSchemaSourceCatalog.DottedName(mapNamespace, allowEmpty: true) || !BizTalkSchemaSourceCatalog.IsIdentifier(typeName)) continue;
                    var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, relative));
                    if (File.Exists(path)) result.Add(new(mapNamespace.Length == 0 ? typeName : mapNamespace + "." + typeName, assembly, path));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or XmlException or NotSupportedException)
            {
                // An unreadable project simply contributes no map classes; its values report NoMatchingMap.
            }
        }
        return result;
    }

    private static string Truncate(string text) => text.Length <= 600 ? text : text[..600] + "…";
}
