# 05-service: Upgrade Service

Retarget `Service` to `net11.0` and apply the assessment's recommended package updates. Resolve the incompatible `Microsoft.VisualStudio.Azure.Containers.Tools.Targets` package in `Service` and `Service.Tests`; the test project already targets `net11.0` from the UI upgrade task. Address the 254 API findings reported for `Service`, including behavioral changes, and rerun `Service.Tests` to verify its references now resolve to the upgraded app and libraries.

**Done when**: `Service` builds for `net11.0`, the incompatible package is resolved in both projects, and the full `Service.Tests` suite passes against `Service` and `Mulse.Ui` on `net11.0`.
