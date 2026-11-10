using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.Mapping;
using Mulse.Modules.Pipeline;

namespace Service.BizTalkImport;

internal sealed class BizTalkSchemaSourceCatalog
{
    private const int MaximumDeclarations = 4096;
    private const int MaximumCandidates = 32;
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly Dictionary<string, List<MigrationSchemaSourceCandidate>> _byClass = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];
    private int _warningCount;
    public IReadOnlyList<string> Warnings => _warnings;
    internal IEnumerable<MigrationSchemaReferenceEvidence> DeclaredSchemas => _byClass.Keys.Select(Resolve);

    public static BizTalkSchemaSourceCatalog Read(IEnumerable<string> projectPaths)
    {
        var catalog = new BizTalkSchemaSourceCatalog();
        var declarations = 0;
        foreach (var project in projectPaths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = XmlMappingSchemas.ReadXmlFile(project);
                var roots = document.Descendants().Where(element => element.Name.LocalName == "RootNamespace").ToArray();
                var rootNamespace = roots.Length == 1 && !Conditional(roots[0]) ? roots[0].Value.Trim() : null;
                var items = document.Descendants().Where(element => element.Name.LocalName == "Schema").ToArray();
                var defaults = items.Any(item => item.Ancestors().Any(parent => parent.Name.LocalName == "ItemDefinitionGroup"));
                foreach (var item in items)
                {
                    if (item.Ancestors().Any(parent => parent.Name.LocalName == "ItemDefinitionGroup")) continue;
                    if (++declarations > MaximumDeclarations)
                    {
                        catalog.Warn("Schema declaration limit exceeded; source ownership evidence is incomplete.");
                        return catalog;
                    }
                    var include = (string?)item.Attribute("Include");
                    var namespaceMetadata = item.Elements().Where(element => element.Name.LocalName == "Namespace").ToArray();
                    var typeMetadata = item.Elements().Where(element => element.Name.LocalName == "TypeName").ToArray();
                    if (Conditional(item) || item.Parent?.Name.LocalName != "ItemGroup" || namespaceMetadata.Length > 1 || typeMetadata.Length > 1
                        || namespaceMetadata.Concat(typeMetadata).Any(Conditional) || !Literal(include)
                        || (defaults && (namespaceMetadata.Length != 1 || typeMetadata.Length != 1)))
                    {
                        catalog.Warn($"{project}, line {Line(item)}: schema metadata is conditional, dynamic, duplicated, target-generated or depends on unevaluated item defaults.");
                        continue;
                    }
                    var relative = include!.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    var parts = relative.Split(Path.DirectorySeparatorChar);
                    var conventionalPath = !Path.IsPathRooted(relative) && !parts.Any(part => part is ".." or "." or "")
                        && !item.Elements().Any(element => element.Name.LocalName == "Link");
                    var schemaNamespace = namespaceMetadata.Length == 1 ? namespaceMetadata[0].Value.Trim()
                        : conventionalPath && rootNamespace is not null ? string.Join('.', new[] { rootNamespace }.Concat(parts.SkipLast(1))) : null;
                    var typeName = typeMetadata.Length == 1 ? typeMetadata[0].Value.Trim() : Path.GetFileNameWithoutExtension(relative);
                    if (schemaNamespace is null || !DottedName(schemaNamespace, allowEmpty: true) || !Identifier.IsMatch(typeName))
                    {
                        catalog.Warn($"{project}, line {Line(item)}: schema class name requires explicit literal Namespace/TypeName metadata or supported RootNamespace/relative-path conventions.");
                        continue;
                    }
                    var source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, relative));
                    var className = schemaNamespace.Length == 0 ? typeName : schemaNamespace + "." + typeName;
                    var method = namespaceMetadata.Length == 1 && typeMetadata.Length == 1 ? "ExplicitNamespaceAndTypeName" : "LiteralMetadataWithNamingConventions";
                    var candidate = new MigrationSchemaSourceCandidate(project, source, className, method, Line(item), File.Exists(source));
                    if (!catalog._byClass.TryGetValue(className, out var matches)) catalog._byClass[className] = matches = [];
                    if (!matches.Any(match => string.Equals(match.SourcePath, source, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(match.SourceProjectPath, project, StringComparison.OrdinalIgnoreCase))) matches.Add(candidate);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or XmlException or NotSupportedException)
            {
                catalog.Warn($"{project}: schema source metadata could not be inspected ({exception.GetType().Name}); ownership evidence is incomplete.");
            }
        }
        return catalog;
    }

    public MigrationSchemaReferenceEvidence Resolve(string reference)
    {
        var matches = _byClass.GetValueOrDefault(reference) ?? [];
        var status = matches.Count > 1 ? "Ambiguous" : matches.Count == 0 ? "Unresolved"
            : !matches[0].SourceExists ? "MissingSource" : _warningCount > 0 ? "IncompleteCatalog" : "SourceCandidate";
        var limitation = "Source metadata evidence only, not evaluated MSBuild output or deployed assembly identity. Class names are matched case-sensitively. No schema text was loaded, no map was wired, and policy selection is unverified.";
        if (_warningCount > _warnings.Count) limitation += $" {_warningCount - _warnings.Count} additional catalog warning(s) omitted.";
        return new(reference, status, matches.Count, Math.Max(0, matches.Count - MaximumCandidates),
            matches.Take(MaximumCandidates).ToArray(), _warningCount, limitation);
    }

    private static string FullPath(string baseDirectory, string reference)
        => Path.GetFullPath(Path.Combine(baseDirectory, reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Resolves a relative file reference (for example a map's <c>.\Schema.xsd</c>) to a schema file that a project declares; anything else returns null.</summary>
    public string? ResolveFileReference(string baseDirectory, string reference)
    {
        if (!Literal(reference) || Path.IsPathRooted(reference) || Uri.TryCreate(reference, UriKind.Absolute, out _)) return null;
        var full = FullPath(baseDirectory, reference);
        return _byClass.Values.SelectMany(matches => matches).Any(candidate => string.Equals(candidate.SourcePath, full, StringComparison.OrdinalIgnoreCase) && candidate.SourceExists) ? full : null;
    }

    public IReadOnlyList<string> ReadSchemaDocuments(string sourcePath)
    {
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        var declared = _byClass.Values.SelectMany(matches => matches).Select(candidate => candidate.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var documents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Read(string path)
        {
            if (documents.ContainsKey(path)) return;
            if (!declared.Contains(path)) throw new NotSupportedException($"Imported schema '{Path.GetFileName(path)}' is not declared by any analyzed project (a BizTalk product schema assembly, for example); add the owning project to the source paths.");
            if (documents.Count >= 16) throw new NotSupportedException("Schema imports exceed the 16-document limit.");
            var document = XmlMappingSchemas.ReadXmlFile(path);
            if (document.Root?.Name != xs + "schema") throw new NotSupportedException("Declared schema source is not XSD.");
            documents.Add(path, document.ToString(SaveOptions.DisableFormatting));
            foreach (var import in document.Root.Elements().Where(element => element.Name == xs + "import" || element.Name == xs + "include" || element.Name == xs + "redefine"))
            {
                var location = (string?)import.Attribute("schemaLocation");
                if (import.Name != xs + "import" || !Literal(location) || Uri.TryCreate(location, UriKind.Absolute, out _)
                    || Path.IsPathRooted(location!)) throw new NotSupportedException("Only xs:import of a declared project schema (relative file or BizTalk class name) is supported; no resolver or external URL is used.");
                var target = FullPath(Path.GetDirectoryName(path)!, location!);
                if (!declared.Contains(target) && _byClass.TryGetValue(location!, out var byClass))
                {
                    // BizTalk imports often name the schema class (Namespace.TypeName) instead of a file.
                    var sources = byClass.Select(candidate => candidate.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (sources.Length != 1) throw new NotSupportedException($"Schema import '{location}' matches {sources.Length} declared schema files; it must match exactly one.");
                    target = sources[0];
                }
                Read(target);
            }
        }
        Read(sourcePath);
        return documents.Values.ToArray();
    }

    private void Warn(string message)
    {
        _warningCount++;
        if (_warnings.Count < 32) _warnings.Add(message);
    }

    internal static bool Conditional(XElement element) => element.AncestorsAndSelf().Any(parent =>
        !string.IsNullOrWhiteSpace((string?)parent.Attribute("Condition")) || parent.Name.LocalName is "Target" or "Choose" or "When" or "Otherwise");
    internal static bool Literal(string? value) => !string.IsNullOrWhiteSpace(value) && value.IndexOfAny(['$', '%', '@', '*', '?', ';']) < 0;
    internal static bool DottedName(string value, bool allowEmpty) => value.Length == 0 ? allowEmpty : value.Split('.').All(Identifier.IsMatch);
    internal static bool IsIdentifier(string value) => Identifier.IsMatch(value);
    internal static int Line(XElement element) => ((IXmlLineInfo)element).LineNumber;
}
