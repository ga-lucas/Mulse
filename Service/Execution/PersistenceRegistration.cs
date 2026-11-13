using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mulse.Persistence;
using Mulse.Persistence.PostgreSql;
using Mulse.Persistence.Sqlite;
using Mulse.Persistence.SqlServer;

namespace Service.Execution;

internal static class PersistenceRegistration
{
    public static void AddWorkspacePersistence(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        // Unconfigured library consumers retain the legacy store; the shipped application explicitly selects SQLite.
        var provider = configuration["Persistence:Provider"] ?? "Json";
        var keyEnvironmentVariable = configuration["Mulse:Encryption:KeyEnvironmentVariable"] ?? "MULSE_STATE_ENCRYPTION_KEY";
        if (provider.Equals("Json", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(new RuntimeStorageSettings("Json", false,
                Path.GetFullPath(configuration["Mulse:RuntimeStatePath"] ?? new Mulse.Modules.MulseOptions().RuntimeStatePath, contentRootPath),
                false, null, keyEnvironmentVariable,
                ShutdownDrainSeconds: configuration.GetValue("Processing:ShutdownDrainSeconds", 90),
                MaxConcurrentExecutions: configuration.GetValue("Processing:MaxConcurrentExecutions", 64)));
            services.AddSingleton<IWorkspaceOwnershipGuard>(_ => new WorkspaceOwnership(Path.GetFullPath(
                configuration["Mulse:RuntimeStatePath"] ?? new Mulse.Modules.MulseOptions().RuntimeStatePath, contentRootPath)));
            services.AddHostedService<Service.Triggers.WorkspaceOwnershipService>();
            return;
        }
        var connection = configuration.GetConnectionString("Mulse");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("ConnectionStrings:Mulse is required when a database persistence provider is selected.");
        var sqlite = provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);
        var settings = new RuntimeStorageSettings(
            sqlite ? "Sqlite" : provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) ? "SqlServer" : "PostgreSql",
            true, null, configuration.GetValue("Persistence:ApplyMigrations", sqlite),
            configuration["Persistence:Encryption:KeyId"] ?? "primary", keyEnvironmentVariable,
            configuration.GetValue("Persistence:History:CaptureStagePayloads", false), configuration.GetValue("Persistence:History:RetentionDays", 30),
            configuration.GetValue("Processing:ShutdownDrainSeconds", 90), configuration.GetValue("Processing:MaxConcurrentExecutions", 64),
            configuration.GetValue("Persistence:History:MaxPendingMessages", 10000));
        if (sqlite)
        {
            var builder = new SqliteConnectionStringBuilder(connection);
            if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:" || builder.Mode == SqliteOpenMode.Memory)
                throw new InvalidOperationException("SQLite persistence requires a durable local database file, not an in-memory database.");
            builder.DataSource = Path.GetFullPath(builder.DataSource, contentRootPath);
            builder.DefaultTimeout = 30;
            var directory = Path.GetDirectoryName(builder.DataSource)!;
            settings = settings with { DataFile = builder.DataSource };
            services.AddMulseSqlite(builder.ToString());
            services.AddSingleton<IWorkspaceOwnershipGuard>(_ => new WorkspaceOwnership(builder.DataSource));
            services.AddSingleton(serviceProvider =>
            {
                Directory.CreateDirectory(directory);
                return CreateDatabase(serviceProvider, settings);
            });
        }
        else
        {
            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)) services.AddMulseSqlServer(connection);
            else if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase)) services.AddMulsePostgreSql(connection);
            else throw new InvalidOperationException($"Unknown persistence provider '{provider}'. Use Sqlite, SqlServer, PostgreSql or Json.");
            services.AddSingleton<IWorkspaceOwnershipGuard>(_ => new WorkspaceOwnership(connection, provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)));
            services.AddSingleton(serviceProvider => CreateDatabase(serviceProvider, settings));
        }
        services.AddHostedService<Service.Triggers.WorkspaceOwnershipService>();
        services.AddSingleton(settings);
        services.AddSingleton<FlowJournal>();
        services.AddSingleton<Mulse.Modules.Execution.IDurableInbox>(serviceProvider => serviceProvider.GetRequiredService<FlowJournal>());
        services.AddHostedService<Service.Triggers.InboxFlowService>();
    }

    private static WorkspaceDatabase CreateDatabase(IServiceProvider services, RuntimeStorageSettings settings)
        => new(services.GetRequiredService<IDbContextFactory<MulseDbContext>>(),
            new DatabasePayloadCodec(settings.EncryptionKeyId!, settings.KeyEnvironmentVariable),
            settings.ApplyMigrations, services.GetRequiredService<IWorkspaceOwnershipGuard>());
}
