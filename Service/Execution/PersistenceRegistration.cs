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
        if (provider.Equals("Json", StringComparison.OrdinalIgnoreCase)) return;
        var connection = configuration.GetConnectionString("Mulse");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("ConnectionStrings:Mulse is required when a database persistence provider is selected.");
        var sqlite = provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);
        if (sqlite)
        {
            var builder = new SqliteConnectionStringBuilder(connection);
            if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:" || builder.Mode == SqliteOpenMode.Memory)
                throw new InvalidOperationException("SQLite persistence requires a durable local database file, not an in-memory database.");
            builder.DataSource = Path.GetFullPath(builder.DataSource, contentRootPath);
            builder.DefaultTimeout = 30;
            var directory = Path.GetDirectoryName(builder.DataSource)!;
            services.AddMulseSqlite(builder.ToString());
            services.AddSingleton(serviceProvider =>
            {
                Directory.CreateDirectory(directory);
                return CreateDatabase(serviceProvider, configuration, applyMigrations: true);
            });
        }
        else
        {
            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)) services.AddMulseSqlServer(connection);
            else if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase)) services.AddMulsePostgreSql(connection);
            else throw new InvalidOperationException($"Unknown persistence provider '{provider}'. Use Sqlite, SqlServer, PostgreSql or Json.");
            services.AddSingleton(serviceProvider => CreateDatabase(serviceProvider, configuration, applyMigrations: false));
        }
    }

    private static WorkspaceDatabase CreateDatabase(IServiceProvider services, IConfiguration configuration, bool applyMigrations)
        => new(services.GetRequiredService<IDbContextFactory<MulseDbContext>>(),
            new DatabasePayloadCodec(configuration["Persistence:Encryption:KeyId"] ?? "primary",
                configuration["Mulse:Encryption:KeyEnvironmentVariable"] ?? "MULSE_STATE_ENCRYPTION_KEY"),
            configuration.GetValue("Persistence:ApplyMigrations", applyMigrations));
}
