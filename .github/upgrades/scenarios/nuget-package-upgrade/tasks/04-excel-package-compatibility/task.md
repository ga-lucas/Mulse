# 04-excel-package-compatibility: Assess Excel dependency upgrades

Evaluate NPOI 2.8.1 and SixLabors.ImageSharp 4.1.3 against the existing Excel behavior and license constraints. The NPOI binary introduces a maintenance-fee agreement and SkiaSharp dependencies, and both packages have significant public API changes. Obtain any necessary user decision before accepting changed license terms; do not make an unverified upgrade. Review `apidiff/NPOI.apidiff.md` and `apidiff/SixLabors.ImageSharp.apidiff.md`.

**Done when**: Compatibility and license risks are documented, user decisions are respected, and any applied upgrade builds and passes Excel tests.
