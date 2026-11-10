using System.Text;
using System.Xml.Linq;
using Mulse.Modules.Mapping;
using Service.BizTalkImport;
using Service.Mapping;
using Xunit;

namespace Service.Tests;

/// <summary>
/// Opt-in tool, not a regression test: it converts every .btm map under a folder of BizTalk projects and writes a tab-separated report
/// (path, OK|FAIL|SCHEMA|CUSTOM, message) plus a grouped summary of failure reasons. It does nothing unless both environment variables are set.
/// <code>
/// $env:MEASURE_ROOT = 'C:\path\to\biztalk\projects'; $env:MEASURE_OUT = 'C:\path\to\report.tsv'
/// dotnet test Service.Tests --filter "FullyQualifiedName~ImportCoverageReport"
/// </code>
/// The report is written outside the repository by the caller and names real maps and schemas, so do not commit it.
/// </summary>
public sealed class ImportCoverageReportTests
{
    [Fact]
    public void Report()
    {
        var root = Environment.GetEnvironmentVariable("MEASURE_ROOT");
        var output = Environment.GetEnvironmentVariable("MEASURE_OUT");
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(output)) return;
        var projects = Directory.EnumerateFiles(root, "*.btproj", SearchOption.AllDirectories).ToArray();
        var catalog = BizTalkSchemaSourceCatalog.Read(projects);
        var lines = new List<(string Path, string Status, string Message)>();
        // Only maps a project declares are imported; loose .btm files left in project folders are stale copies and are not measured.
        var declared = projects.SelectMany(project => XmlMappingSchemas.ReadXmlFile(project).Descendants().Where(element => element.Name.LocalName == "Map" && element.Parent?.Name.LocalName == "ItemGroup")
            .Select(item => (string?)item.Attribute("Include")).Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include!))).Where(File.Exists));
        foreach (var path in declared.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var document = XmlMappingSchemas.ReadXmlFile(path);
                if (document.Root?.Element("CustomXSLT") is not null) { lines.Add((path, "CUSTOM", string.Empty)); continue; }
                var source = BizTalkNativeMapChoiceDeriver.SchemaDocuments(document, "SrcTree", catalog, path, out var sourceProblem);
                var target = BizTalkNativeMapChoiceDeriver.SchemaDocuments(document, "TrgTree", catalog, path, out var targetProblem);
                if (source is null || target is null) { lines.Add((path, "SCHEMA", sourceProblem ?? targetProblem ?? string.Empty)); continue; }
                BizTalkNativeMapConverter.Convert(document.ToString(SaveOptions.DisableFormatting), source, target, default, embedSchemas: false);
                lines.Add((path, "OK", string.Empty));
            }
            catch (Exception exception)
            {
                lines.Add((path, "FAIL", exception.Message.ReplaceLineEndings(" ")));
            }
        }
        var report = new StringBuilder();
        foreach (var (path, status, message) in lines) report.Append(path).Append('\t').Append(status).Append('\t').Append(message).Append('\n');
        File.WriteAllText(output, report.ToString());
        var summary = new StringBuilder();
        summary.Append(string.Join(", ", lines.GroupBy(line => line.Status).OrderBy(group => group.Key).Select(group => $"{group.Key} {group.Count()}"))).Append('\n');
        foreach (var group in lines.Where(line => line.Status is "FAIL" or "SCHEMA")
            .GroupBy(line => System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(line.Message, "'[^']*'", "'X'"), @"\d+", "N"))
            .OrderByDescending(group => group.Count()))
            summary.Append(group.Count()).Append('\t').Append(group.Key.Length > 200 ? group.Key[..200] : group.Key).Append('\n');
        File.WriteAllText(Path.ChangeExtension(output, ".summary.txt"), summary.ToString());
    }
}
