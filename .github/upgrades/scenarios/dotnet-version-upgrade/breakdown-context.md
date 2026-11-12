## Detected Hints

### hint: multi-project-dependency-ordering
- **Status**: active
- **Priority**: MUST
- **Evidence**: Task 03-ui affects three projects in a dependency chain: Mulse.ServiceDefaults → Mulse.Ui → Service.Tests. Task 05-service also includes the Service.Tests → Service reference; upgrade Service before validating its dependent tests.
- **Detected**: During task 03-ui research and re-evaluated for task 05-service

### hint: test-project-lifecycle
- **Status**: active
- **Priority**: MUST
- **Evidence**: Service.Tests references Mulse.Ui and Service and is in scope; its current net11 target and existing tests must stay aligned with the Service changes.
- **Detected**: During task 03-ui research and re-evaluated for task 05-service

## Breakdown Decisions

### task: 03-ui
- Broken into 3 dependency-ordered subtasks: 03.01-service-defaults, 03.02-ui, and 03.03-tests.

### task: 05-service
- Broken into 4 concern-focused subtasks: Service framework/container publishing; TFM-aware plugin selection with regression tests; endpoint compatibility; BizTalk/mapping/flow compatibility. The Service.Tests dependent remains validated alongside the related production changes.
