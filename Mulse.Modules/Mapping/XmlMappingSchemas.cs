using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Mulse.Modules.Mapping;

public static class XmlMappingSchemas
{
    public const int MaximumCharacters = 1_000_000;

    public static XDocument ReadXmlFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 2_000_000) throw new ArgumentException("Mapping/project XML files must not exceed 2,000,000 bytes.");
        using var reader = new StreamReader(stream);
        var characters = new char[MaximumCharacters + 1];
        var length = reader.ReadBlock(characters, 0, characters.Length);
        if (length > MaximumCharacters) throw new ArgumentException("Mapping/project XML files must not exceed 1,000,000 characters.");
        return ReadXml(new string(characters, 0, length));
    }

    public static XDocument ReadXml(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumCharacters)
            throw new ArgumentException("XML is required and must not exceed 1,000,000 characters.");
        try
        {
            using (var probe = XmlReader.Create(new StringReader(text), Settings()))
            {
                var nodes = 0;
                while (probe.Read())
                {
                    if (probe.Depth > 64 || ++nodes > 50_000)
                        throw new ArgumentException("Mapping XML exceeds the 64-level / 50,000-node inspection limit.");
                }
            }
            using var reader = XmlReader.Create(new StringReader(text), Settings());
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException exception) { throw new ArgumentException($"Invalid mapping/schema XML: {exception.Message}", exception); }
    }

    public static XmlSchemaSet Compile(IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        if (documents is null || documents.Count is < 1 or > 16
            || documents.Any(string.IsNullOrWhiteSpace) || documents.Sum(static text => (long)text.Length) > MaximumCharacters)
            throw new ArgumentException("Supply 1 to 16 XSD documents, totaling at most 1,000,000 characters.");
        var schemas = new XmlSchemaSet { XmlResolver = null };
        schemas.ValidationEventHandler += (_, args) => throw new ArgumentException($"XSD compilation failed: {args.Message}");
        try
        {
            var parsed = new List<XmlSchema>();
            foreach (var text in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var reader = ReadXml(text).CreateReader();
                parsed.Add(XmlSchema.Read(reader, (_, args) => throw new ArgumentException($"Invalid XSD: {args.Message}"))
                    ?? throw new ArgumentException("An XSD document is required."));
            }
            foreach (var schema in parsed)
            {
                foreach (XmlSchemaExternal external in schema.Includes)
                {
                    if (external is not XmlSchemaImport import)
                        throw new ArgumentException("XSD include/redefine is not supported. Supply a self-contained schema; external files are never loaded.");
                    if (!parsed.Any(candidate => candidate.TargetNamespace == import.Namespace))
                        throw new ArgumentException($"Supply the imported XSD namespace '{import.Namespace}'. External files are never loaded.");
                }
                schemas.Add(schema);
            }
            cancellationToken.ThrowIfCancellationRequested();
            schemas.Compile();
            return schemas;
        }
        catch (XmlException exception) { throw new ArgumentException($"Invalid XSD XML: {exception.Message}", exception); }
        catch (XmlSchemaException exception) { throw new ArgumentException($"Invalid XSD: {exception.Message}", exception); }
    }

    public static void Validate(string xml, XmlSchemaSet schemas, string side, CancellationToken cancellationToken = default)
    {
        var settings = Settings();
        settings.MaxCharactersInDocument = 4_000_000;
        settings.ValidationType = ValidationType.Schema;
        settings.Schemas = schemas;
        settings.ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings | XmlSchemaValidationFlags.ProcessIdentityConstraints;
        settings.ValidationEventHandler += (_, args) => throw new ArgumentException($"{side} XSD validation failed: {args.Message}");
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read()) cancellationToken.ThrowIfCancellationRequested();
        }
        catch (XmlException exception) { throw new ArgumentException($"{side} XML is invalid: {exception.Message}", exception); }
        catch (XmlSchemaValidationException exception) { throw new ArgumentException($"{side} XSD validation failed: {exception.Message}", exception); }
    }

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaximumCharacters
    };
}
