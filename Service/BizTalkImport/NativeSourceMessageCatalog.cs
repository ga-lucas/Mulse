using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;
using Mulse.Modules.Mapping;
using Mulse.Modules.Orchestration;

namespace Service.BizTalkImport;

internal sealed class NativeSourceMessageCatalog
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private static readonly XNamespace BizTalk = "http://schemas.microsoft.com/BizTalk/2003";
    private readonly Dictionary<string, NativeMessageFact> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _failures = new(StringComparer.Ordinal);

    public static NativeSourceMessageCatalog Read(IEnumerable<OdxNode> declarations, BizTalkSchemaSourceCatalog schemas)
    {
        var result = new NativeSourceMessageCatalog();
        foreach (var group in declarations.GroupBy(node => node.Name, StringComparer.Ordinal))
        {
            var name = group.Key;
            if (group.Count() != 1) { result._failures[name] = "Message declaration is ambiguous."; continue; }
            var declaration = group.Single();
            var evidence = schemas.Resolve(declaration.Properties.GetValueOrDefault("Type", ""));
            if (evidence.Status != "SourceCandidate" || evidence.Candidates.Count != 1)
            { result._failures[name] = "Message schema has no unique complete source ownership evidence (" + evidence.Status + ")."; continue; }
            try
            {
                var document = XmlMappingSchemas.ReadXmlFile(evidence.Candidates[0].SourcePath);
                var schema = document.Root;
                if (schema?.Name != Xs + "schema") throw new NotSupportedException("Message source must be an XSD schema.");
                var roots = schema.Elements(Xs + "element").ToArray();
                if (roots.Length != 1) throw new NotSupportedException("Message schema requires one explicit global root.");
                var root = roots[0];
                var compiled = XmlMappingSchemas.Compile(schemas.ReadSchemaDocuments(evidence.Candidates[0].SourcePath));
                var rootName = new XmlQualifiedName((string?)root.Attribute("name") ?? "", (string?)schema.Attribute("targetNamespace") ?? "");
                var compiledRoot = compiled.GlobalElements[rootName] as XmlSchemaElement ?? throw new NotSupportedException("Message schema root did not compile.");
                var members = new Dictionary<string, NativeMessageFact>(StringComparer.Ordinal);
                var identities = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.Elements(Xs + "annotation").Elements(Xs + "appinfo").Elements(BizTalk + "properties")
                    .Elements(BizTalk + "property").Where(property => (string?)property.Attribute("distinguished") == "true"))
                {
                    string? member = null;
                    try
                    {
                        var path = (string?)property.Attribute("xpath") ?? throw new NotSupportedException("Distinguished field has no exact XPath.");
                        var fact = NativeSourceXmlFactReader.Read(name, "string(" + path + ")");
                        if (fact.Steps.Count < 2) throw new NotSupportedException("Distinguished member identity is absent.");
                        member = name + "." + string.Join('.', fact.Steps.Skip(1).Select(part => part.Name));
                        if (!identities.Add(member)) throw new NotSupportedException("Distinguished member identity is ambiguous.");
                        var elements = new List<XmlSchemaElement> { compiledRoot };
                        for (var index = 0; index < fact.Steps.Count; index++)
                        {
                            var step = fact.Steps[index];
                            if (step.Attribute) throw new NotSupportedException("Distinguished attribute members require explicit source semantics.");
                            if (step.Descendant || step.FilterAttribute.Length != 0) throw new NotSupportedException("Distinguished members require exact child paths, not descendant or filtered steps.");
                            var element = elements.SingleOrDefault(candidate => candidate.QualifiedName.Name == step.Name && candidate.QualifiedName.Namespace == step.Namespace)
                                ?? throw new NotSupportedException("Distinguished field does not match an exact compiled schema path.");
                            if (element.MaxOccurs != 1)
                                throw new NotSupportedException("Repeated distinguished fields require explicit indexing.");
                            if (index == fact.Steps.Count - 1) fact.Kind = ScalarKind(element);
                            else
                            {
                                if (element.ElementSchemaType is not XmlSchemaComplexType complex || complex.ContentTypeParticle is not XmlSchemaGroupBase particle
                                    || particle is not (XmlSchemaSequence or XmlSchemaAll) || particle.MaxOccurs != 1
                                    || particle.Items.Cast<XmlSchemaObject>().Any(child => child is not XmlSchemaElement))
                                    throw new NotSupportedException("Distinguished path requires a nonrepeating sequence/all of explicit elements.");
                                elements = particle.Items.Cast<XmlSchemaElement>().ToList();
                            }
                        }
                        members.Add(member, fact);
                    }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or InvalidOperationException)
                    {
                        if (member is not null) members.Remove(member);
                        result._failures[member ?? name] = "Distinguished field translation failed: " + exception.Message;
                    }
                }
                foreach (var pair in members) result._members.Add(pair.Key, pair.Value);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            { result._failures[name] = "Message schema field translation failed: " + exception.Message; }
        }
        return result;
    }

    public NativeMessageFact Resolve(string member)
    {
        if (_members.TryGetValue(member, out var fact)) return new()
        {
            Kind = fact.Kind, MessageName = fact.MessageName,
            Steps = fact.Steps.Select(step => new NativeXmlStep { Name = step.Name, Namespace = step.Namespace, Attribute = step.Attribute }).ToList()
        };
        var message = member.Split('.')[0];
        throw new NotSupportedException(_failures.GetValueOrDefault(member, _failures.GetValueOrDefault(message,
            "Source member has no exact distinguished schema-field declaration; ordinary XML members and context properties are not inferred.")));
    }

    public void ValidatePolicyMessage(string message, XmlRulePolicy policy)
    {
        foreach (var field in policy.Rules.SelectMany(rule => rule.Conditions.Concat(rule.Assignments)))
        {
            var fact = Resolve(message + "." + field.Name);
            var expected = field.Type == "boolean" ? NativeMessageFactKind.XmlBoolean : NativeMessageFactKind.XmlString;
            if (fact.Kind != expected || fact.Steps.Count != 2 || fact.Steps[0].Name != policy.RootName || fact.Steps[0].Namespace != policy.RootNamespace
                || fact.Steps[1].Name != field.Name || fact.Steps[1].Namespace != field.Namespace || fact.Steps.Any(step => step.Attribute))
                throw new NotSupportedException("Policy fields require exact direct-child distinguished schema identities and matching scalar types.");
        }
    }

    private static NativeMessageFactKind ScalarKind(XmlSchemaElement element)
    {
        if (element.SchemaTypeName.Namespace != Xs.NamespaceName || element.ElementSchemaType is not XmlSchemaSimpleType)
            throw new NotSupportedException("Distinguished scalar type requires explicit XML Schema built-in identity.");
        return element.SchemaTypeName.Name switch
        {
            "string" => NativeMessageFactKind.XmlString, "int" => NativeMessageFactKind.XmlInt32, "boolean" => NativeMessageFactKind.XmlBoolean,
            _ => throw new NotSupportedException("Distinguished scalar supports only XSD string, int and boolean; no implicit datatype conversion.")
        };
    }
}
