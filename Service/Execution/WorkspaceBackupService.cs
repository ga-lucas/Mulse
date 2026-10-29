using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Service.Models;

namespace Service.Execution;

public sealed class WorkspaceBackupService(IRuntimeConfigurationStore store, IModuleCatalog catalog)
{
    public const int MaximumBackupBytes = 10 * 1024 * 1024;
    private static readonly byte[] Magic = "MULSEBK1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public byte[] Export(string password)
    {
        ValidatePassword(password);
        var state = store.GetState();
        var packages = state.ManagedPackages.ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var package in catalog.GetPackages().Where(package => package.SourceKind != RuntimePackageSourceKind.BuiltIn))
        {
            packages.TryAdd(package.Id, new ManagedModulePackageDefinition { Id = package.Id, AssemblyPath = package.AssemblyPath, Enabled = package.IsLoaded });
        }
        var document = new WorkspaceBackupDocument(1, DateTimeOffset.UtcNow, state.Pipelines, packages.Values.ToArray(), state.ConfigValues);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (plaintext.Length > MaximumBackupBytes - 52)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidOperationException("The workspace exceeds the 10 MB backup limit.");
        }
        var result = new byte[52 + plaintext.Length];
        Magic.CopyTo(result, 0);
        RandomNumberGenerator.Fill(result.AsSpan(8, 16));
        var key = DeriveKey(password, result.AsSpan(8, 16));
        try
        {
            var protector = new Mulse.Modules.Security.AesGcmRuntimeStatePayloadProtector(key);
            protector.Protect(plaintext).CopyTo(result, 24);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public WorkspaceRestorePreview Preview(RestoreWorkspaceRequest request)
    {
        var document = Read(request);
        return CreatePreview(document, store.GetState(), request.Mode);
    }

    public async Task<WorkspaceRestorePreview> RestoreAsync(RestoreWorkspaceRequest request, CancellationToken cancellationToken)
    {
        if (request.Confirmation != "RESTORE WORKSPACE")
        {
            throw new ArgumentException("Type RESTORE WORKSPACE to confirm restoration.");
        }
        var document = Read(request);
        var preview = CreatePreview(document, store.GetState(), request.Mode);
        var restored = new RuntimeMulseState
        {
            Pipelines = document.Flows.Select(flow => FlowMappings.CopyPipeline(flow, flow.Id, false, flow.Category)).ToList(),
            ManagedPackages = document.Packages.Select(package => new ManagedModulePackageDefinition { Id = package.Id, AssemblyPath = package.AssemblyPath, Enabled = false }).ToList(),
            ConfigValues = new Dictionary<string, string>(document.ConfigValues, StringComparer.OrdinalIgnoreCase)
        };
        await store.RestoreWorkspaceAsync(restored, request.Mode == "replace", cancellationToken).ConfigureAwait(false);
        return preview;
    }

    private WorkspaceBackupDocument Read(RestoreWorkspaceRequest request)
    {
        ValidatePassword(request.Password);
        if (request.Mode is not ("merge" or "replace"))
        {
            throw new ArgumentException("Restore mode must be merge or replace.");
        }
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > (MaximumBackupBytes + 2) / 3 * 4)
        {
            throw new ArgumentException("Select an encrypted Mulse backup no larger than 10 MB.");
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.Content); }
        catch (FormatException) { throw new ArgumentException("The backup content is not valid base64."); }
        if (bytes.Length < 52 || bytes.Length > MaximumBackupBytes || !bytes.AsSpan(0, 8).SequenceEqual(Magic))
        {
            throw new ArgumentException("Unsupported or invalid Mulse backup file.");
        }
        byte[] plaintext = [];
        var key = DeriveKey(request.Password, bytes.AsSpan(8, 16));
        try
        {
            plaintext = new Mulse.Modules.Security.AesGcmRuntimeStatePayloadProtector(key).Unprotect(bytes[24..]);
            var document = JsonSerializer.Deserialize<WorkspaceBackupDocument>(plaintext, JsonOptions)
                ?? throw new ArgumentException("The backup contains no workspace.");
            ValidateDocument(document);
            return document;
        }
        catch (CryptographicException)
        {
            throw new ArgumentException("The backup password is incorrect or the file has been modified.");
        }
        catch (JsonException)
        {
            throw new ArgumentException("The backup contains invalid workspace data.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void ValidateDocument(WorkspaceBackupDocument document)
    {
        if (document.Version != 1 || document.Flows is null || document.Packages is null || document.ConfigValues is null)
        {
            throw new ArgumentException("The backup has an unsupported version or missing workspace sections.");
        }
        if (document.Flows.Any(flow => flow is null || string.IsNullOrWhiteSpace(flow.Id) || flow.Trigger is null || flow.Fetches is null || flow.Parses is null || flow.Augments is null || flow.Deliveries is null)
            || document.Packages.Any(package => package is null || string.IsNullOrWhiteSpace(package.Id) || string.IsNullOrWhiteSpace(package.AssemblyPath))
            || document.ConfigValues.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("The backup contains invalid flow, package, or configuration entries.");
        }
        if (document.Flows.Select(flow => flow.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Flows.Count
            || document.Packages.Select(package => package.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Packages.Count
            || document.ConfigValues.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.ConfigValues.Count)
        {
            throw new ArgumentException("The backup contains duplicate IDs or configuration keys.");
        }
        foreach (var flow in document.Flows)
        {
            if (flow.Fetches.Count == 0 || flow.Parses.Count == 0 || flow.Deliveries.Count == 0
                || flow.Fetches.Any(node => node is null || node.InputParseIds is null || node.InputParseIds.Any(string.IsNullOrWhiteSpace) || !IsValidStep(node.Fetch))
                || flow.Parses.Any(node => node is null || node.FetchNodeIds is null || node.FetchNodeIds.Any(string.IsNullOrWhiteSpace) || !IsValidStep(node.Parse))
                || flow.Augments.Any(step => !IsValidStep(step))
                || flow.Deliveries.Any(route => route is null || !IsValidStep(route.Render) || route.Delivers is null || route.Delivers.Count == 0
                    || route.Delivers.Any(action => action is null || !IsValidStep(action.Deliver) || (action.Compensation is not null && !IsValidStep(action.Compensation)))))
            {
                throw new ArgumentException($"Backup flow '{flow.Id}' contains invalid nodes, steps, or settings.");
            }
            _ = FlowRuntime.OrderSourceGraph(flow, message => new ArgumentException(message));
        }
        var builtIns = catalog.GetPackages().Where(package => package.SourceKind == RuntimePackageSourceKind.BuiltIn).Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (document.Packages.Any(package => builtIns.Contains(package.Id)))
        {
            throw new ArgumentException("A backup cannot replace built-in package registrations.");
        }
    }

    private static bool IsValidStep(ModuleStepDefinition? step)
        => step is not null && !string.IsNullOrWhiteSpace(step.Module) && step.Settings is not null
            && step.Settings.All(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null);

    private WorkspaceRestorePreview CreatePreview(WorkspaceBackupDocument document, RuntimeMulseState state, string mode)
    {
        var conflicts = new List<string>();
        if (mode == "merge")
        {
            conflicts.AddRange(document.Flows.Where(flow => state.Pipelines.Any(existing => string.Equals(existing.Id, flow.Id, StringComparison.OrdinalIgnoreCase))).Select(flow => $"Flow: {flow.Id}"));
            conflicts.AddRange(document.Packages.Where(package => state.ManagedPackages.Any(existing => string.Equals(existing.Id, package.Id, StringComparison.OrdinalIgnoreCase))).Select(package => $"Package: {package.Id}"));
            conflicts.AddRange(document.ConfigValues.Keys.Where(state.ConfigValues.ContainsKey).Select(key => $"Configuration: {key}"));
        }
        var disabledPackageIds = document.Packages.Select(package => package.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = catalog.GetAll().Where(entry => !disabledPackageIds.Contains(entry.PackageId)
            && (mode != "replace" || entry.PackageSourceKind != RuntimePackageSourceKind.Managed)).ToArray();
        var configValues = mode == "replace" ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(state.ConfigValues, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in document.ConfigValues) configValues[pair.Key] = pair.Value;
        var dependencyWarnings = document.Flows.SelectMany(flow => FlowModuleDependencies.FindMissing(flow, available, configValues)
            .Select(module => $"Flow '{flow.Id}' requires unknown or unloaded module {module}. It will remain disabled; load the required package or correct the reference before enabling or running it.")).ToArray();
        return new WorkspaceRestorePreview(document.Flows.Count, document.Packages.Count, document.ConfigValues.Count, conflicts,
        [
            .. dependencyWarnings,
            "Restored flows and package registrations start disabled. Load trusted packages and review flows before enabling them.",
            "Package DLLs, discovery directories, pending retries, and execution checkpoints are not included. Assembly paths refer to the API server.",
            "Destination discovery directories are retained. Unrelated auto-discovered packages may remain loaded. Package synchronization can take five seconds.",
            mode == "replace" ? "Replace removes current flows, config values, managed package registrations, retries, and checkpoints. Existing runs are not cancelled; stop running workloads first." : "Merge rejects any conflicting flow ID, package ID, or configuration key; existing entries and execution state are retained."
        ]);
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt)
    {
        var passwordBytes = System.Text.Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, 600_000, HashAlgorithmName.SHA256, 32); }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
        {
            throw new ArgumentException("Use a backup password with at least 12 characters.");
        }
    }
}
