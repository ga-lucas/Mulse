# 01-modules-packages: Upgrade core module dependencies

In `Mulse.Modules`, update Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Http, and System.Memory.Data from 10.0.15 to 11.0.0. Assessment API diffs reported no source-breaking changes; confirm behavior through build and tests.

## Research Findings
- `Mulse.Modules.csproj` owns all three references, currently 10.0.15, without centralized package management or local imported targets; it targets net11.0.
- These are foundational references consumed by Service, plugins, and Service.Tests. Assessment recommends 11.0.0 with no detected source-breaking API changes; build the whole solution and run the complete test project to cover downstream consumers.

**Done when**: All three references resolve to 11.0.0, affected projects build without warnings, and relevant tests pass.
