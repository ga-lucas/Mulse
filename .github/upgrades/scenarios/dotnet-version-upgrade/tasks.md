# Migration Progress

**Progress**: 15/16 tasks complete <progress value="94" max="100"></progress> 94%
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
- ✅ 05-service: Upgrade Service ([Content](tasks/05-service/task.md), [Progress](tasks/05-service/progress-details.md))
  - ✅ 05.01-service-container: Retarget Service and replace container tooling ([Content](tasks/05.01-service-container/task.md), [Progress](tasks/05.01-service-container/progress-details.md))
  - ✅ 05.02-plugin-tfm-selection: Select matching plugin assemblies for the host TFM ([Content](tasks/05.02-plugin-tfm-selection/task.md), [Progress](tasks/05.02-plugin-tfm-selection/progress-details.md))
  - ✅ 05.03-endpoint-compatibility: Verify endpoint and middleware compatibility on .NET 11 ([Content](tasks/05.03-endpoint-compatibility/task.md), [Progress](tasks/05.03-endpoint-compatibility/progress-details.md))
  - 🔲 05.04-biztalk-mapping-flow-compatibility: Verify BizTalk, mapping, and flow compatibility on .NET 11 ([Content](tasks/05.04-biztalk-mapping-flow-compatibility/task.md))

**Legend**: ✅ Complete | 🔄 In Progress | 🔲 Pending | ⚠️ Blocked | ❌ Failed
