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
            Fetches =
            [
                new FetchNodeDefinition
                {
                    Id = "primary-fetch",
                    Fetch = new ModuleStepDefinition
                    {
                        Module = "sftp-fetch",
                        Settings = new Dictionary<string, string> { ["host"] = settingValue }
                    }
                }
            ],
            Parses =
            [
                new ParseNodeDefinition
                {
                    Id = "primary",
                    Parse = new ModuleStepDefinition
                    {
                        Module = "xml-xsd-parse",
                        Settings = new Dictionary<string, string> { ["schemaPath"] = settingValue }
                    },
                    FetchNodeIds = ["primary-fetch"]
                }
            ],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "json-render" },
                    Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "http-deliver" } }]
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

        Assert.Equal("sftp.example.com", resolved.Fetches[0].Fetch.Settings["host"]);
        Assert.Equal("sftp.example.com", resolved.Parses[0].Parse.Settings["schemaPath"]);
        Assert.Equal(["primary-fetch"], resolved.Parses[0].FetchNodeIds);
    }

    [Fact]
    public void Resolve_ReplacesSecretToken_WithStoredValue()
    {
        var state = new RuntimeMulseState { ConfigValues = { ["ftp-password"] = "hunter2" } };
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state));
        var pipeline = BuildPipelineWithToken("{{secret:ftp-password}}");

        var resolved = resolver.Resolve(pipeline);

        Assert.Equal("hunter2", resolved.Fetches[0].Fetch.Settings["host"]);
    }

    [Fact]
    public void Resolve_LeavesNonTokenValues_Untouched()
    {
        var state = new RuntimeMulseState();
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state));
        var pipeline = BuildPipelineWithToken("sftp.literal-host.com");

        var resolved = resolver.Resolve(pipeline);

        Assert.Equal("sftp.literal-host.com", resolved.Fetches[0].Fetch.Settings["host"]);
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
            Fetches = unresolvedPipeline.Fetches,
            Parses = unresolvedPipeline.Parses,
            Deliveries = unresolvedPipeline.Deliveries
        };

        var usages = resolver.FindReferenceUsages([resolvedPipeline, unresolvedPipeline]);

        Assert.Equal(4, usages.Count);
        var resolvedUsages = usages.Where(usage => usage.FlowId == "flow-1").ToArray();
        Assert.All(resolvedUsages, usage => Assert.True(usage.IsResolved));
        Assert.All(resolvedUsages, usage => Assert.Equal("ftp-host", usage.Reference));
        Assert.Equal(["fetches[0].fetch.settings.host", "parses[0].parse.settings.schemaPath"], resolvedUsages.Select(static usage => usage.SettingPath));

        var unresolvedUsages = usages.Where(usage => usage.FlowId == "flow-2").ToArray();
        Assert.Equal(2, unresolvedUsages.Length);
        Assert.All(unresolvedUsages, usage => Assert.False(usage.IsResolved));
        Assert.All(unresolvedUsages, usage => Assert.Equal("missing-ref", usage.Reference));
    }
}
