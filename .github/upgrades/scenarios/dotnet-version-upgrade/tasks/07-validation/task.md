# 07-validation: Validate the full solution

Build the complete solution and run all discovered tests after consolidation. Verify that every project in the solution targets `net11.0`, that the reported package upgrades and incompatible package resolution are complete, and that no dependency conflicts or unintended behavioral regressions remain.

**Done when**: The full solution builds without errors, all tests pass, every project targets `net11.0`, and package dependency checks show no unresolved incompatibilities.
