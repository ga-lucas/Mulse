using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class FileSystemWatcherFetchModule(ILogger<FileSystemWatcherFetchModule> logger, TimeProvider timeProvider) : IPushTriggeredFetchModule
{
    private static readonly ConcurrentDictionary<string, WatchRegistration> Registrations = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("path", "Path", "The local directory to watch for created, changed, or renamed files.", true),
        new("searchPattern", "Search pattern", "A wildcard pattern used to filter watched files.", false, ModuleSettingInputKind.Text, "*.*"),
        new("recursive", "Recursive", "Watch subdirectories when enabled.", false, ModuleSettingInputKind.Boolean, "false"),
        new("includeExistingOnFirstRun", "Include existing files on first run", "Queues matching files that already exist when this watcher is first created.", false, ModuleSettingInputKind.Boolean, "true"),
        new("includeChanged", "Include changed files", "Queues change notifications as well as creates and renames when enabled.", false, ModuleSettingInputKind.Boolean, "true"),
        new("settleSeconds", "Settle seconds", "Wait time after the last file-system event before the file is fetched, to reduce partial reads.", false, ModuleSettingInputKind.Text, "2")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.FileSystem],
        [ModuleCapability.Ingestion, ModuleCapability.Polling]);

    public ModuleDescriptor Descriptor { get; } = new(
        "file-system-watcher-fetch",
        "File system watcher fetch",
        ModuleKind.Fetch,
        "Watches a directory with FileSystemWatcher and can initiate flows when new work arrives, while still returning only files queued for this flow instance.",
        SettingDescriptors,
        Recommendation);

    /// <summary><paramref name="input"/> is unused: this is a root source driven by file system change events.</summary>
    public async Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var options = ResolveOptions(step);
        var registration = GetOrCreateRegistration(context.FlowId, options);
        var dueFiles = registration.DequeueReadyFiles(timeProvider.GetUtcNow(), options.SettleWindow);
        if (dueFiles.Count == 0)
        {
            return IntegrationBatch.Empty;
        }

        var payloads = new List<IntegrationPayload>(dueFiles.Count);
        foreach (var fileChange in dueFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(fileChange.Path))
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(fileChange.Path, cancellationToken).ConfigureAwait(false);
            payloads.Add(new IntegrationPayload(
                Path.GetFileName(fileChange.Path),
                BinaryData.FromBytes(bytes),
                ResolveContentType(fileChange.Path),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["flowId"] = context.FlowId,
                    ["executionId"] = context.ExecutionId,
                    ["fullPath"] = fileChange.Path,
                    ["directory"] = Path.GetDirectoryName(fileChange.Path) ?? options.Directory,
                    ["extension"] = Path.GetExtension(fileChange.Path),
                    ["watchEvent"] = fileChange.EventKind,
                    ["watchObservedAtUtc"] = fileChange.ObservedAt.ToString("O"),
                    ["lastWriteTimeUtc"] = File.GetLastWriteTimeUtc(fileChange.Path).ToString("O")
                }));
        }

        return new IntegrationBatch(payloads);
    }

    public Task<IAsyncDisposable> RegisterTriggerAsync(
        PushFlowTriggerContext context,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = ResolveOptions(step);
        var registration = GetOrCreateRegistration(context.FlowId, options);
        var subscription = registration.Subscribe(context, options.SettleWindow);
        return Task.FromResult<IAsyncDisposable>(subscription);
    }

    private WatchRegistration GetOrCreateRegistration(string flowId, WatchOptions options)
    {
        return Registrations.GetOrAdd(
            CreateRegistrationKey(flowId, options),
            key => WatchRegistration.Create(key, options, logger, timeProvider, RemoveRegistration));
    }

    private static string CreateRegistrationKey(string flowId, WatchOptions options)
        => $"{flowId}|{options.Directory}|{options.SearchPattern}|{options.Recursive}|{options.IncludeChanged}|{options.IncludeExistingOnFirstRun}";

    private static WatchOptions ResolveOptions(ModuleStepDefinition step)
    {
        var directory = Path.GetFullPath(ModuleSettings.GetRequired(step.Settings, "path", "file-system-watcher-fetch"));
        var searchPattern = ModuleSettings.GetOptional(step.Settings, "searchPattern") ?? "*.*";
        var recursive = ModuleSettings.GetBoolean(step.Settings, "recursive", defaultValue: false, "file-system-watcher-fetch");
        var includeExistingOnFirstRun = ModuleSettings.GetBoolean(step.Settings, "includeExistingOnFirstRun", defaultValue: true, "file-system-watcher-fetch");
        var includeChanged = ModuleSettings.GetBoolean(step.Settings, "includeChanged", defaultValue: true, "file-system-watcher-fetch");
        var settleSeconds = ModuleSettings.GetInt32(step.Settings, "settleSeconds", 2, "file-system-watcher-fetch");

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Watch directory '{directory}' was not found.");
        }

        if (settleSeconds < 0)
        {
            throw new ArgumentException("Module 'file-system-watcher-fetch' requires a non-negative settleSeconds value.", nameof(step));
        }

        return new WatchOptions(
            directory,
            searchPattern,
            recursive,
            includeExistingOnFirstRun,
            includeChanged,
            TimeSpan.FromSeconds(settleSeconds));
    }

    private static void RemoveRegistration(string key)
    {
        if (Registrations.TryRemove(key, out var registration))
        {
            registration.Dispose();
        }
    }

    private static string ResolveContentType(string filePath)
    {
        return ContentTypeResolver.FromFileName(filePath);
    }

    private sealed class WatchRegistration : IDisposable
    {
        private readonly ConcurrentDictionary<string, PendingFileChange> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Guid, TriggerSubscriptionState> _subscriptions = new();
        private readonly string _registrationKey;
        private readonly WatchOptions _options;
        private readonly ILogger _logger;
        private readonly TimeProvider _timeProvider;
        private readonly Action<string> _removeRegistration;
        private readonly FileSystemWatcher _watcher;

        private WatchRegistration(
            string registrationKey,
            WatchOptions options,
            ILogger logger,
            TimeProvider timeProvider,
            Action<string> removeRegistration)
        {
            _registrationKey = registrationKey;
            _options = options;
            _logger = logger;
            _timeProvider = timeProvider;
            _removeRegistration = removeRegistration;
            _watcher = new FileSystemWatcher(options.Directory, options.SearchPattern)
            {
                IncludeSubdirectories = options.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Created += (_, args) => Queue(args.FullPath, "Created");
            if (options.IncludeChanged)
            {
                _watcher.Changed += (_, args) => Queue(args.FullPath, "Changed");
            }
            _watcher.Renamed += (_, args) => Queue(args.FullPath, "Renamed");
            _watcher.Error += (_, args) => _logger.LogWarning(args.GetException(), "File watcher for {Directory} raised an error.", _options.Directory);
        }

        public static WatchRegistration Create(
            string registrationKey,
            WatchOptions options,
            ILogger logger,
            TimeProvider timeProvider,
            Action<string> removeRegistration)
        {
            var registration = new WatchRegistration(registrationKey, options, logger, timeProvider, removeRegistration);
            if (options.IncludeExistingOnFirstRun)
            {
                registration.QueueExistingFiles();
            }

            logger.LogInformation(
                "Created file watcher registration for directory {Directory} with pattern {SearchPattern} (recursive: {Recursive}, includeChanged: {IncludeChanged}).",
                options.Directory,
                options.SearchPattern,
                options.Recursive,
                options.IncludeChanged);

            return registration;
        }

        public IReadOnlyList<PendingFileChange> DequeueReadyFiles(DateTimeOffset now, TimeSpan settleWindow)
        {
            var ready = _pendingFiles.Values
                .Where(change => now - change.ObservedAt >= settleWindow)
                .OrderBy(change => change.ObservedAt)
                .ToArray();

            var dequeued = new List<PendingFileChange>(ready.Length);
            foreach (var change in ready)
            {
                if (_pendingFiles.TryRemove(change.Path, out var removed))
                {
                    dequeued.Add(removed);
                }
            }

            return dequeued;
        }

        public IAsyncDisposable Subscribe(PushFlowTriggerContext context, TimeSpan settleWindow)
        {
            var subscriptionId = Guid.NewGuid();
            var subscription = new TriggerSubscriptionState(context, settleWindow);
            _subscriptions[subscriptionId] = subscription;
            if (!_pendingFiles.IsEmpty)
            {
                _ = NotifyAsync(subscription, "PendingWork");
            }

            return new TriggerSubscription(subscriptionId, RemoveSubscriptionAsync);
        }

        public void Dispose()
        {
            _watcher.Dispose();
        }

        private async ValueTask RemoveSubscriptionAsync(Guid subscriptionId)
        {
            _subscriptions.TryRemove(subscriptionId, out _);
            if (_subscriptions.IsEmpty)
            {
                await Task.Yield();
                _removeRegistration(_registrationKey);
            }
        }

        private void QueueExistingFiles()
        {
            var searchOption = _options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var filePath in Directory.EnumerateFiles(_options.Directory, _options.SearchPattern, searchOption))
            {
                Queue(filePath, "Existing");
            }
        }

        private void Queue(string filePath, string eventKind)
        {
            var observedAt = _timeProvider.GetUtcNow();
            _pendingFiles.AddOrUpdate(
                filePath,
                static (path, state) => new PendingFileChange(path, state.EventKind, state.ObservedAt),
                static (path, existing, state) => existing.ObservedAt >= state.ObservedAt
                    ? existing
                    : new PendingFileChange(path, state.EventKind, state.ObservedAt),
                (EventKind: eventKind, ObservedAt: observedAt));

            foreach (var subscription in _subscriptions.Values)
            {
                _ = NotifyAsync(subscription, eventKind);
            }
        }

        private static async Task NotifyAsync(TriggerSubscriptionState subscription, string eventKind)
        {
            if (subscription.SettleWindow > TimeSpan.Zero)
            {
                await Task.Delay(subscription.SettleWindow).ConfigureAwait(false);
            }

            subscription.Context.Signal($"File system event: {eventKind}");
        }
    }

    private sealed record WatchOptions(
        string Directory,
        string SearchPattern,
        bool Recursive,
        bool IncludeExistingOnFirstRun,
        bool IncludeChanged,
        TimeSpan SettleWindow);

    private sealed record PendingFileChange(string Path, string EventKind, DateTimeOffset ObservedAt);

    private sealed record TriggerSubscriptionState(PushFlowTriggerContext Context, TimeSpan SettleWindow);

    private sealed class TriggerSubscription(Guid subscriptionId, Func<Guid, ValueTask> unsubscribeAsync) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await unsubscribeAsync(subscriptionId).ConfigureAwait(false);
        }
    }
}
