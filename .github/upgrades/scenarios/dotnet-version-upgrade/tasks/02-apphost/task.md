# 02-apphost: Upgrade Mulse.AppHost

Retarget `Mulse.AppHost` to `net11.0` and update its Aspire packages, including `Aspire.Hosting.AppHost` from 13.6.4 to the assessment-recommended 13.6.5. The assessment flags this package for a security vulnerability. Keep its references to `Mulse.Ui` and `Service` working while those projects still target `net10.0`.

**Done when**: `Mulse.AppHost` builds for `net11.0`, its package vulnerability finding is addressed, and its project references resolve.

## Scope Inventory
- **Projects affected**: `Mulse.AppHost\Mulse.AppHost.csproj` only.
- **Distinct concerns**: Replace its TFM with `net11.0`; update the Aspire AppHost SDK/package; preserve project references to `Mulse.Ui` and `Service`.
- **Assessment signals**: SDK-style `DotNetCoreApp`, current `net10.0`, 1 mandatory TFM finding, 1 recommended package upgrade, 1 optional security finding, 0 API issues, no test-coverage recommendation, and low difficulty.
- **Dependencies**: `Mulse.AppHost` references `Mulse.Ui` and `Service`; both remain on `net10.0` until their planned tasks.
- **Package management**: No Central Package Management or imported package source was reported. `Aspire.Hosting.AppHost` 13.6.4 is supplied through the project SDK declaration `Aspire.AppHost.Sdk/13.6.4`; update that declaration to 13.6.5 rather than adding a duplicate package reference.
- **Files to modify**: `Mulse.AppHost\Mulse.AppHost.csproj`.
- **Stub scan**: No `// STUB:` markers found in the project's C# files.
- **Decomposition**: Atomic single-project TFM/package update; no code API migration or test project is in scope.

## Research
- `TargetFramework` is explicitly declared as `net10.0` in the project file; dependency discovery found no imported `Directory.Build.props` or targets defining it.
- The assessment recommends `Aspire.Hosting.AppHost` 13.6.5 for both the package upgrade and the security finding. `Mulse.AppHost` has no source API compatibility findings.
- A restore/build of the AppHost project and then the full solution will verify package resolution and references to the still-`net10.0` applications.
