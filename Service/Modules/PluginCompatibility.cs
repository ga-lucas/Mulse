using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Versioning;

namespace Service.Modules;

internal static class PluginCompatibility
{
    public static Version HostVersion { get; } = new FrameworkName(
        typeof(ModuleCatalog).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName
        ?? throw new InvalidOperationException("The Service must declare its target framework.")).Version;

    public static void EnsureSupported(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) throw new InvalidOperationException("Module packages must be managed .NET assemblies.");
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (metadata.GetString(type.Namespace) != "System.Runtime.Versioning" || metadata.GetString(type.Name) != nameof(TargetFrameworkAttribute)) continue;
            var value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1) throw new BadImageFormatException("Invalid module target framework metadata.");
            var name = value.ReadSerializedString();
            if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Module package has no target framework identity.");
            var target = new FrameworkName(name);
            if (target.Identifier != ".NETCoreApp" || target.Version < new Version(8, 0))
                throw new InvalidOperationException($"Module target '{target.FullName}' is unsupported. Author the plugin for .NET 8 or newer; .NET Framework is not supported.");
            if (target.Version > HostVersion)
                throw new InvalidOperationException($"Module requires .NET {target.Version}, but the Service targets .NET {HostVersion}. Upgrade the Service or rebuild the module for a supported target.");
            return;
        }
        throw new InvalidOperationException("Module packages must declare a .NET 8 or newer target framework supported by this host.");
    }
}
