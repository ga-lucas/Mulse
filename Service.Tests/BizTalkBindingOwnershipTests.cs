using Service.Models;

namespace Service.Tests;

public sealed class BizTalkBindingOwnershipTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-binding-owners-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SharedApplicationExport_IsMatchedByAssembly_AndScopedToEachOrchestration()
    {
        var source = Path.Combine(_directory, "Source");
        Directory.CreateDirectory(source);
        var project = Path.Combine(source, "UnrelatedFilename.btproj");
        File.WriteAllText(project, "<Project><PropertyGroup><AssemblyName>Example.Shared</AssemblyName></PropertyGroup><ItemGroup><XLang Include='First.odx'/><XLang Include='Second.odx'/></ItemGroup></Project>");
        foreach (var name in new[] { "First", "Second" })
            File.WriteAllText(Path.Combine(source, name + ".odx"), "<?xml version='1.0'?><om:MetaModel xmlns:om='http://schemas.microsoft.com/BizTalk/2003/DesignerData'><om:Element Type='Send'/></om:MetaModel>");
        var binding = Path.Combine(_directory, "arbitrary-export.xml");
        File.WriteAllText(binding, """
            <BindingInfo><ModuleRefCollection>
              <ModuleRef Name="Example.Shared"><Services>
                <Service Name="Custom.Namespace.First"><Ports><Port Name="Out"><SendPortRef Name="FirstOutput"/><ReceivePortRef Name="FirstInput"/></Port></Ports></Service>
                <Service Name="Custom.Namespace.Second"><Ports><Port Name="Out"><SendPortRef Name="SecondOutput"/><ReceivePortRef Name=""/></Port></Ports></Service>
              </Services></ModuleRef>
              <ModuleRef Name="Example.Unrelated"><Services><Service Name="Other"><Ports><Port Name="Out"><SendPortRef Name="UnrelatedOutput"/></Port></Ports></Service></Services></ModuleRef>
            </ModuleRefCollection><ReceivePortCollection>
              <ReceivePort Name="FirstInput"><ReceiveLocations><ReceiveLocation Name="FirstLocation"><Address>C:\synthetic\first</Address><TransportType Name="FILE"/></ReceiveLocation></ReceiveLocations></ReceivePort>
              <ReceivePort Name="UnrelatedInput"><ReceiveLocations><ReceiveLocation Name="OtherLocation"><Address>C:\synthetic\other</Address><TransportType Name="FILE"/></ReceiveLocation></ReceiveLocations></ReceivePort>
            </ReceivePortCollection><SendPortCollection>
              <SendPort Name="FirstOutput"><PrimaryTransport><Address>C:\synthetic\first-out</Address><TransportType Name="FILE"/></PrimaryTransport></SendPort>
              <SendPort Name="SecondOutput"><PrimaryTransport><Address>C:\synthetic\second-out</Address><TransportType Name="FILE"/></PrimaryTransport></SendPort>
              <SendPort Name="UnrelatedOutput"><PrimaryTransport><Address>C:\synthetic\unrelated-out</Address><TransportType Name="FILE"/></PrimaryTransport></SendPort>
            </SendPortCollection></BindingInfo>
            """);
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, AdditionalBindingPaths = [binding] }, default);
        Assert.Equal(2, result.FlowCandidates.Count);
        var first = result.FlowCandidates.Single(candidate => candidate.DraftFlow.Migration!.SourceOrchestration == "First");
        var second = result.FlowCandidates.Single(candidate => candidate.DraftFlow.Migration!.SourceOrchestration == "Second");
        Assert.Single(first.RelatedBindingFiles);
        Assert.Single(second.RelatedBindingFiles);
        Assert.Single(first.DraftFlow.Deliveries);
        Assert.Single(second.DraftFlow.Deliveries);
        Assert.Equal("C:\\synthetic\\first-out", first.DraftFlow.Deliveries.Single().Delivers.Single().Deliver.Settings["path"]);
        Assert.Equal("C:\\synthetic\\second-out", second.DraftFlow.Deliveries.Single().Delivers.Single().Deliver.Settings["path"]);
        Assert.Equal("document-repository-fetch", second.DraftFlow.Fetches.Single().Fetch.Module);
        Assert.Contains(second.DraftFlow.Warnings, warning => warning.Contains("No BizTalk receive location", StringComparison.Ordinal));
        Assert.Contains(first.DraftFlow.Warnings, warning => warning.Contains("explicit assembly/orchestration", StringComparison.Ordinal));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
