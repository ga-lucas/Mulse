# 07-validation: Validate the full solution

Build the complete solution and run all discovered tests after consolidation. Verify that every project in the solution targets `net11.0`, that the reported package upgrades and incompatible package resolution are complete, and that no dependency conflicts or unintended behavioral regressions remain.

## Research Findings
- Confirmed the solution contains 12 projects, all now declare only `net11.0`; there are no remaining `net10.0`, `TargetFrameworks`, or `NET10_0` entries in project/props/targets/C# source.
- `discover_test_projects` found one test project: `Service.Tests\Service.Tests.csproj`. Its full suite passed 2,100 tests after library consolidation; rerun as the final gate.
- `dotnet list Mulse.slnx package` resolved all direct package references without version conflicts. Key upgrades are Aspire.Hosting.AppHost 13.6.5, Microsoft.AspNetCore.OpenApi 11.0.0, Microsoft.Extensions.Http.Resilience/ServiceDiscovery 11.0.0, and System.Security.Cryptography.Pkcs/Xml 10.0.15. The incompatible Visual Studio container-tools package is absent.
- The previous full solution build after consolidation passed with zero warnings and errors. This task will repeat the full build and test suite; no source or project edits are currently planned.

**Done when**: The full solution builds without errors, all tests pass, every project targets `net11.0`, and package dependency checks show no unresolved incompatibilities.
