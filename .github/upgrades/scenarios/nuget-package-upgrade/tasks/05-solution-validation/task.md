# 05-solution-validation: Validate all package changes

Build the entire solution, run all discovered tests, and review the resulting package graph and remaining updates. Report deferred packages or unresolved risk explicitly.

## Research Findings
- `Mulse.slnx` contains the upgraded projects and `Service.Tests` is the identified test project; the previous task ran 2,100 tests successfully. Test Explorer did not return results using its project filter, so validate via the solution's CLI test runner.
- The current `dotnet list Mulse.slnx package --outdated` reports only NPOI 2.7.5 and ImageSharp 3.2.0, which the user chose to retain. The transitive vulnerability scan reports no known vulnerable packages from the configured sources.
- Final verification will rebuild the solution and execute the full test suite; external SQL Server, telemetry collector, and ARM64 runtime behavior are not covered by that suite.

**Done when**: Full solution builds without warnings or errors, all tests pass, and remaining package constraints are recorded.
