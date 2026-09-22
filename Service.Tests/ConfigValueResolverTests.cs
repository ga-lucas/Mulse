using Mulse.Modules;
using Service;

namespace Service.Tests;

public class ConfigValueResolverTests
{
    private static PipelineDefinition BuildPipelineWithToken(string settingValue)
    {
        return new PipelineDefinition
        {
            Id = "flow-1",
            Sources =
            [
                new SourceDefinition
                {
                    Id = "primary",
                    Fetch = new ModuleStepDefinition
                    {
                        Module = "sftp-fetch",
                        Settings = new Dictionary<string, string> { ["host"] = settingValue }
                    },
                    Parse = new ModuleStepDefinition { Module = "json-parse" }
                }
            ],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "json-render" },
                    Deliver = new ModuleStepDefinition { Module = "http-deliver" }
                }
            ]
        };
    }

    [Fact]
    public void Resolve_ReplacesConfigToken_WithStoredValue()
    {
        var state = new RuntimeMulseState { ConfigValues = { ["ftp-host"] = "sftp.example.com" } };
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state));
        var pipeline = BuildPipelineWithToken("{{config:ftp-host}}");

        var resolved = resolver.Resolve(pipeline);

        Assert.Equal("sftp.example.com", resolved.Sources[0].Fetch.Settings["host"]);
    }

    [Fact]
    public void Resolve_ReplacesSecretToken_WithStoredValue()
    {
        var state = new RuntimeMulseState { ConfigValues = { ["ftp-password"] = "hunter2" } };
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state));
        var pipeline = BuildPipelineWithToken("{{secret:ftp-password}}");

        var resolved = resolver.Resolve(pipeline);

        Assert.Equal("hunter2", resolved.Sources[0].Fetch.Settings["host"]);
    }

    [Fact]
    public void Resolve_LeavesNonTokenValues_Untouched()
    {
        var state = new RuntimeMulseState();
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state));
        var pipeline = BuildPipelineWithToken("sftp.literal-host.com");

        var resolved = resolver.Resolve(pipeline);

        Assert.Equal("sftp.literal-host.com", resolved.Sources[0].Fetch.Settings["host"]);
    }

    [Fact]
    public void Resolve_Throws_WhenReferenceHasNoStoredValue()
    {
        var state = new RuntimeMulseState();
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state));
        var pipeline = BuildPipelineWithToken("{{config:missing-ref}}");

        var exception = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(pipeline));
        Assert.Contains("missing-ref", exception.Message);
    }

    [Fact]
    public void FindReferenceUsages_ReportsResolvedAndUnresolvedTokensWithSettingPath()
    {
        var state = new RuntimeMulseState { ConfigValues = { ["ftp-host"] = "sftp.example.com" } };
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state));
        var resolvedPipeline = BuildPipelineWithToken("{{config:ftp-host}}");
        var unresolvedPipeline = BuildPipelineWithToken("{{config:missing-ref}}");
        unresolvedPipeline = new PipelineDefinition
        {
            Id = "flow-2",
            Sources = unresolvedPipeline.Sources,
            Deliveries = unresolvedPipeline.Deliveries
        };

        var usages = resolver.FindReferenceUsages([resolvedPipeline, unresolvedPipeline]);

        Assert.Equal(2, usages.Count);
        var resolvedUsage = Assert.Single(usages, usage => usage.FlowId == "flow-1");
        Assert.True(resolvedUsage.IsResolved);
        Assert.Equal("ftp-host", resolvedUsage.Reference);
        Assert.Equal("sources[0].fetch.settings.host", resolvedUsage.SettingPath);

        var unresolvedUsage = Assert.Single(usages, usage => usage.FlowId == "flow-2");
        Assert.False(unresolvedUsage.IsResolved);
        Assert.Equal("missing-ref", unresolvedUsage.Reference);
    }
}
