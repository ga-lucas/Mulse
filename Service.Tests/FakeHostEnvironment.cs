using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Service.Tests;

/// <summary>Minimal <see cref="IHostEnvironment"/> double used only so <see cref="RuntimeConfigurationStore"/>
/// can be constructed in tests; tests always pass an already-rooted <c>RuntimeStatePath</c>, so
/// <see cref="ContentRootPath"/> is never actually consulted for path resolution.</summary>
internal sealed class FakeHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";

    public string ApplicationName { get; set; } = "Service.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
