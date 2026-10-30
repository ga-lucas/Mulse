using Microsoft.Extensions.DependencyInjection;

namespace Service.Tests;

internal static class ApiTestValidation
{
    public static void Register(IServiceCollection services)
    {
        // Multiple AddValidation call sites make the .NET 10 generator emit duplicate source names.
        services.AddValidation();
    }
}
