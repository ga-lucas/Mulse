using Mulse.Modules.Abstractions;

namespace Mulse.Excel;

public sealed class ExcelModuleInstaller : IModuleInstaller
{
    public void Install(IModuleRegistryBuilder builder)
    {
        builder.AddParse<ExcelParseModule>();
        builder.AddRender<ExcelRenderModule>();
    }
}
