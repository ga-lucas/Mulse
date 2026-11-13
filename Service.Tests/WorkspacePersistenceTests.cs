using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mulse.Modules.Security;
using Mulse.Persistence;
using Mulse.Persistence.PostgreSql;
using Mulse.Persistence.Sqlite;
using Mulse.Persistence.SqlServer;
using Npgsql;

namespace Service.Tests;

public sealed class WorkspacePersistenceTests
{
    public static IEnumerable<object[]> Providers()
    {
        yield return ["Sqlite"];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULSE_TEST_SQLSERVER"))) yield return ["SqlServer"];
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MULSE_TEST_POSTGRESQL"))) yield return ["PostgreSql"];
    }

    private static PipelineDefinition Flow(string name = "Original") => new()
    {
        Id = "orders", Name = name, Category = "Invoices", Enabled = false,
        Fetches = [new() { Id = "fetch", Fetch = new() { Module = "source", Settings = new() { ["password"] = "private-payload-sentinel" } } }]
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Workspace_RoundTripsIndependentRecordsAndImmutableRevisions(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var store = fixture.Store();
        await store.UpsertFlowAsync(Flow(), default);
        await store.UpsertFlowAsync(Flow("Edited"), default);
        await store.SetConfigValueAsync("credential", "private-payload-sentinel", default);
        await store.UpsertManagedPackageAsync(new() { Id = "plugin", AssemblyPath = "C:\\plugins\\example.dll", Enabled = false }, default);
        await store.UpsertOrchestrationCheckpointAsync(new() { FlowId = "orders", CorrelationKey = "tenant", Bookmark = "waiting" }, default);
        await store.UpsertRetryStateAsync(new()
        {
            FlowId = "orders", ExecutionId = "execution", StageId = "fetch:fetch", PipelineSnapshot = Flow(), AttemptsMade = 2,
            NextAttemptAt = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.FromHours(2)),
            InputPayloads = [new() { Name = "input", ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("private-payload-sentinel")), ContentType = "text/plain" }]
        }, default);
        var reloaded = fixture.Store().GetState();
        Assert.Equal("Edited", Assert.Single(reloaded.Pipelines).Name);
        Assert.Equal("private-payload-sentinel", reloaded.ConfigValues["CREDENTIAL"]);
        Assert.Equal("C:\\plugins\\example.dll", Assert.Single(reloaded.ManagedPackages).AssemblyPath);
        Assert.Equal("waiting", Assert.Single(reloaded.OrchestrationCheckpoints).Bookmark);
        var retry = Assert.Single(reloaded.PendingRetries);
        Assert.Equal("Original", retry.PipelineSnapshot!.Name);
        Assert.Equal(2, retry.AttemptsMade);
        Assert.Equal("private-payload-sentinel", Assert.Single(retry.InputPayloads).Restore().GetText());
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(new long[] { 1, 2 }, db.FlowRevisions.OrderBy(row => row.Number).Select(row => row.Number).ToArray());
        var firstRevision = db.FlowRevisions.OrderBy(row => row.Number).First();
        var firstBytes = firstRevision.Definition.ToArray();
        using var originalDefinition = JsonDocument.Parse(firstBytes);
        Assert.Equal("Original", originalDefinition.RootElement.GetProperty("name").GetString());
        Assert.Equal(retry.NextAttemptAt.UtcTicks, Assert.Single(db.Retries).NextAttemptUtcTicks);
        Assert.Equal(1, db.Workspaces.Count());
        Assert.Equal(4, db.Database.GetAppliedMigrations().Count());
        Assert.False(db.Database.HasPendingModelChanges());
        await store.DeleteRetryStateAsync("ORDERS", "EXECUTION", "FETCH:FETCH", default);
        await store.DeleteOrchestrationCheckpointAsync("orders", "tenant", default);
        await store.DeleteManagedPackageAsync("PLUGIN", default);
        await store.DeleteConfigValueAsync("credential", default);
        using var after = fixture.Factory.CreateDbContext();
        Assert.Equal(firstBytes, after.FlowRevisions.Single(row => row.Id == firstRevision.Id).Definition);
        Assert.Equal(2, after.FlowRevisions.Count());
        Assert.Empty(after.Retries);
        Assert.Empty(after.Checkpoints);
        Assert.Empty(after.ModulePackages);
        Assert.Empty(after.ConfigurationValues);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LegacyImport_IsAtomicRetainsPendingWorkAndNeverReimports(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var legacy = fixture.Legacy();
        await legacy.UpsertFlowAsync(Flow(), default);
        await legacy.SetConfigValueAsync("credential", "private-payload-sentinel", default);
        await legacy.UpsertManagedPackageAsync(new() { Id = "plugin", AssemblyPath = "C:\\plugin.dll", Enabled = false }, default);
        await legacy.UpsertRetryStateAsync(new() { FlowId = "orders", ExecutionId = "legacy", StageId = "augment:0" }, default);
        await legacy.UpsertOrchestrationCheckpointAsync(new() { FlowId = "orders", CorrelationKey = "tenant", Batch = new() }, default);
        await legacy.SetEncryptionAsync(true, default);
        var originalFile = await File.ReadAllBytesAsync(fixture.StatePath);
        var imported = fixture.Store();
        Assert.True(imported.GetEncryptionStatus().Enabled);
        Assert.Equal(JsonSerializer.Serialize(legacy.GetState()), JsonSerializer.Serialize(imported.GetState()));
        Assert.Null(Assert.Single(imported.GetState().PendingRetries).PipelineSnapshot);
        Assert.NotNull(Assert.Single(imported.GetState().OrchestrationCheckpoints).Batch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => imported.DeleteFlowAsync("orders", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => imported.UpsertFlowAsync(Flow("Unsafe edit"), default));
        Assert.Equal(originalFile, await File.ReadAllBytesAsync(fixture.StatePath));
        await legacy.SetConfigValueAsync("unimported", "later-file-change", default);
        Assert.False(fixture.Store().GetState().ConfigValues.ContainsKey("unimported"));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(fixture.StatePath, Assert.Single(db.Workspaces).ImportSource);
        Assert.Single(db.FlowRevisions);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Encryption_RewritesAllPayloadsAndHistoricalRevisions_AndPreservesSavedMode(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var store = fixture.Store();
        await store.UpsertFlowAsync(Flow(), default);
        await store.UpsertFlowAsync(Flow("Edited"), default);
        await store.SetConfigValueAsync("credential", "private-payload-sentinel", default);
        await store.UpsertManagedPackageAsync(new() { Id = "plugin", AssemblyPath = "private-payload-sentinel" }, default);
        await store.UpsertOrchestrationCheckpointAsync(new() { FlowId = "orders", CorrelationKey = "tenant", StateValues = new() { ["sensitive"] = "private-payload-sentinel" } }, default);
        await store.UpsertRetryStateAsync(new() { FlowId = "orders", ExecutionId = "execution", StageId = "fetch:fetch", PipelineSnapshot = Flow() }, default);
        Assert.True((await store.SetEncryptionAsync(true, default)).Enabled);
        using (var db = fixture.Factory.CreateDbContext())
        {
            Assert.All(AllPayloads(db), payload => Assert.DoesNotContain("private-payload-sentinel", Encoding.UTF8.GetString(payload)));
            Assert.Equal("primary", Assert.Single(db.Workspaces).KeyId);
            Assert.Equal(2, db.FlowRevisions.Count());
        }
        var reloaded = fixture.Store();
        Assert.True(reloaded.GetEncryptionStatus().Enabled);
        Assert.Equal("private-payload-sentinel", reloaded.GetState().ConfigValues["credential"]);
        var originalKey = Environment.GetEnvironmentVariable(fixture.KeyVariable);
        Environment.SetEnvironmentVariable(fixture.KeyVariable, null);
        Assert.Throws<InvalidOperationException>(() => fixture.Store());
        Environment.SetEnvironmentVariable(fixture.KeyVariable, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<AuthenticationTagMismatchException>(() => fixture.Store());
        Environment.SetEnvironmentVariable(fixture.KeyVariable, originalKey);
        Assert.Throws<InvalidOperationException>(() => fixture.Store(keyId: "wrong-key-id"));
        Assert.False((await reloaded.SetEncryptionAsync(false, default)).Enabled);
        Environment.SetEnvironmentVariable(fixture.KeyVariable, null);
        var disabled = fixture.Store(encryptedDefault: true);
        Assert.False(disabled.GetEncryptionStatus().Enabled);
        Assert.Equal("private-payload-sentinel", disabled.GetState().ConfigValues["credential"]);
        using var final = fixture.Factory.CreateDbContext();
        Assert.Equal(2, final.FlowRevisions.Count());
        Assert.All(final.FlowRevisions, revision => Assert.Contains("private-payload-sentinel", Encoding.UTF8.GetString(revision.Definition)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Encryption_RejectsSwappedRecordPayloadsInsteadOfLoadingAnotherSecret(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var store = fixture.Store();
        await store.SetConfigValueAsync("first", "first-secret", default);
        await store.SetConfigValueAsync("second", "second-secret", default);
        await store.SetEncryptionAsync(true, default);
        using (var db = fixture.Factory.CreateDbContext())
        {
            var rows = db.ConfigurationValues.ToArray();
            (rows[0].Payload, rows[1].Payload) = (rows[1].Payload, rows[0].Payload);
            await db.SaveChangesAsync();
        }
        Assert.Throws<AuthenticationTagMismatchException>(() => fixture.Store());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FailedOperationalWrite_RollsBackWorkspaceVersionAndDoesNotPublishMemory(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var store = fixture.Store();
        using var cancellation = new CancellationTokenSource();
        foreach (var cancel in new[] { false, true })
        {
            fixture.Failure.OnRetryWrite = () =>
            {
                if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                throw new IOException("Injected operational write failure after workspace version claim.");
            };
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertRetryStateAsync(new() { FlowId = "orders", ExecutionId = "execution", StageId = "fetch:fetch" }, cancellation.Token));
            else
                await Assert.ThrowsAsync<IOException>(() => store.UpsertRetryStateAsync(new() { FlowId = "orders", ExecutionId = "execution", StageId = "fetch:fetch" }, default));
            Assert.Empty(store.GetState().PendingRetries);
            Assert.Empty(fixture.Store().GetState().PendingRetries);
        }
        fixture.Failure.OnRetryWrite = null;
        await store.SetConfigValueAsync("successful", "after-failure", default);
        Assert.Equal("after-failure", fixture.Store().GetState().ConfigValues["successful"]);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StaleServiceSnapshot_CannotOverwriteAnotherCommittedWorkspace(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var first = fixture.Store();
        var stale = fixture.Store();
        await first.SetConfigValueAsync("first", "committed", default);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SetConfigValueAsync("second", "must-not-commit", default));
        Assert.Throws<InvalidOperationException>(() => stale.GetState());
        var reloaded = fixture.Store().GetState();
        Assert.Equal("committed", reloaded.ConfigValues["first"]);
        Assert.False(reloaded.ConfigValues.ContainsKey("second"));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LostCommitAcknowledgement_BlocksFurtherWorkUntilRestartWithoutReplayingCommittedData(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var store = fixture.Store();
        fixture.CommitFailure.Fail = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetConfigValueAsync("committed", "saved-before-acknowledgement-loss", default));
        Assert.Contains("commit was not acknowledged", error.Message);
        Assert.Throws<InvalidOperationException>(() => store.GetState());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetConfigValueAsync("unsafe", "must-not-write", default));
        fixture.CommitFailure.Fail = false;
        var reloaded = fixture.Store();
        Assert.Equal("saved-before-acknowledgement-loss", reloaded.GetState().ConfigValues["committed"]);
        Assert.False(reloaded.GetState().ConfigValues.ContainsKey("unsafe"));
        await reloaded.SetConfigValueAsync("restarted", "safe-to-continue", default);
        Assert.Equal("safe-to-continue", fixture.Store().GetState().ConfigValues["restarted"]);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ProcessingRetry_ResumesOriginalStageAfterDatabaseRestart(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var fetches = 0;
        var attempts = 0;
        var before = new List<IntegrationBatch>();
        var after = new List<IntegrationBatch>();
        var targets = new List<string>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("source", (_, _, _, _) =>
            {
                fetches++;
                return Task.FromResult(new IntegrationBatch([new("message", BinaryData.FromString("retained-message"), "text/plain", new Dictionary<string, string>())]));
            }))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(DelegateModuleFactory.Deliver("before", before))
            .AddDeliver(new DelegateDeliverModule("after", (_, batch, step, _) =>
            {
                targets.Add(step.Settings["target"]);
                if (++attempts == 1) throw new IOException("Transient destination failure.");
                after.Add(batch);
                return Task.CompletedTask;
            }));
        var flow = new PipelineDefinition
        {
            Id = "orders", Enabled = true, Retry = new() { MaxAttempts = 2, Delay = TimeSpan.Zero },
            Fetches = [new() { Id = "f", Fetch = new() { Module = "source" } }],
            Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
            Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "before" } },
                new() { Deliver = new() { Module = "after", Settings = new() { ["target"] = "{{config:destination}}" } } }] }]
        };
        FlowRuntime Runtime(RuntimeConfigurationStore store) => new(new FlowDefinitionService(store, catalog), catalog,
            new FlowOrchestrationStateStore(store), new FlowRetryStateStore(store), new ConfigValueResolver(store), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowRuntime>.Instance);
        var store = fixture.Store();
        await store.UpsertFlowAsync(flow, default);
        await store.SetConfigValueAsync("destination", "original-destination", default);
        Assert.Equal(FlowExecutionOutcome.Retrying, (await Runtime(store).ExecuteAsync(flow.Id, default)).Outcome);
        var pending = Assert.Single(fixture.Store().GetState().PendingRetries);
        Assert.Equal("delivery:0:deliver:1", pending.StageId);
        await store.UpsertFlowAsync(new() { Id = "orders", Enabled = true }, default);
        await store.DeleteConfigValueAsync("destination", default);
        var restarted = fixture.Store();
        Assert.Equal(FlowExecutionOutcome.Completed, (await Runtime(restarted).ResumeRetryAsync(pending, default))!.Outcome);
        Assert.Equal(1, fetches);
        Assert.Single(before);
        Assert.Equal("retained-message", Assert.Single(Assert.Single(after).Payloads).GetText());
        Assert.Equal(new[] { "original-destination", "original-destination" }, targets);
        Assert.Empty(fixture.Store().GetState().PendingRetries);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FailedImport_RollsBackEveryTableAndLeavesLegacyFileUntouched(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var legacy = fixture.Legacy();
        await legacy.UpsertFlowAsync(Flow(), default);
        await legacy.UpsertRetryStateAsync(new() { FlowId = "orders", ExecutionId = "execution", StageId = "fetch:fetch" }, default);
        var original = await File.ReadAllBytesAsync(fixture.StatePath);
        fixture.Failure.OnRetryWrite = () => throw new IOException("Injected import failure.");
        Assert.Throws<IOException>(() => fixture.Store());
        using (var db = fixture.Factory.CreateDbContext())
        {
            Assert.Empty(db.Workspaces);
            Assert.Empty(db.FlowRevisions);
            Assert.Empty(db.Flows);
            Assert.Empty(db.Retries);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.StatePath));
        fixture.Failure.OnRetryWrite = null;
        Assert.Equal("execution", Assert.Single(fixture.Store().GetState().PendingRetries).ExecutionId);
    }

    [Fact]
    public void CorruptLegacyImport_IsNotReplacedWithSeedData()
    {
        using var fixture = new DatabaseFixture("Sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.StatePath)!);
        File.WriteAllText(fixture.StatePath, "null");
        Assert.Throws<InvalidOperationException>(() => fixture.Store());
        Assert.Equal("null", File.ReadAllText(fixture.StatePath));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Empty(db.Workspaces);
        Assert.Empty(db.Flows);
    }

    [Fact]
    public async Task ConfiguredSqlite_UsesContentRootDatabaseAndFullDurability()
    {
        using var fixture = new DatabaseFixture("Sqlite");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite", ["ConnectionStrings:Mulse"] = "Data Source=Data\\registered.db;Pooling=False"
        }).Build();
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeHostEnvironment { ContentRootPath = fixture.DirectoryPath })
            .AddMulsePlatform(config, fixture.DirectoryPath).BuildServiceProvider();
        var store = services.GetRequiredService<IRuntimeConfigurationStore>();
        await store.SetConfigValueAsync("registered", "persisted", default);
        Assert.True(File.Exists(Path.Combine(fixture.DirectoryPath, "Data", "registered.db")));
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "Data", "mulse-runtime.json")));
        using var db = services.GetRequiredService<IDbContextFactory<MulseDbContext>>().CreateDbContext();
        db.Database.OpenConnection();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA synchronous";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar());
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("SqlServer")]
    [InlineData("PostgreSql")]
    public void Providers_HaveIndependentMigrationsMatchingTheirModel(string provider)
    {
        using var fixture = new DatabaseFixture(provider, modelOnly: true);
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(4, db.Database.GetMigrations().Count());
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript();
        Assert.Contains("FlowRevisions", sql);
        Assert.Contains("Retries", sql);
        Assert.Contains("Workspaces", sql);
        Assert.Equal(11, db.Model.GetEntityTypes().Count());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Inbox_AcknowledgedMessagesSurviveRestartAndClaimsAreNeverBlindlyRequeued(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var first = fixture.Journal(encrypted: true);
        var id = Guid.NewGuid();
        var batch = new IntegrationBatch([new("accepted", BinaryData.FromString("durable-message-sentinel"), "text/plain", new Dictionary<string, string> { ["correlationId"] = "request-one" })]);
        await first.Journal.EnqueueAsync("http:orders", "orders", id, batch, default);
        using (var db = fixture.Factory.CreateDbContext())
        {
            var saved = Assert.Single(db.Messages);
            Assert.Equal("Pending", saved.State);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("durable-message-sentinel")), Encoding.UTF8.GetString(saved.Payload));
        }
        var restarted = fixture.Journal();
        var context = new FlowExecutionContext("orders", "run-one", DateTimeOffset.UtcNow);
        await restarted.Journal.BeginAsync(Flow(), context, default);
        var claimed = await restarted.Journal.ClaimAsync("HTTP:ORDERS", context, default);
        Assert.Equal("durable-message-sentinel", Assert.Single(claimed.Payloads).GetText());
        Assert.Equal("request-one", claimed.Payloads[0].Metadata["correlationId"]);
        Assert.Empty((await restarted.Journal.ClaimAsync("http:orders", context, default)).Payloads);
        var recovered = fixture.Journal();
        await recovered.Journal.RecoverAsync(default);
        Assert.True(await recovered.Journal.IsBlockedAsync("ORDERS", default));
        Assert.Empty((await recovered.Journal.ClaimAsync("http:orders", new("orders", "another", DateTimeOffset.UtcNow), default)).Payloads);
        using (var db = fixture.Factory.CreateDbContext())
        {
            Assert.Equal("Held", Assert.Single(db.Messages).State);
            Assert.Equal("Held", Assert.Single(db.Executions).State);
        }
        await recovered.Store.SetEncryptionAsync(false, default);
        var plaintext = await recovered.Journal.ReadMessageAsync(id, default);
        Assert.Equal("durable-message-sentinel", Assert.Single(JsonSerializer.Deserialize<List<FlowOrchestrationPayloadSnapshot>>(plaintext, JsonSerializerOptions.Web)!).Restore().GetText());
        await recovered.Store.SetEncryptionAsync(true, default);
        Assert.Equal(plaintext, await fixture.Journal().Journal.ReadMessageAsync(id, default));
        await fixture.Journal().Journal.EnqueueAsync("http:orders", "orders", id, batch, default);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => fixture.Journal().Journal.EnqueueAsync("http:other", "orders", id, batch, default));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Journal_DeliveredButUnacknowledgedCommit_IsHeldWithoutReinvokingDestination(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var setup = fixture.Journal();
        var invocations = 0;
        var pipeline = new PipelineDefinition { Id = "orders", Enabled = true, Fetches = Flow().Fetches,
            Retry = new() { MaxAttempts = 3, Delay = TimeSpan.Zero } };
        pipeline.Parses.Add(new() { Id = "p", FetchNodeIds = ["fetch"], Parse = new() { Module = "parse" } });
        pipeline.Deliveries.Add(new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] });
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("source", (_, _, _, _) => Task.FromResult(new IntegrationBatch([new("body", BinaryData.FromString("one"), "text/plain", new Dictionary<string, string>())]))))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(new DelegateDeliverModule("deliver", (_, _, _, _) => { invocations++; throw new IOException("Reply lost after destination accepted the request."); }));
        FlowRuntime Runtime((RuntimeConfigurationStore Store, FlowJournal Journal) pair) => new(new FakeFlowDefinitionService(pipeline), catalog,
            new FlowOrchestrationStateStore(pair.Store), new FlowRetryStateStore(pair.Store), new ConfigValueResolver(pair.Store),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowRuntime>.Instance, pair.Journal);
        var error = await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(setup).ExecuteAsync("orders", default));
        Assert.Contains("outcome is uncertain", error.Message);
        Assert.Equal(1, invocations);
        using (var db = fixture.Factory.CreateDbContext())
        {
            Assert.Equal("Unknown", Assert.Single(db.Deliveries).State);
            Assert.Equal("Held", Assert.Single(db.Executions).State);
            Assert.Single(db.Retries);
        }
        var recovered = fixture.Journal();
        await recovered.Journal.RecoverAsync(default);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(recovered).ExecuteAsync("orders", default));
        Assert.Equal(1, invocations);
        Assert.Single(await recovered.Journal.ListAsync("orders", 50, default));
        var retry = Assert.Single(recovered.Store.GetState().PendingRetries);
        using var inspected = fixture.Factory.CreateDbContext();
        var delivery = Assert.Single(inspected.Deliveries);
        await Assert.ThrowsAsync<ArgumentException>(() => recovered.Journal.ConfirmDeliveredAsync(retry.ExecutionId, delivery.Id, "guess", default));
        await recovered.Journal.ConfirmDeliveredAsync(retry.ExecutionId, delivery.Id, "Destination audit independently confirms receipt 123.", default);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => recovered.Journal.ConfirmDeliveredAsync(retry.ExecutionId, delivery.Id, "Destination confirms again.", default));
        Assert.False(await recovered.Journal.IsDeliveryHeldAsync(retry.ExecutionId, default));
        Assert.Equal(FlowExecutionOutcome.Completed, (await Runtime(recovered).ResumeRetryAsync(retry, default))!.Outcome);
        Assert.Equal(1, invocations);
        Assert.Empty(recovered.Store.GetState().PendingRetries);
        Assert.Equal("Completed", Assert.Single(await recovered.Journal.ListAsync("orders", 50, default)).State);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Journal_CommittedDeliveryIsSkippedAndResponsesRestoredWithoutReplaying(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var setup = fixture.Journal(encrypted: true);
        var context = new FlowExecutionContext("orders", "committed", DateTimeOffset.UtcNow);
        var batch = new IntegrationBatch([new("message", BinaryData.FromString("body"), "text/plain", new Dictionary<string, string>())]);
        await setup.Journal.BeginAsync(Flow(), context, default);
        Assert.True(await setup.Journal.PrepareDeliveryAsync(context, "delivery:0:deliver:0", new() { Module = "external" }, batch, default));
        await setup.Journal.DeliveryStateAsync(context, "delivery:0:deliver:0", "InFlight", default);
        context.CaptureResponse(new IntegrationBatch([new("reply", BinaryData.FromString("response-sentinel"), "text/plain", new Dictionary<string, string>())]));
        await setup.Journal.DeliveryStateAsync(context, "delivery:0:deliver:0", "Succeeded", default);
        var restarted = fixture.Journal();
        var resumed = new FlowExecutionContext("orders", "committed", context.StartedAt);
        Assert.False(await restarted.Journal.PrepareDeliveryAsync(resumed, "delivery:0:deliver:0", new() { Module = "external" }, batch, default));
        Assert.Equal("response-sentinel", Assert.Single(resumed.CapturedResponses).GetText());
        using var db = fixture.Factory.CreateDbContext();
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("response-sentinel")), Encoding.UTF8.GetString(Assert.Single(db.Deliveries).Payload));
        await restarted.Store.SetEncryptionAsync(false, default);
        using var unencrypted = fixture.Factory.CreateDbContext();
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("response-sentinel")), Encoding.UTF8.GetString((await unencrypted.Deliveries.AsNoTracking().SingleAsync()).Payload));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Journal_StageHistoryRetentionProtectsPendingWorkAndEncryptionRewritesAllRecords(string provider)
    {
        using var fixture = new DatabaseFixture(provider);
        var clock = new JournalClock();
        var options = new FlowHistoryOptions { CaptureStagePayloads = true, RetentionDays = 1 };
        var pair = fixture.Journal(history: options, clock: clock);
        var context = new FlowExecutionContext("orders", "terminal", clock.Now);
        await pair.Journal.BeginAsync(Flow(), context, default);
        await pair.Journal.StageAsync(context, "parse:p", "Succeeded", new IntegrationBatch([new("body", BinaryData.FromString("history-sentinel"), "text/plain", new Dictionary<string, string>())]), "event-detail-sentinel", default);
        await pair.Journal.FinishAsync(context, "Completed", null, default);
        var pendingId = Guid.NewGuid();
        await pair.Journal.EnqueueAsync("http:pending", "pending", pendingId, IntegrationBatch.Empty, default);
        await pair.Store.SetEncryptionAsync(true, default);
        using (var db = fixture.Factory.CreateDbContext())
        {
            Assert.All(db.ExecutionEvents, row => Assert.DoesNotContain("event-detail-sentinel", Encoding.UTF8.GetString(row.Detail)));
            Assert.All(db.Messages, row => Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("history-sentinel")), Encoding.UTF8.GetString(row.Payload)));
        }
        var detail = JsonSerializer.Serialize(await pair.Journal.DetailAsync("terminal", default));
        Assert.Contains("event-detail-sentinel", detail);
        Assert.DoesNotContain("history-sentinel", detail);
        clock.Now = clock.Now.AddDays(2);
        Assert.Equal(1, await pair.Journal.PurgeAsync(default));
        using (var db = fixture.Factory.CreateDbContext())
        {
            Assert.Empty(db.Executions);
            Assert.Empty(db.ExecutionEvents);
            Assert.Equal(pendingId, Assert.Single(db.Messages).Id);
        }
        Assert.NotEmpty(await pair.Journal.PendingChannelsAsync(default));
    }

    private sealed class JournalClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static IEnumerable<byte[]> AllPayloads(MulseDbContext db)
        => db.FlowRevisions.Select(row => row.Definition).AsEnumerable()
            .Concat(db.ConfigurationValues.Select(row => row.Payload))
            .Concat(db.ModulePackages.Select(row => row.Payload))
            .Concat(db.Checkpoints.Select(row => row.Payload))
            .Concat(db.Retries.Select(row => row.Payload))
            .Concat(db.Workspaces.Select(row => row.PluginDirectories));

    internal sealed class DatabaseFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "mulse-ef-tests-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(DirectoryPath, "legacy.json");
        public string KeyVariable { get; } = "MULSE_TEST_DATABASE_KEY_" + Guid.NewGuid().ToString("N");
        public FailureInterceptor Failure { get; } = new();
        public CommitFailureInterceptor CommitFailure { get; } = new();
        public IDbContextFactory<MulseDbContext> Factory { get; }
        private readonly bool _modelOnly;
        private bool _opened;

        public DatabaseFixture(string provider, bool modelOnly = false)
        {
            _modelOnly = modelOnly;
            Directory.CreateDirectory(DirectoryPath);
            Environment.SetEnvironmentVariable(KeyVariable, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var database = "mulse_ef_tests_" + Guid.NewGuid().ToString("N");
            Factory = provider switch
            {
                "Sqlite" => new ContextFactory(() =>
                {
                    var options = new DbContextOptionsBuilder<SqliteMulseDbContext>().AddInterceptors(Failure, CommitFailure);
                    SqlitePersistence.Configure(options, $"Data Source={Path.Combine(DirectoryPath, "test.db")};Pooling=False");
                    return new SqliteMulseDbContext(options.Options);
                }),
                "SqlServer" => new ContextFactory(() => new SqlServerMulseDbContext(new DbContextOptionsBuilder<SqlServerMulseDbContext>()
                    .UseSqlServer(new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MULSE_TEST_SQLSERVER") ?? "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true")
                    { InitialCatalog = database, Pooling = false }.ToString()).AddInterceptors(Failure, CommitFailure).Options)),
                "PostgreSql" => new ContextFactory(() => new PostgreSqlMulseDbContext(new DbContextOptionsBuilder<PostgreSqlMulseDbContext>()
                    .UseNpgsql(new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("MULSE_TEST_POSTGRESQL") ?? "Host=localhost;Username=postgres")
                    { Database = database, Pooling = false }.ToString()).AddInterceptors(Failure, CommitFailure).Options)),
                _ => throw new ArgumentOutOfRangeException(nameof(provider))
            };
        }

        public RuntimeConfigurationStore Store(string keyId = "primary", bool encryptedDefault = false)
        {
            _opened = true;
            return new(Microsoft.Extensions.Options.Options.Create(Options(encryptedDefault)), new FakeHostEnvironment(), database:
                new WorkspaceDatabase(Factory, new DatabasePayloadCodec(keyId, KeyVariable), applyMigrations: true));
        }

        public (RuntimeConfigurationStore Store, FlowJournal Journal) Journal(bool encrypted = false, FlowHistoryOptions? history = null, TimeProvider? clock = null)
        {
            _opened = true;
            var database = new WorkspaceDatabase(Factory, new DatabasePayloadCodec("primary", KeyVariable), applyMigrations: true);
            var store = new RuntimeConfigurationStore(Microsoft.Extensions.Options.Options.Create(Options(encrypted)), new FakeHostEnvironment(), database: database);
            return (store, new FlowJournal(database, store, Microsoft.Extensions.Options.Options.Create(history ?? new()), clock ?? TimeProvider.System));
        }

        public RuntimeConfigurationStore Legacy() => new(Microsoft.Extensions.Options.Options.Create(Options(false)), new FakeHostEnvironment());
        private MulseOptions Options(bool encrypted) => new()
        {
            RuntimeStatePath = StatePath, PluginDirectories = ["C:\\plugins"],
            Encryption = new RuntimeStateEncryptionOptions { Enabled = encrypted, KeyEnvironmentVariable = KeyVariable }
        };

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(KeyVariable, null);
            if (_opened && !_modelOnly)
            {
                using var db = Factory.CreateDbContext();
                db.Database.EnsureDeleted();
            }
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class ContextFactory(Func<MulseDbContext> create) : IDbContextFactory<MulseDbContext>
    {
        public MulseDbContext CreateDbContext() => create();
    }

    internal sealed class CommitFailureInterceptor : DbTransactionInterceptor
    {
        public bool Fail { get; set; }
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Simulated loss of commit acknowledgement.");
            return Task.CompletedTask;
        }
    }

    internal sealed class FailureInterceptor : SaveChangesInterceptor
    {
        public Action? OnRetryWrite { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RetryRecord>().Any(entry => entry.State != EntityState.Unchanged)) OnRetryWrite?.Invoke();
            return ValueTask.FromResult(result);
        }
    }
}
