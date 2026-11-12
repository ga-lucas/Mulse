# 06-library-consolidation: Retarget shared libraries to .NET 11

Once all application and test consumers use `net11.0`, remove `net10.0` from the temporarily multi-targeted libraries: `Mulse.ServiceDefaults`, `Mulse.Modules`, `Mulse.CompatibilityPack`, `Mulse.Billing`, `Mulse.Dbms.SqlServer`, `Mulse.Excel`, `Mulse.Hl7`, and `Mulse.SamplePlugin`. Remove only framework-specific compilation branches that are no longer needed; preserve symbols serving other behavior or security controls.

**Done when**: All eight shared libraries target only `net11.0`, and no project retains a conditional path required solely for `net10.0`.
