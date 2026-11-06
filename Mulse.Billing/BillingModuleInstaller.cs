using Mulse.Modules.Abstractions;

namespace Mulse.Billing;

public sealed class BillingModuleInstaller : IModuleInstaller
{
    public void Install(IModuleRegistryBuilder builder)
    {
        builder.AddOrchestrationAugment<BillingFieldAugmentModule>();
    }
}
