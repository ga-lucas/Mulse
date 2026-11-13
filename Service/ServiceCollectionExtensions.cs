using Mulse.Modules;

namespace Service;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMulsePlatform(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        services.AddHttpClient();
        services.AddSingleton(TimeProvider.System);
        services.Configure<MulseOptions>(configuration.GetSection("Mulse"));
        services.AddOptions<FlowHistoryOptions>().Bind(configuration.GetSection("Persistence:History"))
            .Validate(options => options.RetentionDays is >= 1 and <= 36500, "History RetentionDays must be between 1 and 36500.")
            .Validate(options => options.MaxPendingMessages is >= 1 and <= 1000000, "History MaxPendingMessages must be between 1 and 1000000.").ValidateOnStart();
        services.AddWorkspacePersistence(configuration, contentRootPath);
        services.AddSingleton<IRuntimeConfigurationStore, RuntimeConfigurationStore>();
        services.AddSingleton<WorkspaceBackupService>();
        services.AddSingleton<StorageMigrationService>();
        services.AddSingleton<IModuleCatalog, ModuleCatalog>();
        services.AddSingleton<IModuleInvoker, ModuleInvoker>();
        services.AddSingleton<Mulse.Modules.Abstractions.IAugmentModuleInvoker, AugmentModuleInvoker>();
        services.AddSingleton<Mulse.Modules.Abstractions.IRenderModuleInvoker, RenderModuleInvoker>();
        services.AddSingleton<Mulse.Modules.Abstractions.IDeliverModuleInvoker, DeliverModuleInvoker>();
        services.AddSingleton<IFlowDefinitionService, FlowDefinitionService>();
        services.AddSingleton<IFlowDesignService, FlowDesignService>();
        services.AddSingleton<IBizTalkImportService, BizTalkImportService>();
        services.AddSingleton<IFlowOrchestrationStateStore, FlowOrchestrationStateStore>();
        services.AddSingleton<IFlowRetryStateStore, FlowRetryStateStore>();
        services.AddSingleton<IConfigValueResolver, ConfigValueResolver>();
        services.AddSingleton<IHttpInboundRegistry, HttpInboundRegistry>();
        services.AddOptions<ProcessingOptions>().Bind(configuration.GetSection("Processing"))
            .Validate(options => options.ShutdownDrainSeconds is >= 1 and <= 3600 && options.MaxConcurrentExecutions is >= 1 and <= 10000,
                "Processing ShutdownDrainSeconds must be 1-3600 and MaxConcurrentExecutions 1-10000.").ValidateOnStart();
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(
            configuration.GetValue("Processing:ShutdownDrainSeconds", 90) + 30));
        services.AddSingleton<ProcessingCoordinator>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<ProcessingCoordinator>());
        services.AddSingleton<FlowRuntime>();
        services.AddSingleton<IFlowRuntime>(serviceProvider => new CoordinatedFlowRuntime(
            serviceProvider.GetRequiredService<FlowRuntime>(), serviceProvider.GetRequiredService<ProcessingCoordinator>()));
        services.AddHostedService<ModuleSynchronizationService>();
        services.AddHostedService<ScheduledFlowService>();
        services.AddHostedService<PushTriggeredFlowService>();
        services.AddHostedService<RetryDrivenFlowService>();
        services.AddHostedService<BatchDeadlineFlowService>();

        return services;
    }
}
