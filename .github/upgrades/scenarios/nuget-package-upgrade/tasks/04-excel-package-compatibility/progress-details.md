# Excel dependency assessment results

- Retained NPOI 2.7.5 and ImageSharp 3.2.0 at the user's request; no package or production code changes were made in this task.
- The proposed NPOI 2.8.1 NuGet binary introduces maintenance-fee terms and different image dependencies. The applicability of those terms was not determined.
- ImageSharp 4.1.3 has substantial API changes and has not been validated against NPOI 2.7.5, particularly for image-containing workbooks. A future upgrade needs representative runtime testing.
- The preceding task's full solution build passed with zero warnings and all 2,100 tests passed using the retained versions. No new runtime tests were run for this assessment-only task.
