using Microsoft.Extensions.DependencyInjection;

namespace Service.Modules;

// Host DI caches closed generic service types, including ILogger<PluginType>, for its lifetime.
// Resolve plugin-owned loggers without putting collectible types into those caches.
internal sealed class PluginServiceProvider(IServiceProvider host) : IServiceProvider, IServiceProviderIsService,
    IKeyedServiceProvider, IServiceProviderIsKeyedService
{
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceProviderIsService)
            || serviceType == typeof(IKeyedServiceProvider) || serviceType == typeof(IServiceProviderIsKeyedService)) return this;
        if (!serviceType.IsCollectible) return host.GetService(serviceType);
        if (IsPluginLogger(serviceType))
            return Activator.CreateInstance(typeof(Logger<>).MakeGenericType(serviceType.GenericTypeArguments), host.GetRequiredService<ILoggerFactory>());
        throw new InvalidOperationException($"Plugin-owned service '{serviceType}' cannot be resolved from host DI. Use shared SDK services or construct plugin-local dependencies inside the module.");
    }

    public bool IsService(Type serviceType)
    {
        if (serviceType.IsCollectible) return IsPluginLogger(serviceType) && host.GetService<ILoggerFactory>() is not null;
        if (serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceProviderIsService)
            || serviceType == typeof(IKeyedServiceProvider) || serviceType == typeof(IServiceProviderIsKeyedService)) return true;
        return host.GetRequiredService<IServiceProviderIsService>().IsService(serviceType);
    }

    public object? GetKeyedService(Type serviceType, object? serviceKey)
    {
        EnsureSharedKey(serviceKey);
        if (serviceType.IsCollectible) throw new InvalidOperationException("Keyed plugin-owned services cannot be resolved from host DI.");
        return host.GetKeyedService(serviceType, serviceKey);
    }

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => GetKeyedService(serviceType, serviceKey)
        ?? throw new InvalidOperationException($"No keyed service '{serviceType}' is registered for the plugin.");

    public bool IsKeyedService(Type serviceType, object? serviceKey)
    {
        EnsureSharedKey(serviceKey);
        return !serviceType.IsCollectible && host.GetRequiredService<IServiceProviderIsKeyedService>().IsKeyedService(serviceType, serviceKey);
    }

    private static bool IsPluginLogger(Type type) => type.IsConstructedGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>);
    private static void EnsureSharedKey(object? key)
    {
        if (key is not null && (key.GetType().IsCollectible || key is Type { IsCollectible: true }))
            throw new InvalidOperationException("Plugin-owned service keys cannot be cached in host DI.");
    }
}
