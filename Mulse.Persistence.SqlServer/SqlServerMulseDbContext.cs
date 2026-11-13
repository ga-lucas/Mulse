using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace Mulse.Persistence.SqlServer;

public sealed class SqlServerMulseDbContext(DbContextOptions<SqlServerMulseDbContext> options) : MulseDbContext(options);

public static class SqlServerPersistence
{
    public static IServiceCollection AddMulseSqlServer(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<SqlServerMulseDbContext>(options => options.UseSqlServer(connectionString));
        services.AddSingleton<IDbContextFactory<MulseDbContext>, MulseDbContextFactory<SqlServerMulseDbContext>>();
        return services;
    }
}

public sealed class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<SqlServerMulseDbContext>
{
    public SqlServerMulseDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<SqlServerMulseDbContext>().UseSqlServer(
            Environment.GetEnvironmentVariable("MULSE_MIGRATION_CONNECTION")
                ?? "Server=(localdb)\\MSSQLLocalDB;Database=MulseDesign;Integrated Security=true;TrustServerCertificate=true").Options);
}
