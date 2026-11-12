## Detected Hints

### hint: multi-project-dependency-ordering
- **Status**: active
- **Priority**: MUST
- **Evidence**: Task 03-ui affects three projects in a dependency chain: Mulse.ServiceDefaults → Mulse.Ui → Service.Tests.
- **Detected**: During task 03-ui research

### hint: test-project-lifecycle
- **Status**: active
- **Priority**: MUST
- **Evidence**: Service.Tests references Mulse.Ui and is in scope; it has 528 API findings and requires significant test-code/build validation while retargeting.
- **Detected**: During task 03-ui research

## Breakdown Decisions

### task: 03-ui
- Broken into 3 dependency-ordered subtasks: 03.01-service-defaults, 03.02-ui, and 03.03-tests.
