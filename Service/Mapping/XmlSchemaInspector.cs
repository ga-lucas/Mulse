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
    string? UnsupportedReason);

public static class XmlSchemaInspector
{
    public const int MaximumCharacters = Mulse.Modules.Mapping.XmlMappingSchemas.MaximumCharacters;

    public static XmlSchemaTree Analyze(IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        var schemas = Mulse.Modules.Mapping.XmlMappingSchemas.Compile(documents, cancellationToken);
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var roots = new List<XmlSchemaRoot>();
        var count = 0;
        foreach (var element in schemas.GlobalElements.Values.Cast<XmlSchemaElement>()
                     .OrderBy(static element => element.QualifiedName.ToString(), StringComparer.Ordinal))
        {
            var fields = new List<XmlSchemaField>();
            VisitElement(element, string.Empty, string.Empty, true, false, null, new HashSet<XmlSchemaType>(), fields);
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
            if (++count > 2048) throw new ArgumentException("Schema trees exceed the 2,048-node inspection limit. Supply a smaller schema.");
            fields.Add(field);
        }

        void VisitElement(XmlSchemaElement element, string parent, string localParent, bool required, bool repeating,
            string? unsupported, HashSet<XmlSchemaType> ancestors, List<XmlSchemaField> fields)
        {
            if (parent.Count(static character => character == '/') >= 32)
                throw new ArgumentException("Schema trees exceed the 32-level inspection limit.");
            var path = parent + "/" + Name(element.QualifiedName);
            var localPath = localParent + "/" + element.QualifiedName.Name;
            required &= element.MinOccurs > 0;
            repeating |= element.MaxOccurs > 1;
            var type = element.ElementSchemaType ?? throw new ArgumentException($"Schema type for '{path}' is unresolved.");
            var complex = type as XmlSchemaComplexType;
            var scalar = type.Datatype is not null || complex?.ContentType == XmlSchemaContentType.TextOnly;
            if (element.IsAbstract || !element.SubstitutionGroup.IsEmpty
                || schemas.GlobalElements.Values.Cast<XmlSchemaElement>().Any(candidate => candidate.SubstitutionGroup == element.QualifiedName))
                unsupported ??= "Abstract/substitution-group elements need explicit handling.";
            if (element.IsNillable) unsupported ??= "Nillable elements require explicit nil-preservation handling.";
            if (complex?.IsMixed == true) unsupported ??= "Mixed content is not supported by scalar mappings.";
            if (ancestors.Contains(type)) unsupported ??= "Recursive schema type; descendants are not expanded.";
            Add(new(path, localPath, scalar, required, repeating, element.DefaultValue is not null || element.FixedValue is not null, unsupported), fields);
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
                        attribute.QualifiedName.Namespace == "http://www.w3.org/XML/1998/namespace" ? "Reserved XML namespace attributes require explicit handling." : null), fields);
                }
                if (complex.AttributeWildcard is not null)
                    Add(new(path + "/@*", localPath + "/@*", false, false, repeating, false, "Wildcard attributes require explicit mapping."), fields);
                VisitParticle(complex.ContentTypeParticle, path, localPath, required, repeating, ancestors, fields);
            }
            ancestors.Remove(type);
        }

        void VisitParticle(XmlSchemaParticle particle, string parent, string localParent, bool required, bool repeating,
            HashSet<XmlSchemaType> ancestors, List<XmlSchemaField> fields)
        {
            required &= particle.MinOccurs > 0;
            repeating |= particle.MaxOccurs > 1;
            if (particle is XmlSchemaElement element)
                VisitElement(element, parent, localParent, required, repeating, null, ancestors, fields);
            else if (particle is XmlSchemaGroupBase group)
            {
                if (group is XmlSchemaChoice)
                    Add(new(parent + "/(choice)", localParent + "/(choice)", false, required, repeating, false, "Choice groups require explicit branch handling."), fields);
                else if (group.MinOccurs == 0 && group.Items.OfType<XmlSchemaParticle>().Any(item => item.MinOccurs > 0))
                    Add(new(parent + "/(optional-group)", localParent + "/(optional-group)", false, false, repeating, false, "Optional model groups with required members need explicit group-presence handling."), fields);
                foreach (var item in group.Items.OfType<XmlSchemaParticle>())
                    VisitParticle(item, parent, localParent, group is XmlSchemaChoice ? false : required, repeating, ancestors, fields);
            }
            else if (particle is XmlSchemaAny)
                Add(new(parent + "/*", localParent + "/*", false, required, repeating, false, "Wildcard elements require explicit mapping."), fields);
        }
    }
}
