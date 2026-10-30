using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.Mapping;
using Mulse.Modules.Pipeline;

namespace Service.BizTalkImport;

internal static class BizTalkPipelineInventoryAnalyzer
{
    private static readonly IReadOnlyDictionary<Guid, (string Name, string Direction)> Categories = new Dictionary<Guid, (string, string)>
    {
        [new("9d0e4103-4cce-4536-83fa-4a5040674ad6")] = ("Decode", "Receive"),
        [new("9d0e4105-4cce-4536-83fa-4a5040674ad6")] = ("Disassemble", "Receive"),
        [new("9d0e410d-4cce-4536-83fa-4a5040674ad6")] = ("Validate", "Receive"),
        [new("9d0e410e-4cce-4536-83fa-4a5040674ad6")] = ("ResolveParty", "Receive"),
        [new("9d0e4101-4cce-4536-83fa-4a5040674ad6")] = ("PreAssemble", "Send"),
        [new("9d0e4107-4cce-4536-83fa-4a5040674ad6")] = ("Assemble", "Send"),
        [new("9d0e4108-4cce-4536-83fa-4a5040674ad6")] = ("Encode", "Send")
    };

    public static (PipelineMigrationInventory? Inventory, IReadOnlyList<MigrationDiagnostic> Diagnostics) Analyze(string path)
    {
        var diagnostics = new List<MigrationDiagnostic>();
        try
        {
            var document = XmlMappingSchemas.ReadXmlFile(path);
            if (document.Root?.Name != "Document" || document.Root.Elements("Stages").Count() != 1)
                return Failed("A Document with exactly one Stages collection is required.");
            var stages = document.Root.Element("Stages")!.Elements("Stage").ToArray();
            if (stages.Length == 0) return Failed("No pipeline stages were declared.");
            var components = stages.SelectMany(stage => stage.Elements("Components").Elements("Component")).ToArray();
            if (document.Descendants("Stage").Count() != stages.Length || document.Descendants("Component").Count() != components.Length
                || document.Root.Element("Stages")!.Elements().Any(element => element.Name != "Stage")
                || stages.Any(stage => stage.Elements("Components").Count() != 1 || stage.Elements().Any(element => element.Name != "Components"))
                || stages.SelectMany(stage => stage.Elements("Components")).Any(collection => collection.Elements().Any(element => element.Name != "Component")))
                Add("BT-PIPELINE-STRUCTURE", "Unknown or misplaced stage/component content was detected; the inventory does not represent every construct.");
            var directions = new HashSet<string>(StringComparer.Ordinal);
            var stageReports = new List<PipelineStageEvidence>();
            var savedComponents = 0;
            var unknownCategory = false;
            for (var index = 0; index < stages.Length; index++)
            {
                var stage = stages[index];
                var category = (string?)stage.Attribute("CategoryId") ?? "";
                var known = Guid.TryParse(category, out var id) && Categories.ContainsKey(id);
                var stageName = known ? Categories[id].Name : "Unknown";
                if (known) directions.Add(Categories[id].Direction); else unknownCategory = true;
                if (index >= 64) continue;
                var componentReports = new List<PipelineComponentEvidence>();
                var order = 0;
                foreach (var component in stage.Elements("Components").Elements("Component"))
                {
                    order++;
                    if (savedComponents >= 256) continue;
                    var names = component.Elements("Name").ToArray();
                    var versions = component.Elements("Version").ToArray();
                    var properties = component.Elements("Properties").Elements("Property").ToArray();
                    if (names.Length != 1 || string.IsNullOrWhiteSpace(names[0].Value) || names.Any(name => name.HasElements)
                        || versions.Length > 1 || versions.Any(version => version.HasElements)
                        || component.Elements("Properties").Count() > 1 || properties.Any(property => string.IsNullOrWhiteSpace((string?)property.Attribute("Name"))))
                        Add("BT-PIPELINE-COMPONENT-METADATA", "Missing, complex or ambiguous component identity/property metadata requires source review.");
                    componentReports.Add(new(order, Line(component), Identity(names.FirstOrDefault()), Identity(versions.FirstOrDefault()),
                        properties.Length, Math.Max(0, properties.Length - 64), properties.Take(64).Select(property => Bounded((string?)property.Attribute("Name") ?? "")).ToArray()));
                    savedComponents++;
                }
                stageReports.Add(new(index + 1, Line(stage), Bounded(category), stageName, componentReports));
            }
            var direction = unknownCategory || directions.Count != 1 ? "Unknown" : directions.Single();
            if (direction == "Unknown") Add("BT-PIPELINE-DIRECTION", "Unknown or mixed stage categories prevent a single receive/send direction assertion.");
            if (stages.Length > 64 || components.Length > savedComponents || stageReports.SelectMany(stage => stage.Components).Any(component => component.OmittedPropertyCount > 0))
                Add("BT-PIPELINE-INVENTORY-LIMIT", "Pipeline evidence exceeded a saved-report limit (64 stages, 256 components, 64 property names per component); omitted counts are retained.");
            return (new(direction, stages.Length, Math.Max(0, stages.Length - 64), components.Length, components.Length - savedComponents, stageReports), diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or XmlException or NotSupportedException)
        {
            return Failed($"Pipeline XML could not be inspected ({exception.GetType().Name}). Read, format or resource limits require source review; source values are not included in this error.");
        }

        string Identity(XElement? element) => element is null || element.HasElements ? "" : Bounded(element.Value);
        string Bounded(string text)
        {
            if (text.Length <= 512) return text;
            Add("BT-PIPELINE-METADATA-LIMIT", "An identity/property name exceeded 512 characters and was truncated; inspect the original source.");
            return text[..512];
        }
        void Add(string code, string message)
        {
            if (diagnostics.Any(diagnostic => diagnostic.Code == code)) return;
            diagnostics.Add(new(code, MigrationDiagnosticSeverity.Blocking, path, message,
                "Inspect the original pipeline source. Translate and test all component behavior, property configuration, ordering and context effects; inventory is not execution approval."));
        }
        (PipelineMigrationInventory?, IReadOnlyList<MigrationDiagnostic>) Failed(string message)
        {
            Add("BT-PIPELINE-NOT-ANALYZED", message);
            return (null, diagnostics);
        }
    }

    private static int Line(XElement element) => ((IXmlLineInfo)element).LineNumber;
}
