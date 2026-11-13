using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Mulse.Persistence.Sqlite;

public sealed class SqliteMulseDbContext(DbContextOptions<SqliteMulseDbContext> options) : MulseDbContext(options);

public static class SqlitePersistence
{
    public static IServiceCollection AddMulseSqlite(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<SqliteMulseDbContext>(options => Configure(options, connectionString));
        services.AddSingleton<IDbContextFactory<MulseDbContext>, MulseDbContextFactory<SqliteMulseDbContext>>();
        return services;
    }

    public static void Configure(DbContextOptionsBuilder options, string connectionString)
        => options.UseSqlite(connectionString).AddInterceptors(new DurabilityInterceptor());

    private sealed class DurabilityInterceptor : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
        }

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteMulseDbContext>
{
    public SqliteMulseDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SqliteMulseDbContext>();
        SqlitePersistence.Configure(options, Environment.GetEnvironmentVariable("MULSE_MIGRATION_CONNECTION") ?? "Data Source=mulse-design.db");
        return new(options.Options);
    }
}
