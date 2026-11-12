# Projects and dependencies analysis

This document provides a comprehensive overview of the projects and their dependencies in the context of upgrading to .NETCoreApp,Version=v11.0.

Detailed findings live alongside this file in `assessment/`. This page is the index: read it first, then open only the documents you need.

## Table of Contents

- [Executive Summary](#executive-summary)
  - [Highlevel Metrics](#highlevel-metrics)
  - [Projects Compatibility](#projects-compatibility)
  - [Package Compatibility](#package-compatibility)
  - [API Compatibility](#api-compatibility)
- [Top API Migration Challenges](#top-api-migration-challenges)
  - [Technologies and Features](#technologies-and-features)
  - [Most Frequent API Issues](#most-frequent-api-issues)
- [Detailed Reports](#detailed-reports)
  - [Projects Relationship Graph](assessment/project-graph.md)
  - [Aggregate NuGet packages details](assessment/nuget/aggregate-packages.md)
  - [Most Frequent API Issues (complete list)](assessment/api-issues/most-frequent-api-issues.md)
  - [Project Details](#project-details)

## Executive Summary

### Highlevel Metrics

| Metric | Count | Status |
| :--- | :---: | :--- |
| Total Projects | 12 | All require upgrade |
| Total NuGet Packages | 168 | 14 need upgrade |
| Total Code Files | 646 |  |
| Total Code Files with Incidents | 213 |  |
| Total Lines of Code | 67224 |  |
| Total Number of Issues | 1086 |  |
| Proposed Target Framework | net11.0 |  |
| Estimated LOC to modify | 1059+ | at least 1.6% of codebase |

### Projects Compatibility

| Project | Target Framework | Difficulty | Package Issues | API Issues | Binding Issues | Est. LOC Impact | Description |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| [Mulse.AppHost\Mulse.AppHost.csproj](assessment/projects/Mulse.AppHost.md) | net10.0 | 🟢 Low | 2 | 0 | 0 |  | DotNetCoreApp, Sdk Style = True |
| [Mulse.Billing\Mulse.Billing.csproj](assessment/projects/Mulse.Billing.md) | net10.0 | 🟢 Low | 0 | 0 | 0 |  | ClassLibrary, Sdk Style = True |
| [Mulse.CompatibilityPack\Mulse.CompatibilityPack.csproj](assessment/projects/Mulse.CompatibilityPack.md) | net10.0 | 🟢 Low | 0 | 27 | 0 | 27+ | ClassLibrary, Sdk Style = True |
| [Mulse.Dbms.SqlServer\Mulse.Dbms.SqlServer.csproj](assessment/projects/Mulse.Dbms.SqlServer.md) | net10.0 | 🟢 Low | 1 | 4 | 0 | 4+ | ClassLibrary, Sdk Style = True |
| [Mulse.Excel\Mulse.Excel.csproj](assessment/projects/Mulse.Excel.md) | net10.0 | 🟢 Low | 1 | 9 | 0 | 9+ | ClassLibrary, Sdk Style = True |
| [Mulse.Hl7\Mulse.Hl7.csproj](assessment/projects/Mulse.Hl7.md) | net10.0 | 🟢 Low | 0 | 14 | 0 | 14+ | ClassLibrary, Sdk Style = True |
| [Mulse.Modules\Mulse.Modules.csproj](assessment/projects/Mulse.Modules.md) | net10.0 | 🟢 Low | 4 | 165 | 0 | 165+ | ClassLibrary, Sdk Style = True |
| [Mulse.SamplePlugin\Mulse.SamplePlugin.csproj](assessment/projects/Mulse.SamplePlugin.md) | net10.0 | 🟢 Low | 0 | 10 | 0 | 10+ | ClassLibrary, Sdk Style = True |
| [Mulse.ServiceDefaults\Mulse.ServiceDefaults.csproj](assessment/projects/Mulse.ServiceDefaults.md) | net10.0 | 🟢 Low | 4 | 0 | 0 |  | ClassLibrary, Sdk Style = True |
| [Mulse.Ui\Mulse.Ui.csproj](assessment/projects/Mulse.Ui.md) | net10.0 | 🟢 Low | 0 | 48 | 0 | 48+ | AspNetCore, Sdk Style = True |
| [Service.Tests\Service.Tests.csproj](assessment/projects/Service.Tests.md) | net10.0 | 🟢 Low | 1 | 528 | 0 | 528+ | DotNetCoreApp, Sdk Style = True |
| [Service\Service.csproj](assessment/projects/Service.md) | net10.0 | 🟢 Low | 2 | 254 | 0 | 254+ | AspNetCore, Sdk Style = True |

### Package Compatibility

| Status | Count | Percentage |
| :--- | :---: | :---: |
| ✅ Compatible | 154 | 91.7% |
| ⚠️ Incompatible | 2 | 1.2% |
| 🔄 Upgrade Recommended | 12 | 7.1% |
| ***Total NuGet Packages*** | ***168*** | ***100%*** |

### API Compatibility

| Category | Count | Impact |
| :--- | :---: | :--- |
| 🔴 Binary Incompatible | 1 | High - Require code changes |
| 🟡 Source Incompatible | 449 | Medium - Needs re-compilation and potential conflicting API error fixing |
| 🔵 Behavioral change | 609 | Low - Behavioral changes that may require testing at runtime |
| ✅ Compatible | 203906 |  |
| ***Total APIs Analyzed*** | ***204965*** |  |

## Top API Migration Challenges

### Technologies and Features

| Technology | Issues | Percentage | Migration Path |
| :--- | :---: | :---: | :--- |

### Most Frequent API Issues

| API | Count | Percentage | Category |
| :--- | :---: | :---: | :--- |
| T:System.BinaryData | 250 | 23.6% | Source Incompatible |
| T:System.NotSupportedException | 238 | 22.5% | Behavioral Change |
| T:System.Uri | 130 | 12.3% | Behavioral Change |
| M:System.BinaryData.FromString(System.String) | 118 | 11.1% | Source Incompatible |
| M:Microsoft.AspNetCore.Http.OpenApiRouteHandlerBuilderExtensions.ProducesProblem(Microsoft.AspNetCore.Builder.RouteHandlerBuilder,System.Int32,System.String) | 80 | 7.6% | Behavioral Change |
| M:System.Uri.#ctor(System.String) | 48 | 4.5% | Behavioral Change |
| T:System.Net.Http.HttpContent | 26 | 2.5% | Behavioral Change |
| M:System.BinaryData.FromBytes(System.Byte[]) | 25 | 2.4% | Source Incompatible |
| T:System.Text.Json.JsonDocument | 19 | 1.8% | Behavioral Change |
| M:System.BinaryData.ToString | 18 | 1.7% | Source Incompatible |

The table above is the top 10. See [the complete list](assessment/api-issues/most-frequent-api-issues.md) for every affected API.

## Detailed Reports

- [Projects Relationship Graph](assessment/project-graph.md)
- [Aggregate NuGet packages details](assessment/nuget/aggregate-packages.md)
- [Most Frequent API Issues (complete list)](assessment/api-issues/most-frequent-api-issues.md)

### Project Details

- [Mulse.AppHost\Mulse.AppHost.csproj](assessment/projects/Mulse.AppHost.md)
- [Mulse.Billing\Mulse.Billing.csproj](assessment/projects/Mulse.Billing.md)
- [Mulse.CompatibilityPack\Mulse.CompatibilityPack.csproj](assessment/projects/Mulse.CompatibilityPack.md)
- [Mulse.Dbms.SqlServer\Mulse.Dbms.SqlServer.csproj](assessment/projects/Mulse.Dbms.SqlServer.md)
- [Mulse.Excel\Mulse.Excel.csproj](assessment/projects/Mulse.Excel.md)
- [Mulse.Hl7\Mulse.Hl7.csproj](assessment/projects/Mulse.Hl7.md)
- [Mulse.Modules\Mulse.Modules.csproj](assessment/projects/Mulse.Modules.md)
- [Mulse.SamplePlugin\Mulse.SamplePlugin.csproj](assessment/projects/Mulse.SamplePlugin.md)
- [Mulse.ServiceDefaults\Mulse.ServiceDefaults.csproj](assessment/projects/Mulse.ServiceDefaults.md)
- [Mulse.Ui\Mulse.Ui.csproj](assessment/projects/Mulse.Ui.md)
- [Service.Tests\Service.Tests.csproj](assessment/projects/Service.Tests.md)
- [Service\Service.csproj](assessment/projects/Service.md)


