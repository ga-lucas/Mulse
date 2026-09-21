using Microsoft.Extensions.Options;
using Mulse.Modules;

namespace Service;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMulsePlatform(this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        services.AddHttpClient();
        services.AddSingleton(TimeProvider.System);
        services.Configure<MulseOptions>(configuration.GetSection("Mulse"));
        services.AddSingleton<IRuntimeStatePayloadProtector>(CreateRuntimeStatePayloadProtector);
        services.AddSingleton<IRuntimeConfigurationStore, RuntimeConfigurationStore>();
        services.AddSingleton<IModuleCatalog, ModuleCatalog>();
        services.AddSingleton<IFlowDefinitionService, FlowDefinitionService>();
        services.AddSingleton<IFlowDesignService, FlowDesignService>();
        services.AddSingleton<IBizTalkImportService, BizTalkImportService>();
        services.AddSingleton<IFlowOrchestrationStateStore, FlowOrchestrationStateStore>();
        services.AddSingleton<IFlowRetryStateStore, FlowRetryStateStore>();
        services.AddSingleton<IConfigValueResolver, ConfigValueResolver>();
        services.AddSingleton<IHttpInboundRegistry, HttpInboundRegistry>();
        services.AddSingleton<IFlowRuntime, FlowRuntime>();
        services.AddHostedService<ModuleSynchronizationService>();
        services.AddHostedService<ScheduledFlowService>();
        services.AddHostedService<PushTriggeredFlowService>();
        services.AddHostedService<RetryDrivenFlowService>();

        return services;
    }

    private static IRuntimeStatePayloadProtector CreateRuntimeStatePayloadProtector(IServiceProvider serviceProvider)
    {
        var encryption = serviceProvider.GetRequiredService<IOptions<MulseOptions>>().Value.Encryption;
        if (!encryption.Enabled)
        {
            return NoOpRuntimeStatePayloadProtector.Instance;
        }

        var keyBase64 = !string.IsNullOrWhiteSpace(encryption.KeyBase64)
            ? encryption.KeyBase64
            : Environment.GetEnvironmentVariable(encryption.KeyEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            throw new InvalidOperationException(
                $"Runtime state encryption is enabled (Mulse:Encryption:Enabled) but no key was found. Set " +
                $"'Mulse:Encryption:KeyBase64' in configuration or the '{encryption.KeyEnvironmentVariable}' " +
                "environment variable to a base64-encoded 128/192/256-bit AES key.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(keyBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The configured runtime state encryption key is not valid base64.", exception);
        }

        return new AesGcmRuntimeStatePayloadProtector(key);
    }
}
