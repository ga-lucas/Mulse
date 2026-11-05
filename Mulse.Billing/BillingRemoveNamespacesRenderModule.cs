using System.Xml.Linq;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Mapping;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;

namespace Mulse.Billing;

public sealed class BillingRemoveNamespacesRenderModule : IRenderModule
{
    public ModuleDescriptor Descriptor { get; } = new("billing-remove-namespaces-render", "Billing XML - remove namespaces", ModuleKind.Render,
        "Native equivalent of the supplied namespace-removal stylesheet: removes element/attribute namespace identities while retaining text, CDATA, comments and processing instructions. Attribute-name collisions fail rather than losing data.", []);

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var outputs = new List<IntegrationPayload>();
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = XmlMappingSchemas.ReadXml(payload.GetText(), preserveWhitespace: true);
            foreach (var element in document.Descendants())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToArray();
                if (attributes.Select(attribute => attribute.Name.LocalName).Distinct(StringComparer.Ordinal).Count() != attributes.Length)
                    throw new ArgumentException("Removing XML namespaces would collide attribute names; no attribute was silently overwritten.");
                element.Name = element.Name.LocalName;
                element.ReplaceAttributes(attributes.Select(attribute => new XAttribute(attribute.Name.LocalName, attribute.Value)));
            }
            var text = document.ToString(SaveOptions.DisableFormatting);
            if (text.Length > XmlMappingSchemas.MaximumCharacters) throw new ArgumentException("Namespace-removal output exceeds 1,000,000 characters.");
            outputs.Add(payload with { Content = BinaryData.FromString(text), ContentType = "application/xml", Metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal) });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new IntegrationBatch(outputs));
    }
}
