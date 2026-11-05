using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.Mapping;

namespace Mulse.Modules.ControlFlow;

internal static class NativeMessageOperations
{
    public static IntegrationBatch CreateXml(string name, NativeScalar value)
    {
        if (value.Read() is not string text) throw new InvalidOperationException("CreateXmlMessage requires an XML String expression.");
        var document = XmlMappingSchemas.ReadXml(text, preserveWhitespace: true);
        var output = document.Declaration is null ? text : Serialize(document);
        if (output.Length > XmlMappingSchemas.MaximumCharacters) throw new ArgumentException("Native XML creation exceeds 1,000,000 characters.");
        return new([new(name + ".xml", BinaryData.FromString(output), "application/xml", new Dictionary<string, string>(StringComparer.Ordinal))]);
    }

    public static IntegrationBatch CreateRoot(string name, NativeScalar localName, NativeScalar namespaceValue)
    {
        if (localName.Read() is not string local || namespaceValue.Read() is not string ns)
            throw new InvalidOperationException("CreateXmlRoot requires String local-name and namespace expressions.");
        if (local.Length is 0 or > 256 || ns.Length > 2048 || ns is "http://www.w3.org/2000/xmlns/" or "http://www.w3.org/XML/1998/namespace")
            throw new ArgumentException("CreateXmlRoot requires a 1-256 character local name and a namespace of at most 2048 characters; reserved namespaces are unsupported.");
        try { XmlConvert.VerifyNCName(local); }
        catch (XmlException exception) { throw new ArgumentException("CreateXmlRoot requires an XML local name, not a prefixed name or markup.", exception); }
        return CreateXml(name, NativeScalar.From(new XElement(XName.Get(local, ns)).ToString(SaveOptions.DisableFormatting)));
    }

    public static IntegrationBatch AppendXml(IntegrationBatch target, IntegrationBatch source)
    {
        if (target.Count != 1 || source.Count != 1) throw new InvalidOperationException("AppendXmlMessage requires one target and one source payload.");
        var payload = target.Payloads[0];
        var document = XmlMappingSchemas.ReadXml(payload.GetText(), preserveWhitespace: true);
        var fragment = XmlMappingSchemas.ReadXml(source.Payloads[0].GetText(), preserveWhitespace: true);
        if (fragment.Declaration is not null || fragment.FirstNode is not XElement element || fragment.Root != element)
            throw new InvalidOperationException("AppendXmlMessage requires the source's first document node to be its root element; declarations, leading comments, processing instructions and whitespace are not skipped.");
        document.Root!.Add(new XElement(element));
        var output = Serialize(document);
        _ = XmlMappingSchemas.ReadXml(output, preserveWhitespace: true);
        return new([payload with { Content = BinaryData.FromString(output) }]);
    }

    public static IntegrationBatch SetValue(NativeMessageFact target, NativeScalar value, IntegrationBatch message)
    {
        target.Validate();
        if (message.Count != 1) throw new InvalidOperationException("SetMessageValue requires one named payload.");
        var payload = message.Payloads[0];
        var scalar = value.Read();
        if (target.Kind == NativeMessageFactKind.Metadata)
        {
            if (scalar is not string text) throw new InvalidOperationException("Native metadata assignment requires a String; no scalar coercion is performed.");
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal) { [target.MetadataKey] = text };
            return new([payload with { Metadata = metadata }]);
        }
        var formatted = (target.Kind, scalar) switch
        {
            (NativeMessageFactKind.XmlString, string text) => text,
            (NativeMessageFactKind.XmlInt32, int number) => XmlConvert.ToString(number),
            (NativeMessageFactKind.XmlBoolean, bool boolean) => XmlConvert.ToString(boolean),
            _ => throw new InvalidOperationException("Native XML assignment requires a value matching its explicit scalar type; null/coercion is unsupported.")
        };
        var document = XmlMappingSchemas.ReadXml(payload.GetText(), preserveWhitespace: true);
        target.ScalarElement(document).Value = formatted;
        var output = Serialize(document);
        if (output.Length > XmlMappingSchemas.MaximumCharacters) throw new ArgumentException("Native XML assignment exceeds 1,000,000 characters.");
        return new([payload with { Content = BinaryData.FromString(output) }]);
    }

    private static string Serialize(XDocument document) => (document.Declaration is { } declaration
        ? new XDeclaration(declaration.Version, "utf-8", declaration.Standalone).ToString() : "") + document.ToString(SaveOptions.DisableFormatting);
}
