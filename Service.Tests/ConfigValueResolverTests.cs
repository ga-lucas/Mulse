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
    public void Resolve_EmbeddedAdjacentAndRepeatedTokens_AreLiteralAndSinglePassAcrossAllStages()
    {
        const string template = "Server={{config:host}};Password={{secret:password}};{{config:host}}{{secret:password}}";
        const string password = @"$1\data;{{config:not-recursive}}";
        var state = new RuntimeMulseState { ConfigValues = { ["host"] = "db.example", ["password"] = password } };
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state));
        var pipeline = BuildPipelineWithToken(template);
        var steps = new[]
        {
            pipeline.Fetches[0].Fetch, pipeline.Parses[0].Parse,
            new ModuleStepDefinition { Module = "augment", Settings = new() { ["value"] = template } },
            pipeline.Deliveries[0].Render, pipeline.Deliveries[0].Delivers[0].Deliver,
            new ModuleStepDefinition { Module = "compensate", Settings = new() { ["value"] = template } }
        };
        pipeline.Augments.Add(steps[2]);
        pipeline.Deliveries[0].Render.Settings["value"] = template;
        pipeline.Deliveries[0].Delivers[0].Deliver.Settings["value"] = template;
        pipeline.Deliveries[0] = new DeliveryRouteDefinition
        {
            Render = steps[3],
            Delivers = [new DeliveryActionDefinition { Deliver = steps[4] }, new DeliveryActionDefinition { Deliver = steps[4], Compensation = steps[5] }]
        };

        var resolved = resolver.Resolve(pipeline);
        var expected = $"Server=db.example;Password={password};db.example{password}";
        Assert.Equal(expected, resolved.Fetches[0].Fetch.Settings["host"]);
        Assert.Equal(expected, resolved.Parses[0].Parse.Settings["schemaPath"]);
        Assert.Equal(expected, resolved.Augments[0].Settings["value"]);
        Assert.Equal(expected, resolved.Deliveries[0].Render.Settings["value"]);
        Assert.Equal(expected, resolved.Deliveries[0].Delivers[0].Deliver.Settings["value"]);
        Assert.Equal(expected, resolved.Deliveries[0].Delivers[1].Compensation!.Settings["value"]);
        Assert.All(steps, step => Assert.All(step.Settings.Values, value => Assert.Equal(template, value)));
        var usages = resolver.FindReferenceUsages([pipeline]);
        Assert.Equal(14, usages.Count);
        Assert.All(usages, usage => Assert.True(usage.IsResolved));
        Assert.Contains(usages, usage => usage.SettingPath.Contains("compensation"));
    }

    [Fact]
    public void Resolve_MissingEmbeddedReferences_ReportsAllWithoutRevealingValues()
    {
        var state = new RuntimeMulseState { ConfigValues = { ["password"] = "sensitive-value" } };
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(state));
        var pipeline = BuildPipelineWithToken("{{secret:password}};{{config:missing-a}}/{{secret:missing-b}}/{{config:missing-a}}");
        var error = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(pipeline));
        Assert.Contains("2 unresolved", error.Message);
        Assert.Contains("missing-a", error.Message);
        Assert.Contains("missing-b", error.Message);
        Assert.DoesNotContain("sensitive-value", error.Message);
        var usages = resolver.FindReferenceUsages([pipeline]);
        Assert.Equal(6, usages.Count);
        Assert.Equal(4, usages.Count(usage => !usage.IsResolved));
    }

    [Theory]
    [InlineData("prefix{{config:empty}}suffix", "prefixsuffix")]
    [InlineData("{{config:empty}}{{config:empty}}", "")]
    [InlineData("line one\n{{config:empty}}line two", "line one\nline two")]
    public void Resolve_EmptyStoredValuesAndMultilineSettings(string input, string expected)
    {
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState { ConfigValues = { ["empty"] = "" } }));
        Assert.Equal(expected, resolver.Resolve(BuildPipelineWithToken(input)).Fetches[0].Fetch.Settings["host"]);
    }

    [Theory]
    [InlineData("{{config:}}")]
    [InlineData("{{config:unfinished}")]
    [InlineData("{{other:value}}")]
    public void Resolve_NonTokenSyntaxRemainsLiteral(string value)
    {
        var resolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        Assert.Equal(value, resolver.Resolve(BuildPipelineWithToken(value)).Fetches[0].Fetch.Settings["host"]);
        Assert.Empty(resolver.FindReferenceUsages([BuildPipelineWithToken(value)]));
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
