using Mulse.Modules;

namespace Mulse.Dbms.SqlServer;

public sealed class SqlServerModuleInstaller : IModuleInstaller
{
    public void Install(IModuleRegistryBuilder builder)
    {
        builder.AddOrchestrationAugment<SqlServerLookupAugmentModule>();
        builder.AddDeliver<SqlServerExecuteDeliverModule>();
    }
}
