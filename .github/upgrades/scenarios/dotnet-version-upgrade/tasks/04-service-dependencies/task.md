# 04-service-dependencies: Prepare Service library dependencies

Temporarily multi-target `Mulse.Modules`, `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin` for `net10.0` and `net11.0`. Resolve package recommendations and the assessment's API incompatibilities as needed for both the existing consumers and `Service`'s new target; the affected libraries include source-incompatible and behavioral-change findings, particularly in `Mulse.Modules` and `Mulse.CompatibilityPack`.

**Done when**: Each listed library builds for both target frameworks, existing `net10.0` consumers remain buildable, and `Service` can resolve its `net11.0` library dependencies.
