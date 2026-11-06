using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.Mapping;
using Mulse.Modules.Payloads;

namespace Mulse.Modules.ControlFlow;

[JsonConverter(typeof(JsonStringEnumConverter<NativeMessageFactKind>))]
public enum NativeMessageFactKind { Metadata, XmlText, XmlCount, XmlExists, XmlString, XmlInt32, XmlBoolean, MetadataInt32, MetadataBoolean }

public sealed class NativeXmlStep
{
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public bool Attribute { get; set; }
    /// <summary>First step only: select the element at any depth (XPath <c>//name</c>) instead of as a child of the root.</summary>
    public bool Descendant { get; set; }
    /// <summary>Optional exact filter on an unqualified attribute of this element (XPath <c>[@name='value']</c>).</summary>
    public string FilterAttribute { get; set; } = "";
    public string FilterValue { get; set; } = "";
}

public sealed class NativeMessageFact
{
    public NativeMessageFactKind Kind { get; set; }
    public string MessageName { get; set; } = "";
    public string MetadataKey { get; set; } = "";
    public List<NativeXmlStep> Steps { get; set; } = [];
    [JsonIgnore] public bool IsMetadata => Kind is NativeMessageFactKind.Metadata or NativeMessageFactKind.MetadataInt32 or NativeMessageFactKind.MetadataBoolean;

    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || MessageName is null || MessageName.Length > 128 || MessageName != MessageName.Trim()
            || MetadataKey is null || Steps is null || Steps.Count > 32)
            throw new ArgumentException("Native message facts require a known kind, bounded message name and at most 32 XML steps.");
        if (IsMetadata)
        {
            if (string.IsNullOrWhiteSpace(MetadataKey) || MetadataKey.Length > 2048 || Steps.Count != 0)
                throw new ArgumentException("Metadata facts require an exact key and no inactive XML steps.");
        }
        else
        {
            if (MetadataKey.Length != 0 || Steps.Count == 0
                || (Kind is NativeMessageFactKind.XmlString or NativeMessageFactKind.XmlInt32 or NativeMessageFactKind.XmlBoolean) && Steps.Any(step => step is not null && step.Attribute))
                throw new ArgumentException("XML facts require an absolute path and no inactive metadata key; typed scalars require elements only.");
            for (var index = 0; index < Steps.Count; index++)
            {
                var step = Steps[index];
                if (step is null || string.IsNullOrEmpty(step.Name) || step.Namespace is null || step.Namespace.Length > 2048
                    || step.Name.Length > 256 || step.Attribute && (index == 0 || index != Steps.Count - 1))
                    throw new ArgumentException("XML fact steps require expanded names; attributes may appear only at the end after an element.");
                if (step.FilterAttribute is null || step.FilterValue is null || step.FilterAttribute.Length > 256 || step.FilterValue.Length > 2048
                    || step.FilterAttribute.Length == 0 && step.FilterValue.Length != 0
                    || (step.FilterAttribute.Length != 0 || step.Descendant) && (step.Attribute || Kind is NativeMessageFactKind.XmlString or NativeMessageFactKind.XmlInt32 or NativeMessageFactKind.XmlBoolean)
                    || step.Descendant && index != 0)
                    throw new ArgumentException("XML descendant selection is allowed only on the first element step and attribute filters only on element steps; typed scalars use exact child paths.");
                try { if (step.FilterAttribute.Length != 0) XmlConvert.VerifyNCName(step.FilterAttribute); }
                catch (XmlException exception) { throw new ArgumentException("XML fact filter attributes require valid unqualified XML local names.", exception); }
                try { XmlConvert.VerifyNCName(step.Name); }
                catch (XmlException exception) { throw new ArgumentException("XML fact steps require valid XML local names.", exception); }
            }
        }
    }

    public NativeScalar Read(IntegrationBatch batch)
    {
        Validate();
        if (batch.Count != 1) throw new InvalidOperationException("Native message facts require exactly one payload; use ForEach for batches.");
        var payload = batch.Payloads[0];
        if (IsMetadata)
        {
            var matches = payload.Metadata.Where(pair => string.Equals(pair.Key, MetadataKey, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].Value is null) throw new InvalidOperationException("Native metadata fact is missing a non-null value for its exact key: " + MetadataKey);
            return Kind == NativeMessageFactKind.Metadata ? Checked(matches[0].Value) : ParseScalar(matches[0].Value);
        }
        var document = XmlMappingSchemas.ReadXml(payload.GetText(), preserveWhitespace: true);
        if (Kind is NativeMessageFactKind.XmlString or NativeMessageFactKind.XmlInt32 or NativeMessageFactKind.XmlBoolean)
            return ParseScalar(ScalarElement(document).Value);
        IEnumerable<XElement> elements = document.Elements();
        for (var index = 0; index < Steps.Count; index++)
        {
            var step = Steps[index];
            var name = XName.Get(step.Name, step.Namespace);
            if (step.Attribute) return Result(elements.Attributes(name).Where(attribute => !attribute.IsNamespaceDeclaration).Select(attribute => attribute.Value));
            elements = step.Descendant ? document.Descendants(name) : elements.Where(element => element.Name == name);
            if (step.FilterAttribute.Length != 0)
                elements = elements.Where(element => element.Attribute(XName.Get(step.FilterAttribute)) is { } filter && string.Equals(filter.Value, step.FilterValue, StringComparison.Ordinal));
            elements = Steps[0].Descendant ? elements.OrderBy(element => element, XNode.DocumentOrderComparer).ToArray() : elements.ToArray();
            if (index == Steps.Count - 1) return Result(elements.Select(element => element.Value));
            if (!Steps[index + 1].Attribute) elements = elements.Elements();
        }
        throw new InvalidOperationException("Native XML fact has no active selection.");
    }

    internal XElement ScalarElement(XDocument document)
    {
        IEnumerable<XElement> candidates = document.Elements();
        XElement? selected = null;
        foreach (var step in Steps)
        {
            if (step.Attribute) throw new ArgumentException("Typed XML scalar fields require element paths, not attributes.");
            var matches = candidates.Where(element => element.Name == XName.Get(step.Name, step.Namespace)).Take(2).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Typed XML scalar path requires exactly one element at every step; missing/duplicate values are not defaulted.");
            selected = matches[0]; candidates = selected.Elements();
        }
        if (selected is null || selected.HasElements || selected.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
            throw new InvalidOperationException("Typed XML scalar requires a simple, non-nil element without context/type attributes.");
        return selected;
    }

    internal NativeScalar ParseScalar(string text)
    {
        if (text.Length > 4096) throw new ArgumentException("Native scalar fact exceeds 4096 characters.");
        try
        {
            return Kind switch
            {
                NativeMessageFactKind.XmlString => Checked(text),
                NativeMessageFactKind.XmlInt32 or NativeMessageFactKind.MetadataInt32 => NativeScalar.From(XmlConvert.ToInt32(text.Trim(' ', '\t', '\r', '\n'))),
                NativeMessageFactKind.XmlBoolean or NativeMessageFactKind.MetadataBoolean => NativeScalar.From(XmlConvert.ToBoolean(text.Trim(' ', '\t', '\r', '\n'))),
                _ => throw new ArgumentException("Typed scalar requires an explicit String, Int32 or Boolean fact kind.")
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        { throw new ArgumentException("Scalar fact has an invalid value for its explicit native type.", exception); }
    }

    private NativeScalar Result(IEnumerable<string> values) => Kind switch
    {
        NativeMessageFactKind.XmlCount => NativeScalar.From(values.Count()),
        NativeMessageFactKind.XmlExists => NativeScalar.From(values.Any()),
        NativeMessageFactKind.XmlText => Checked(values.FirstOrDefault() ?? ""),
        _ => throw new InvalidOperationException("Unknown native XML fact result kind.")
    };
    private static NativeScalar Checked(string text)
    {
        var value = NativeScalar.From(text);
        _ = value.Read();
        return value;
    }
}
