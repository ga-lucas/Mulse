using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Mulse.Persistence.PostgreSql;

public sealed class PostgreSqlMulseDbContext(DbContextOptions<PostgreSqlMulseDbContext> options) : MulseDbContext(options);

public static class PostgreSqlPersistence
{
    public static IServiceCollection AddMulsePostgreSql(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<PostgreSqlMulseDbContext>(options => options.UseNpgsql(connectionString));
        services.AddSingleton<IDbContextFactory<MulseDbContext>, MulseDbContextFactory<PostgreSqlMulseDbContext>>();
        return services;
    }
}

public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<PostgreSqlMulseDbContext>
{
    public PostgreSqlMulseDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<PostgreSqlMulseDbContext>().UseNpgsql(
            Environment.GetEnvironmentVariable("MULSE_MIGRATION_CONNECTION")
                ?? "Host=localhost;Database=mulse_design;Username=mulse").Options);
}
