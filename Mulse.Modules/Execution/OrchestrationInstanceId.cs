namespace Mulse.Modules.Execution;

internal static class OrchestrationInstanceId
{
    public static string Create()
    {
#if NET9_0_OR_GREATER
        return Guid.CreateVersion7().ToString();
#else
        // Instance IDs are opaque; .NET 8 has no UUIDv7 API. The .NET 11 host keeps UUIDv7.
        return Guid.NewGuid().ToString();
#endif
    }
}
