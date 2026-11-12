# 04-excel-package-compatibility: Assess Excel dependency upgrades

Evaluate NPOI 2.8.1 and SixLabors.ImageSharp 4.1.3 against the existing Excel behavior and license constraints. The NPOI binary introduces a maintenance-fee agreement and SkiaSharp dependencies, and both packages have significant public API changes. Obtain any necessary user decision before accepting changed license terms; do not make an unverified upgrade. Review `apidiff/NPOI.apidiff.md` and `apidiff/SixLabors.ImageSharp.apidiff.md`.

## Research Findings
- `Mulse.Excel` directly references NPOI 2.7.5 and ImageSharp 3.2.0. Its renderer creates XLS/XLSX sheets and its parser accepts existing workbooks; neither directly calls ImageSharp. Existing ExcelModuleTests exercise plain workbooks, typed cells, dates, formulas, errors, and both formats, but not workbooks containing images.
- NPOI 2.7.5 declares ImageSharp >=2.1.11. The ImageSharp 4.1.3 diff identifies extensive changes; a passing build or plain-workbook tests alone cannot establish binary compatibility for NPOI's image-dependent paths.
- NPOI 2.8.1's packaged binary terms introduce a maintenance-fee agreement and its image APIs move toward SkiaSharp. The package metadata excludes runtime assets for its SkiaSharp dependencies; native/runtime availability requires investigation if this upgrade is chosen. Seek the user's decision before changing NPOI.
- Both examined ImageSharp packages include the Six Labors Split License v1.0; eligibility depends on the applicable terms and project licensing, which has not been established. Do not assert legal eligibility.

**Done when**: Compatibility and license risks are documented, user decisions are respected, and any applied upgrade builds and passes Excel tests.
