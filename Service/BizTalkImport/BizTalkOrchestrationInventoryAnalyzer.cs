using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.Pipeline;

namespace Service.BizTalkImport;

internal static class BizTalkOrchestrationInventoryAnalyzer
{
    private const int MaximumCharacters = 8_000_000;
    private static readonly XNamespace DesignerNamespace = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static readonly Regex EndTag = new(@"</(?:[A-Za-z_][\w.-]*:)?MetaModel\s*>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> ReferenceProperties = new(StringComparer.Ordinal)
    {
        "Type", "PortName", "OperationName", "MessageName", "CalledService", "PolicyName", "PolicyVersion"
    };

    public static (OrchestrationMigrationInventory? Inventory, IReadOnlyList<MigrationDiagnostic> Diagnostics) Analyze(string path)
    {
        try
        {
            using var file = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[8192];
            var text = new System.Text.StringBuilder();
            int length;
            while ((length = file.ReadBlock(buffer, 0, buffer.Length)) > 0)
            {
                if (text.Length + length > MaximumCharacters)
                    return Failed("The orchestration exceeds the 8,000,000-character analysis limit.");
                text.Append(buffer, 0, length);
            }

            var content = text.ToString();
            var start = content.IndexOf("<?xml", StringComparison.Ordinal);
            var end = start < 0 ? Match.Empty : EndTag.Match(content, start);
            if (!end.Success)
                return Failed("The orchestration has no complete XML designer metadata section.");

            using var reader = XmlReader.Create(new StringReader(content[start..(end.Index + end.Length)]), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumCharacters
            });
            var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            if (document.Root?.Name != DesignerNamespace + "MetaModel")
                return Failed("The orchestration designer namespace or root is not recognized.");

            var elements = document.Descendants(DesignerNamespace + "Element").ToArray();
            if (elements.Length == 0)
                return Failed("The orchestration designer metadata contains no shapes.");

            var ids = elements.Select((element, index) => (element, id: index + 1))
                .ToDictionary(item => item.element, item => item.id);
            var offset = content.AsSpan(0, start).Count('\n');
            var shapes = elements.Select(element =>
            {
                var parent = element.Ancestors(DesignerNamespace + "Element").FirstOrDefault();
                var properties = element.Elements(DesignerNamespace + "Property");
                return new OrchestrationShapeReport(
                    ids[element], parent is null ? null : ids[parent],
                    ((IXmlLineInfo)element).LineNumber + offset,
                    (string?)element.Attribute("Type") ?? "Unknown",
                    properties.FirstOrDefault(property => (string?)property.Attribute("Name") == "Name")?.Attribute("Value")?.Value ?? "",
                    properties.Where(property => ReferenceProperties.Contains((string?)property.Attribute("Name") ?? ""))
                        .GroupBy(property => (string)property.Attribute("Name")!, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => string.Join("; ", group.Select(property => (string?)property.Attribute("Value") ?? "")), StringComparer.Ordinal));
            }).ToArray();
            var counts = shapes.GroupBy(shape => shape.ShapeType, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var diagnostics = new List<MigrationDiagnostic>();
            AddBlocker("BT-ODX-ASSIGNMENTS", ["MessageAssignment", "VariableAssignment"],
                "Message construction and expressions have not been translated, including any dynamic transforms, context writes or custom helper calls.",
                "Inspect the expressions at the source locations. Resolve every transform/helper dependency and implement native message/context behavior; no source expressions are executed by import.");
            AddBlocker("BT-ODX-RULES", ["CallRules"],
                "Business Rules Engine policy calls have not been translated. Policy implementations and deployed versions cannot be inferred from call sites.",
                "Obtain the deployed policy/vocabulary export, identify rule inputs/outputs and implement and test the equivalent native rules.");
            AddBlocker("BT-ODX-LOOPS", ["While", "Loop"],
                "Loop bodies, iteration state and output ordering have not been translated.",
                "Implement the iteration/debatch behavior, zero-item cases, ordering and per-item failure/retry semantics.");
            AddBlocker("BT-ODX-PORTS", ["Receive", "Send", "Call", "Exec"],
                "Message sends, receives and orchestration calls are inventoried, not wired to equivalent native operations. A list of delivery routes does not preserve their execution order or request/reply contract.",
                "Resolve every port/operation/message reference and binding, including direct subscriptions, dynamic destinations, request/reply completion and child workflows.");
            AddBlocker("BT-ODX-FAILURE-SCOPES", ["Scope", "AtomicTransaction", "LongRunningTransaction", "Catch", "Throw", "Compensate", "Suspend", "Terminate"],
                "Scope, transaction and failure behavior has not been translated.",
                "Define atomicity, exception results, compensation, duplicate prevention and recovery boundaries explicitly; compare failure outputs and side effects.");
            AddBlocker("BT-ODX-WAITS", ["Delay", "Listen", "Parallel", "CorrelationDeclaration"],
                "Waits, concurrency and correlation declarations require orchestration-specific review; declarations alone do not prove active correlation behavior.",
                "Inspect the relevant bodies and usage, then implement durable state, timing and correlation only where actually used. Test restart/replay and competing arrivals.");
            return (new OrchestrationMigrationInventory(counts, shapes), diagnostics);

            void AddBlocker(string code, string[] types, string message, string remediation)
            {
                var matches = shapes.Where(shape => types.Contains(shape.ShapeType, StringComparer.Ordinal)).ToArray();
                if (matches.Length == 0) return;
                var summary = string.Join(", ", matches.GroupBy(shape => shape.ShapeType).Select(group => $"{group.Count()} {group.Key}"));
                diagnostics.Add(new MigrationDiagnostic(code, MigrationDiagnosticSeverity.Blocking, path,
                    $"Detected {summary}. {message}", remediation));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or RegexMatchTimeoutException)
        {
            return Failed($"Designer metadata could not be analyzed: {exception.Message}");
        }

        (OrchestrationMigrationInventory?, IReadOnlyList<MigrationDiagnostic>) Failed(string message)
            => (null, [new MigrationDiagnostic("BT-ODX-NOT-ANALYZED", MigrationDiagnosticSeverity.Blocking, path,
                message, "Restore valid BizTalk designer metadata and reanalyze. Missing analysis is not evidence that the orchestration has no behavior.")]);
    }
}
