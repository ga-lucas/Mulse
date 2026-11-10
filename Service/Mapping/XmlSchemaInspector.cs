using System.Xml;
using System.Xml.Schema;

namespace Service.Mapping;

public sealed record XmlSchemaTree(
    IReadOnlyDictionary<string, string> Namespaces,
    IReadOnlyList<XmlSchemaRoot> Roots);

public sealed record XmlSchemaRoot(string Name, string Path, IReadOnlyList<XmlSchemaField> Fields);

public sealed record XmlSchemaField(
    string Path,
    string LocalPath,
    bool Scalar,
    bool Required,
    bool Repeating,
    bool HasDefaultOrFixed,
    string? UnsupportedReason,
    bool SelfRepeating = false,
    bool Nillable = false,
    bool RequiredInParent = true,
    string? FixedValue = null,
    bool RequiredHere = true,
    bool GroupRepeating = false);

public static class XmlSchemaInspector
{
    public const int MaximumCharacters = Mulse.Modules.Mapping.XmlMappingSchemas.MaximumCharacters;
    /// <summary>Large message schemas (for example HL7 with repeating segment groups) expand to many thousands of nodes.</summary>
    public const int MaximumNodes = 16384;

    public static XmlSchemaTree Analyze(IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
        => Analyze(documents, null, cancellationToken);

    /// <summary>Analyzes only global elements with the given local name (all roots when null), so a large imported schema set is not counted in full.</summary>
    public static XmlSchemaTree Analyze(IReadOnlyList<string> documents, string? rootLocalName, CancellationToken cancellationToken = default)
    {
        var schemas = Mulse.Modules.Mapping.XmlMappingSchemas.Compile(documents, cancellationToken);
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var roots = new List<XmlSchemaRoot>();
        var count = 0;
        foreach (var element in schemas.GlobalElements.Values.Cast<XmlSchemaElement>()
                     .Where(element => rootLocalName is null || element.QualifiedName.Name == rootLocalName)
                     .OrderBy(static element => element.QualifiedName.ToString(), StringComparer.Ordinal))
        {
            var fields = new List<XmlSchemaField>();
            VisitElement(element, string.Empty, string.Empty, true, true, true, false, false, null, new HashSet<XmlSchemaType>(), fields);
            roots.Add(new(element.QualifiedName.ToString(), fields[0].Path, fields));
        }
        if (roots.Count == 0) throw new ArgumentException("The supplied schemas have no global root elements.");
        return new(namespaces, roots);

        string Name(XmlQualifiedName name)
        {
            if (string.IsNullOrEmpty(name.Namespace)) return name.Name;
            if (name.Namespace == "http://www.w3.org/XML/1998/namespace") return "xml:" + name.Name;
            var prefix = namespaces.FirstOrDefault(entry => entry.Value == name.Namespace).Key;
            if (prefix is null)
            {
                if (namespaces.Count == 32) throw new ArgumentException("Schema trees support at most 32 namespace URIs.");
                prefix = "n" + (namespaces.Count + 1);
                namespaces.Add(prefix, name.Namespace);
            }
            return prefix + ":" + name.Name;
        }

        void Add(XmlSchemaField field, List<XmlSchemaField> fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaximumNodes) throw new ArgumentException($"Schema trees exceed the {MaximumNodes:N0}-node inspection limit. Supply a smaller schema.");
            fields.Add(field);
        }

        void VisitElement(XmlSchemaElement element, string parent, string localParent, bool required, bool chain, bool here, bool repeating, bool groupRepeats,
            string? unsupported, HashSet<XmlSchemaType> ancestors, List<XmlSchemaField> fields)
        {
            if (parent.Count(static character => character == '/') >= 32)
                throw new ArgumentException("Schema trees exceed the 32-level inspection limit.");
            var path = parent + "/" + Name(element.QualifiedName);
            var localPath = localParent + "/" + element.QualifiedName.Name;
            required &= element.MinOccurs > 0;
            chain &= element.MinOccurs > 0;
            here &= element.MinOccurs > 0;
            repeating |= element.MaxOccurs > 1;
            var type = element.ElementSchemaType ?? throw new ArgumentException($"Schema type for '{path}' is unresolved.");
            var complex = type as XmlSchemaComplexType;
            var scalar = type.Datatype is not null || complex?.ContentType == XmlSchemaContentType.TextOnly;
            if (element.IsAbstract || !element.SubstitutionGroup.IsEmpty
                || schemas.GlobalElements.Values.Cast<XmlSchemaElement>().Any(candidate => candidate.SubstitutionGroup == element.QualifiedName))
                unsupported ??= "Abstract/substitution-group elements need explicit handling.";
            if (complex?.IsMixed == true) unsupported ??= "Mixed content is not supported by scalar mappings.";
            if (ancestors.Contains(type)) unsupported ??= "Recursive schema type; descendants are not expanded.";
            Add(new(path, localPath, scalar, required, repeating, element.DefaultValue is not null || element.FixedValue is not null, unsupported,
                element.MaxOccurs > 1 || groupRepeats, element.IsNillable, chain, element.FixedValue, here, groupRepeats), fields);
            if (unsupported is not null) return;
            ancestors.Add(type);
            if (complex is not null)
            {
                foreach (var attribute in complex.AttributeUses.Values.Cast<XmlSchemaAttribute>().OrderBy(static attribute => attribute.QualifiedName.ToString(), StringComparer.Ordinal))
                {
                    if (attribute.Use == XmlSchemaUse.Prohibited) continue;
                    Add(new(path + "/@" + Name(attribute.QualifiedName), localPath + "/@" + attribute.QualifiedName.Name, true,
                        required && attribute.Use == XmlSchemaUse.Required, repeating,
                        attribute.DefaultValue is not null || attribute.FixedValue is not null,
                        attribute.QualifiedName.Namespace == "http://www.w3.org/XML/1998/namespace" ? "Reserved XML namespace attributes require explicit handling." : null,
                        RequiredInParent: attribute.Use == XmlSchemaUse.Required, FixedValue: attribute.FixedValue,
                        RequiredHere: attribute.Use == XmlSchemaUse.Required), fields);
                }
                if (complex.AttributeWildcard is not null)
                    Add(new(path + "/@*", localPath + "/@*", false, false, repeating, false, "Wildcard attributes require explicit mapping."), fields);
                VisitParticle(complex.ContentTypeParticle, path, localPath, required, true, true, repeating, false, null, ancestors, fields);
            }
            ancestors.Remove(type);
        }

        void VisitParticle(XmlSchemaParticle particle, string parent, string localParent, bool required, bool chain, bool here, bool repeating, bool groupRepeats,
            string? unsupported, HashSet<XmlSchemaType> ancestors, List<XmlSchemaField> fields)
        {
            required &= particle.MinOccurs > 0;
            chain &= particle.MinOccurs > 0;
            here &= particle.MinOccurs > 0;
            repeating |= particle.MaxOccurs > 1;
            // A repeating sequence repeats each member element as a unit; a map can treat every member as its own repeating record. Other repeating groups stay unsupported.
            if (particle is XmlSchemaSequence { MaxOccurs: > 1 }) groupRepeats = true;
            else if (particle is XmlSchemaGroupBase { MaxOccurs: > 1 })
                unsupported ??= "Repeating model groups require explicit handling.";
            if (particle is XmlSchemaElement element)
                VisitElement(element, parent, localParent, required, chain, here, repeating, groupRepeats, unsupported, ancestors, fields);
            else if (particle is XmlSchemaGroupBase group)
            {
                if (group is XmlSchemaChoice)
                    Add(new(parent + "/(choice)", localParent + "/(choice)", false, required, repeating, false, "Choice groups require explicit branch handling."), fields);
                else if (group.MinOccurs == 0 && group.Items.OfType<XmlSchemaParticle>().Any(item => item.MinOccurs > 0))
                    Add(new(parent + "/(optional-group)", localParent + "/(optional-group)", false, false, repeating, false, "Optional model groups with required members need explicit group-presence handling."), fields);
                foreach (var item in group.Items.OfType<XmlSchemaParticle>())
                    VisitParticle(item, parent, localParent, group is XmlSchemaChoice ? false : required, group is XmlSchemaChoice ? false : chain,
                        group is XmlSchemaChoice ? false : here, repeating, groupRepeats, unsupported, ancestors, fields);
            }
            else if (particle is XmlSchemaAny)
                Add(new(parent + "/*", localParent + "/*", false, required, repeating, false, "Wildcard elements require explicit mapping."), fields);
        }
    }
}
