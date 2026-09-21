using Mulse.Modules;

namespace Service;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMulsePlatform(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        services.AddSingleton(TimeProvider.System);
        services.Configure<MulseOptions>(configuration.GetSection("Mulse"));
        services.AddSingleton<IRuntimeConfigurationStore, RuntimeConfigurationStore>();
        services.AddSingleton<IModuleCatalog, ModuleCatalog>();
        services.AddSingleton<IFlowDefinitionService, FlowDefinitionService>();
        services.AddSingleton<IFlowDesignService, FlowDesignService>();
        services.AddSingleton<IFlowRuntime, FlowRuntime>();
        services.AddHostedService<ModuleSynchronizationService>();
        services.AddHostedService<ScheduledFlowService>();

        return services;
    }
}
