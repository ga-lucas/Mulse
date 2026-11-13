namespace Mulse.Persistence;

public interface IWorkspaceOwnershipGuard : IDisposable
{
    void EnsureAvailable();
}
