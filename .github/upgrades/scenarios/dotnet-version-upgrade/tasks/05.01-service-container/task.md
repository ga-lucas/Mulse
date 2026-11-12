# 05.01-service-container: Retarget Service and replace container tooling

## Objective
Retarget `Service` to `net11.0`, apply its direct package recommendations, and replace the unsupported Visual Studio container-tools package with SDK container publishing while preserving the Windows container path.

## Scope
- `Service/Service.csproj`, `Service/Dockerfile`, and `Service/Properties/launchSettings.json`; add/update only directly relevant container instructions if needed.
- Remove `Microsoft.VisualStudio.Azure.Containers.Tools.Targets`, update `Microsoft.AspNetCore.OpenApi` from `10.0.14` to the `net11.0`-supported `11.0.0`, and configure `PublishContainer` for a supported Windows .NET 11 base image.
- Keep `Service.Tests` buildable; it currently targets `net11.0` and receives the unsupported package only transitively.

## Steps
1. Confirm the TFM property is project-local, then retarget Service and update/remove the assessed direct package references.
2. Replace the unsupported package-dependent Docker launch profile with a usable SDK container-publishing workflow; preserve the Dockerfile for manual builds and update its .NET images to 11.0. Verify whether its custom VS Build Tools stage is needed before changing it (the project has no `PublishAot` setting).
3. Fix compile blockers and all warnings in Service needed for a clean net11 build, including the five previously observed warnings in `BizTalkNativeMapConverter.cs`; do not edit generated `obj` files.
4. Build Service and Service.Tests, confirm the incompatible package is absent from the resolved test dependency graph, and publish an SDK container archive without requiring a local container daemon.

## Research Findings
- `Service.csproj` is SDK-style (`Microsoft.NET.Sdk.Web`), and `get_project_dependencies` confirms it defines its own single `TargetFramework` and both package versions locally; no imported targets or central package management were reported. Replace the singular framework value in place.
- Direct package actions are `Microsoft.AspNetCore.OpenApi` 10.0.14 -> 11.0.0 (the assessment's 10.0.15 suggestion was based on the old TFM; the supported-version resolver for `net11.0` returns 11.0.0) and removal of the unsupported `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` 1.24.2-preview.1. `Service.Tests` already targets `net11.0`, references Service, and has no direct reference to the container-tools package; removing it from Service should remove the transitive dependency.
- `Service/Dockerfile` uses Windows Server Core/Nano Server .NET 10 images. The project is an ASP.NET Core web app. Its build stage installs Visual Studio C++ Build Tools, described as needed for AOT, but no `PublishAot` setting exists in the Service project or source. The Microsoft Container Registry exposes .NET 11 Windows images for LTSC 2025, not LTSC 2022, so the upgraded Windows image requires the 2025 tags. The redundant AOT toolchain can be removed if the Dockerfile build succeeds without it.
- `Service/Properties/launchSettings.json` has a `Container (Dockerfile)` profile with `commandName: Docker`; it depends on Visual Studio container tooling and cannot remain as a broken profile after removing that package.
- Official [SDK container publishing guidance](https://learn.microsoft.com/en-us/dotnet/core/containers/sdk-publish) supports .NET 8+ and the `PublishContainer` target without Dockerfiles. [Container publish configuration](https://learn.microsoft.com/en-us/dotnet/core/containers/publish-configuration) documents `ContainerBaseImage` and `ContainerRuntimeIdentifier`; Windows images require an explicit fully qualified base-image tag. `ContainerArchiveOutputPath` allows validating a publish without a local daemon.
- The current `net10.0` Service build reports five existing warnings in `BizTalkNativeMapConverter.cs` (one CS8619 and four CS1573). This project must be warning-free at completion. Existing task research also confirms the `Configure<MulseOptions>` overload exists; verify the net11 build before touching it.

## Done when
Service and Service.Tests build successfully; Service is warning-free on `net11.0`; the unsupported package is removed; an SDK-published Windows container archive is produced using a supported .NET 11 base; and the manual Dockerfile path targets .NET 11 without leaving a broken launch profile.
