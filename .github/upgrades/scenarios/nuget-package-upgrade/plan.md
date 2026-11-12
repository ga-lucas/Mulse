# NuGet Package Upgrade Plan

## Overview

**Target**: Upgrade confirmed packages to supported stable versions without unverified behavioral changes.
**Scope**: 12-project solution; project-local package references, with no central package management.

## Tasks

### 01-modules-packages: Upgrade core module dependencies

In `Mulse.Modules`, update Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Http, and System.Memory.Data from 10.0.15 to 11.0.0. Assessment API diffs reported no source-breaking changes; confirm behavior through build and tests.

**Done when**: All three references resolve to 11.0.0, affected projects build without warnings, and relevant tests pass.

### 02-cryptography-packages: Upgrade cryptography dependencies

In `Mulse.Dbms.SqlServer` and `Mulse.Excel`, update System.Security.Cryptography.Pkcs and System.Security.Cryptography.Xml from 10.0.15 to 11.0.0. Assessment API diffs reported no source-breaking changes; validate plugin builds and behavior.

**Done when**: Both references resolve to 11.0.0 and affected projects build and tests pass.

### 03-telemetry-test-packages: Upgrade telemetry and test dependencies

In `Mulse.ServiceDefaults`, update OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Extensions.Hosting, and OpenTelemetry.Instrumentation.Runtime from 1.20.1 to 1.21.0. In `Service.Tests`, update Microsoft.NET.Test.Sdk from 18.11.0 to 18.12.0. Assessment API diffs reported no source-breaking changes.

**Done when**: References resolve to the selected versions, the solution builds without warnings, and the complete test suite passes.

### 04-excel-package-compatibility: Assess Excel dependency upgrades

Evaluate NPOI 2.8.1 and SixLabors.ImageSharp 4.1.3 against the existing Excel behavior and license constraints. The NPOI binary introduces a maintenance-fee agreement and SkiaSharp dependencies, and both packages have significant public API changes. Obtain any necessary user decision before accepting changed license terms; do not make an unverified upgrade. Review `apidiff/NPOI.apidiff.md` and `apidiff/SixLabors.ImageSharp.apidiff.md`.

**Done when**: Compatibility and license risks are documented, user decisions are respected, and any applied upgrade builds and passes Excel tests.

### 05-solution-validation: Validate all package changes

Build the entire solution, run all discovered tests, and review the resulting package graph and remaining updates. Report deferred packages or unresolved risk explicitly.

**Done when**: Full solution builds without warnings or errors, all tests pass, and remaining package constraints are recorded.
