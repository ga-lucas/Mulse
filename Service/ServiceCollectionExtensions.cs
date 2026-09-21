using Mulse.Modules;

namespace Service;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMulsePlatform(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        services.AddHttpClient();
        services.AddSingleton(TimeProvider.System);
        services.Configure<MulseOptions>(configuration.GetSection("Mulse"));
        services.AddSingleton<IRuntimeConfigurationStore, RuntimeConfigurationStore>();
        services.AddSingleton<IModuleCatalog, ModuleCatalog>();
        services.AddSingleton<IFlowDefinitionService, FlowDefinitionService>();
        services.AddSingleton<IFlowDesignService, FlowDesignService>();
        services.AddSingleton<IBizTalkImportService, BizTalkImportService>();
        services.AddSingleton<IFlowOrchestrationStateStore, FlowOrchestrationStateStore>();
        services.AddSingleton<IFlowRuntime, FlowRuntime>();
        services.AddHostedService<ModuleSynchronizationService>();
        services.AddHostedService<ScheduledFlowService>();
        services.AddHostedService<PushTriggeredFlowService>();

        return services;
    }
}
