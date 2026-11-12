# 03-ui: Upgrade Mulse.Ui and its tests

Retarget `Mulse.Ui` and `Service.Tests` to `net11.0`, and temporarily multi-target `Mulse.ServiceDefaults` so both upgraded and not-yet-upgraded consumers can build. Updating the test project with `Mulse.Ui` avoids leaving a `net10.0` test project referencing a `net11.0` app; its existing `Service` and library references can remain on `net10.0` until their upgrades. The assessment reports 48 API findings for `Mulse.Ui` and 528 for `Service.Tests`, including source-incompatible and behavioral-change findings; review affected code and preserve intended behavior.

**Done when**: `Mulse.Ui` and `Service.Tests` build for `net11.0`, `Mulse.ServiceDefaults` supports both `net10.0` and `net11.0`, and the UI-related tests pass against the upgraded app.
