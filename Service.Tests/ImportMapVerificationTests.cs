using System.Text;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using Service.BizTalkImport;
using Service.Mapping;
using Xunit;

namespace Service.Tests;

/// <summary>
/// Opt-in tool, not a regression test: converts one BizTalk map with the native converter, runs it on a real input message and compares the result with the output BizTalk
/// actually produced (for example a message exported from the BizTalk Administration console). It does nothing unless the variables are set.
/// <code>
/// $env:VERIFY_ROOT = 'C:\path\to\biztalk\projects'   # folder searched for *.btproj (schemas)
/// $env:VERIFY_MAP = 'C:\path\to\Some.btm'
/// $env:VERIFY_INPUT = 'C:\path\to\input.xml'; $env:VERIFY_EXPECTED = 'C:\path\to\expected.xml'
/// $env:VERIFY_OUT = 'C:\path\to\verify.txt'          # report; written outside the repository
/// dotnet test Service.Tests --filter "FullyQualifiedName~ImportMapVerification"
/// </code>
/// Message data is often sensitive, so the report lists only paths and value lengths. Set VERIFY_SHOW_VALUES=1 to include (truncated) values.
/// </summary>
public sealed class ImportMapVerificationTests
{
    [Fact]
    public void Verify()
    {
        string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
        var (root, mapPath, inputPath, expectedPath, outPath) = (Env("VERIFY_ROOT"), Env("VERIFY_MAP"), Env("VERIFY_INPUT"), Env("VERIFY_EXPECTED"), Env("VERIFY_OUT"));
        if (root is null || mapPath is null || inputPath is null || expectedPath is null || outPath is null) return;
        var showValues = Env("VERIFY_SHOW_VALUES") == "1";
        var report = new StringBuilder();
        try
        {
            var catalog = BizTalkSchemaSourceCatalog.Read(Directory.EnumerateFiles(root, "*.btproj", SearchOption.AllDirectories).ToArray());
            var document = XmlMappingSchemas.ReadXmlFile(mapPath);
            var source = BizTalkNativeMapChoiceDeriver.SchemaDocuments(document, "SrcTree", catalog, mapPath, out var sourceProblem) ?? throw new ArgumentException(sourceProblem);
            var target = BizTalkNativeMapChoiceDeriver.SchemaDocuments(document, "TrgTree", catalog, mapPath, out var targetProblem) ?? throw new ArgumentException(targetProblem);
            var conversion = BizTalkNativeMapConverter.Convert(document.ToString(SaveOptions.DisableFormatting), source, target, default, embedSchemas: false);
            var actual = XDocument.Parse(new XmlMappingEngine(conversion.MappingJson, allowStubs: true).Transform(File.ReadAllText(inputPath)));
            var expected = XDocument.Parse(File.ReadAllText(expectedPath).TrimStart('\uFEFF'));
            var differences = new List<string>();
            Compare(expected.Root!, actual.Root!, "/" + expected.Root!.Name.LocalName, differences, showValues);
            report.Append("Map: ").AppendLine(Path.GetFileName(mapPath));
            report.Append("Warnings: ").AppendLine(conversion.Warnings.Count.ToString());
            report.Append("Differences: ").AppendLine(differences.Count.ToString());
            foreach (var difference in differences.Take(200)) report.AppendLine(difference);
            report.AppendLine(differences.Count == 0 ? "RESULT: MATCH (after ignoring whitespace-only text and namespace prefixes)" : "RESULT: DIFFERENT");
        }
        catch (Exception exception)
        {
            report.AppendLine("RESULT: ERROR").AppendLine(exception.Message);
        }
        File.WriteAllText(outPath, report.ToString());
    }

    private static string Show(string value, bool showValues)
        => showValues ? "'" + (value.Length > 60 ? value[..60] + "…" : value) + "'" : $"len {value.Length}";

    private static void Compare(XElement expected, XElement actual, string path, List<string> differences, bool showValues)
    {
        if (expected.Name != actual.Name) { differences.Add($"{path}: element is {actual.Name.LocalName} but BizTalk wrote {expected.Name.LocalName}"); return; }
        static IEnumerable<XAttribute> Attributes(XElement element) => element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration);
        foreach (var attribute in Attributes(expected))
            if (Attributes(actual).FirstOrDefault(candidate => candidate.Name == attribute.Name) is not { } other) differences.Add($"{path}/@{attribute.Name.LocalName}: missing from native output");
            else if (other.Value != attribute.Value) differences.Add($"{path}/@{attribute.Name.LocalName}: value differs (BizTalk {Show(attribute.Value, showValues)}, native {Show(other.Value, showValues)})");
        foreach (var attribute in Attributes(actual))
            if (!Attributes(expected).Any(candidate => candidate.Name == attribute.Name)) differences.Add($"{path}/@{attribute.Name.LocalName}: extra in native output");
        var expectedChildren = expected.Elements().ToArray();
        var actualChildren = actual.Elements().ToArray();
        if (expectedChildren.Length == 0 && actualChildren.Length == 0)
        {
            if (expected.Value != actual.Value) differences.Add($"{path}: text differs (BizTalk {Show(expected.Value, showValues)}, native {Show(actual.Value, showValues)})");
            return;
        }
        var occurrences = new Dictionary<XName, int>();
        var actualUsed = new bool[actualChildren.Length];
        foreach (var child in expectedChildren)
        {
            var index = occurrences[child.Name] = occurrences.GetValueOrDefault(child.Name) + 1;
            var position = Array.FindIndex(actualChildren, candidate => candidate.Name == child.Name && !actualUsed[Array.IndexOf(actualChildren, candidate)]);
            if (position < 0) { differences.Add($"{path}/{child.Name.LocalName}[{index}]: missing from native output"); continue; }
            actualUsed[position] = true;
            Compare(child, actualChildren[position], $"{path}/{child.Name.LocalName}[{index}]", differences, showValues);
        }
        for (var position = 0; position < actualChildren.Length; position++)
            if (!actualUsed[position]) differences.Add($"{path}/{actualChildren[position].Name.LocalName}: extra element in native output");
        var order = expectedChildren.Select(child => child.Name.LocalName).SequenceEqual(actualChildren.Select(child => child.Name.LocalName));
        if (!order && differences.Count == 0) differences.Add($"{path}: child element order differs");
    }
}
