using Mulse.Modules;

namespace Service.Modules;

public sealed class ModuleLease<TModule> : IAsyncDisposable where TModule : class, IModule
{
    private readonly Func<ValueTask> _releaseAsync;
    private bool _disposed;

    public ModuleLease(TModule module, ModuleCatalogEntry entry, Func<ValueTask> releaseAsync)
    {
        Module = module;
        Entry = entry;
        _releaseAsync = releaseAsync;
    }

    public TModule Module { get; }

    public ModuleCatalogEntry Entry { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _releaseAsync().ConfigureAwait(false);
    }
}
