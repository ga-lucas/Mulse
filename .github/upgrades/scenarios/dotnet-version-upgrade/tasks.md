# Migration Progress

**Progress**: 11/13 tasks complete <progress value="85" max="100"></progress> 85%
**Status**: Not Started

## Tasks

- ✅ 01-prerequisites: Verify .NET 11 toolchain ([Content](tasks/01-prerequisites/task.md), [Progress](tasks/01-prerequisites/progress-details.md))
- ✅ 02-apphost: Upgrade Mulse.AppHost ([Content](tasks/02-apphost/task.md), [Progress](tasks/02-apphost/progress-details.md))
- ✅ 03-ui: Upgrade Mulse.Ui and its tests ([Content](tasks/03-ui/task.md), [Progress](tasks/03-ui/progress-details.md))
  - ✅ 03.01-service-defaults: Multi-target Mulse.ServiceDefaults and update its packages ([Content](tasks/03.01-service-defaults/task.md), [Progress](tasks/03.01-service-defaults/progress-details.md))
  - ✅ 03.02-ui: Retarget Mulse.Ui and resolve its API findings ([Content](tasks/03.02-ui/task.md), [Progress](tasks/03.02-ui/progress-details.md))
  - ✅ 03.03-tests: Retarget Service.Tests and fix .NET 11 test API issues ([Content](tasks/03.03-tests/task.md), [Progress](tasks/03.03-tests/progress-details.md))
    - ✅ 03.03.01-retarget-compile: Retarget Service.Tests and resolve .NET 11 source incompatibilities ([Content](tasks/03.03.01-retarget-compile/task.md), [Progress](tasks/03.03.01-retarget-compile/progress-details.md))
    - ✅ 03.03.02-behavior-tests: Run the Service.Tests suite and resolve .NET 11 behavioral regressions ([Content](tasks/03.03.02-behavior-tests/task.md), [Progress](tasks/03.03.02-behavior-tests/progress-details.md))
- ✅ 04-service-dependencies: Prepare Service library dependencies ([Content](tasks/04-service-dependencies/task.md), [Progress](tasks/04-service-dependencies/progress-details.md))
  - ✅ 04.01-modules: Multi-target Mulse.Modules and resolve its package/API findings ([Content](tasks/04.01-modules/task.md), [Progress](tasks/04.01-modules/progress-details.md))
  - ✅ 04.02-dependent-libraries: Multi-target the six libraries that depend on Mulse.Modules ([Content](tasks/04.02-dependent-libraries/task.md), [Progress](tasks/04.02-dependent-libraries/progress-details.md))
- 🔲 06-library-consolidation: Retarget shared libraries to .NET 11 ([Content](tasks/06-library-consolidation/task.md))
- 🔲 07-validation: Validate the full solution ([Content](tasks/07-validation/task.md))

**Legend**: ✅ Complete | 🔄 In Progress | 🔲 Pending | ⚠️ Blocked | ❌ Failed
