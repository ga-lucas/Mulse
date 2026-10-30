using Microsoft.Extensions.Logging.Abstractions;
using Mulse.CompatibilityPack;

namespace Service.Tests;

public sealed class FileSystemWatcherTriggerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushRegistration_SignalsWorkBeforeAnyFetchExecution(bool existingFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-watcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "input.json");
            if (existingFile) await File.WriteAllTextAsync(path, "[{\"id\":1}]");
            var module = new FileSystemWatcherFetchModule(NullLogger<FileSystemWatcherFetchModule>.Instance, TimeProvider.System);
            var flowId = Guid.NewGuid().ToString("N");
            var signal = new TaskCompletionSource<PushFlowTriggerSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            var step = new ModuleStepDefinition
            {
                Module = "file-system-watcher-fetch",
                Settings = new Dictionary<string, string>
                {
                    ["path"] = directory,
                    ["searchPattern"] = "*.json",
                    ["settleSeconds"] = "0",
                    ["includeChanged"] = "false",
                    ["includeExistingOnFirstRun"] = "true"
                }
            };
            await using var subscription = await module.RegisterTriggerAsync(
                new PushFlowTriggerContext(flowId, step.Module, value => signal.TrySetResult(value)), step, CancellationToken.None);
            if (!existingFile) await File.WriteAllTextAsync(path, "[{\"id\":1}]");

            var received = await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(flowId, received.FlowId);
            Assert.Equal(step.Module, received.ModuleId);
            var batch = await module.FetchAsync(new FlowExecutionContext(flowId, "execution", DateTimeOffset.UtcNow),
                IntegrationBatch.Empty, step, CancellationToken.None);
            var payload = Assert.Single(batch.Payloads);
            Assert.Equal("input.json", payload.Name);
            Assert.Equal("[{\"id\":1}]", payload.Content.ToString());
            var empty = await module.FetchAsync(new FlowExecutionContext(flowId, "next", DateTimeOffset.UtcNow),
                IntegrationBatch.Empty, step, CancellationToken.None);
            Assert.Empty(empty.Payloads);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
