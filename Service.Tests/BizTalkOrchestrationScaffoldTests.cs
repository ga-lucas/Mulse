using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;

namespace Service.Tests;

public sealed class BizTalkOrchestrationScaffoldTests
{
    [Fact]
    public async Task GeneratedStub_CompilesAgainstSdkWithoutGlobalUsings_AndFailsInsteadOfPassingThrough()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-scaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var odx = Path.Combine(directory, "Process.odx");
            File.WriteAllText(odx, """
                // #if __DESIGNER_DATA
                <?xml version="1.0"?>
                <om:MetaModel xmlns:om="http://schemas.microsoft.com/BizTalk/2003/DesignerData">
                  <om:Element Type="While" />
                </om:MetaModel>
                // #endif
                """);
            var source = BizTalkOrchestrationDecisionAnalyzer.Analyze(odx, "Process", "Migration.Test").ScaffoldedModuleSourceCode!;
            Assert.False(string.IsNullOrWhiteSpace(source));
            File.WriteAllText(Path.Combine(directory, "Generated.cs"), source);
            var project = Path.Combine(directory, "Stub.csproj");
            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("ImplicitUsings", "disable")),
                new XElement("ItemGroup", new XElement("Reference", new XAttribute("Include", "Mulse.Modules"),
                    new XElement("HintPath", typeof(IOrchestrationAugmentModule).Assembly.Location), new XElement("Private", "false")))))
                .Save(project);
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "build", project, "--verbosity", "quiet", "--disable-build-servers" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            var assembly = Assembly.Load(File.ReadAllBytes(Path.Combine(directory, "bin", "Debug", "net10.0", "Stub.dll")));
            var module = (IOrchestrationAugmentModule)Activator.CreateInstance(assembly.GetTypes().Single(type => typeof(IOrchestrationAugmentModule).IsAssignableFrom(type)))!;
            Assert.Contains("unimplemented migration stub", module.Descriptor.DisplayName);
            var error = await Assert.ThrowsAsync<NotSupportedException>(() => module.AugmentAsync(null!, IntegrationBatch.Empty, null!, CancellationToken.None));
            Assert.Contains("has not been implemented", error.Message);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.AugmentAsync(null!, IntegrationBatch.Empty, null!, new CancellationToken(canceled: true)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
