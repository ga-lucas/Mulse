using System.Xml.Linq;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static bool IsBtaHl7Assembler(string className)
        => className.Contains("BTAHL7", StringComparison.OrdinalIgnoreCase) && className.Contains("Asm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// When a send port's binding carries no SendPipelineData, falls back to the design-time component properties of the
    /// referenced custom pipeline (.btp) so the same component-to-step mapping applies. Values are design-time defaults.
    /// </summary>
    private static BindingFileAnalysis ApplyPipelineFileDefaults(BindingFileAnalysis binding, IEnumerable<BizTalkArtifactResponse> artifacts, List<string> draftWarnings)
    {
        var pipelineFiles = artifacts
            .Where(static artifact => string.Equals(artifact.Kind, "Pipeline", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(static artifact => artifact.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pipelineFiles.Length == 0 || binding.SendPorts.All(static port => port.Operational.PipelineComponents.Count > 0 || port.TransmitPipeline.Length == 0))
        {
            return binding;
        }

        var ports = binding.SendPorts.Select(port =>
        {
            if (port.Operational.PipelineComponents.Count > 0 || port.TransmitPipeline.Length == 0)
            {
                return port;
            }

            var typeName = port.TransmitPipeline.Split(',')[0].Trim();
            var simpleName = typeName[(typeName.LastIndexOf('.') + 1)..];
            var file = pipelineFiles.FirstOrDefault(artifact => string.Equals(Path.GetFileNameWithoutExtension(artifact.FullPath), simpleName, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                return port;
            }

            var components = ReadPipelineFileComponents(file.FullPath);
            if (components.Count == 0)
            {
                return port;
            }

            draftWarnings.Add($"Send port '{port.Name}' has no pipeline configuration in the binding; component properties were read from the design-time pipeline file '{Path.GetFileName(file.FullPath)}'. Deployed per-port overrides are not known - compare with the deployed binding.");
            return port with { Operational = port.Operational with { PipelineComponents = components } };
        }).ToArray();

        return binding with { SendPorts = ports };
    }

    private static IReadOnlyList<PipelineComponentConfig> ReadPipelineFileComponents(string path)
    {
        try
        {
            var document = System.Xml.Linq.XDocument.Load(path, System.Xml.Linq.LoadOptions.None);
            return document.Descendants()
                .Where(static element => element.Name.LocalName == "Component" && element.Elements().Any(static child => child.Name.LocalName == "Name"))
                .Select(static component => new PipelineComponentConfig(
                    component.Elements().First(static child => child.Name.LocalName == "Name").Value.Split(',')[0].Trim(),
                    component.Elements().Where(static element => element.Name.LocalName == "Properties").Elements()
                        .Where(static property => property.Name.LocalName == "Property" && property.Attribute("Name") is not null && !SecretPropertyPattern.IsMatch(property.Attribute("Name")!.Value))
                        .GroupBy(static property => property.Attribute("Name")!.Value, StringComparer.Ordinal)
                        .ToDictionary(static group => group.Key, static group => (group.First().Elements().FirstOrDefault(static child => child.Name.LocalName == "Value")?.Value ?? group.First().Value).Trim(), StringComparer.Ordinal)))
                .ToArray();
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Turns the pipeline components configured on each send port into draft augment steps with their property values
    /// pre-filled. Only components with a verified native equivalent are mapped (HL7TypeCaster from the Mulse.Hl7 package
    /// and SetProperty from the Compatibility Pack); everything else gets an explicit warning so nothing is silently dropped.
    /// </summary>
    private static IReadOnlyList<BizTalkDraftStepResponse> CreatePipelineComponentAugments(
        IReadOnlyList<BindingFileAnalysis> relatedBindings,
        List<string> draftWarnings)
    {
        var steps = new List<BizTalkDraftStepResponse>();
        var sendPorts = ConsolidateSendPorts(relatedBindings.SelectMany(static binding => binding.SendPorts).ToArray(), []);
        var perPort = new List<List<BizTalkDraftStepResponse>>(sendPorts.Count);

        foreach (var port in sendPorts)
        {
            var portSteps = new List<BizTalkDraftStepResponse>();
            perPort.Add(portSteps);
            var assemblerSeen = false;
            foreach (var component in port.Operational.PipelineComponents)
            {
                var className = component.ClassName;
                if (assemblerSeen && (className.EndsWith("HL7TypeCaster", StringComparison.OrdinalIgnoreCase) || className.EndsWith(".SetProperty", StringComparison.OrdinalIgnoreCase)))
                    draftWarnings.Add($"Send port '{port.Name}' has '{className}' after the HL7 assembler, but draft augments run before rendering. Source order is not preserved; review and implement the pipeline explicitly.");
                if (className.EndsWith("HL7TypeCaster", StringComparison.OrdinalIgnoreCase))
                {
                    portSteps.Add(new BizTalkDraftStepResponse("hl7-message-type-augment", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                }
                else if (className.EndsWith("PS.SetProperty", StringComparison.OrdinalIgnoreCase) || className.EndsWith(".SetProperty", StringComparison.OrdinalIgnoreCase))
                {
                    var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["propertyName"] = component.Properties.GetValueOrDefault("PropertyName", string.Empty),
                        ["propertyNamespace"] = component.Properties.GetValueOrDefault("PropertyNamespace", string.Empty),
                        ["propertyValue"] = component.Properties.GetValueOrDefault("PropertyValue", string.Empty)
                    };
                    portSteps.Add(new BizTalkDraftStepResponse("set-context-property-augment", settings));
                    if (settings.Values.Any(static value => value.Length == 0))
                    {
                        draftWarnings.Add($"Send port '{port.Name}' has a SetProperty pipeline component with an empty name, namespace or value; like the original it writes nothing until all three are configured on the 'set-context-property-augment' step.");
                    }
                }
                else if (className.Contains("MIME_SMIME", StringComparison.OrdinalIgnoreCase))
                {
                    var detail = string.Join(", ", component.Properties.Select(static pair => $"{pair.Key}={pair.Value}"));
                    draftWarnings.Add($"Send port '{port.Name}' uses the MIME/S-MIME encoder{(detail.Length > 0 ? $" ({detail})" : string.Empty)}. No native MIME/S-MIME module exists: attach the body through the SMTP deliver settings and decide separately how signing/encryption is handled.");
                }
                else if (IsBtaHl7Assembler(className))
                {
                    assemblerSeen = true;
                    draftWarnings.Add($"Send port '{port.Name}' uses '{className}'; the assembler is mapped to the 'hl7-xml-render' step (trailing-delimiter setting imported). Verify delimiters, escape sequences and segment validation against synthetic messages.");
                }
                else
                {
                    draftWarnings.Add($"Send port '{port.Name}' uses unsupported pipeline component '{className}'; its behavior has not been translated. Review the source configuration and implement an explicit native replacement.");
                }
            }
        }

            var identical = perPort.Count <= 1 || perPort.Skip(1).All(port => SameSteps(perPort[0], port));
            for (var index = 0; index < perPort.Count; index++)
            {
                if (identical && index > 0)
                {
                    break;
                }

                foreach (var step in perPort[index])
                {
                    if (identical)
                    {
                        steps.Add(step);
                        continue;
                    }

                    var scoped = new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase)
                    {
                        ["conditionJson"] = $$"""[{"source":"Metadata","path":"targetRoute","operator":"Equals","value":"route-{{index + 1}}"}]"""
                    };
                    steps.Add(step with { Settings = scoped });
                }
            }

            if (steps.Count > 0)
            {
                draftWarnings.Add($"{steps.Count} pipeline-component augment step(s) were generated from send-port pipeline configuration. " +
                    (identical
                        ? "The mapped augment steps are identical across send ports, so they run for the whole flow. Unsupported components and renderer placement still require review."
                        : "Mapped send-port augment steps differ, so each step is scoped to its delivery route with a targetRoute condition (route-N, in send-port order); populate the 'targetRoute' metadata for each payload before enabling."));
            }

            return steps;

        static bool SameSteps(List<BizTalkDraftStepResponse> first, List<BizTalkDraftStepResponse> second)
            => first.Count == second.Count && first.Zip(second).All(pair =>
                pair.First.Module == pair.Second.Module && pair.First.Settings.Count == pair.Second.Settings.Count
                && pair.First.Settings.All(setting => pair.Second.Settings.TryGetValue(setting.Key, out var value) && value == setting.Value));
    }
}
