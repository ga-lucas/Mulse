# 02-cryptography-packages: Upgrade cryptography dependencies

In `Mulse.Dbms.SqlServer` and `Mulse.Excel`, update System.Security.Cryptography.Pkcs and System.Security.Cryptography.Xml from 10.0.15 to 11.0.0. Assessment API diffs reported no source-breaking changes; validate plugin builds and behavior.

## Research Findings
- Both references are project-local, not centrally managed. The SQL plugin also references Dapper and Microsoft.Data.SqlClient; the Excel plugin references NPOI and ImageSharp. Both consume Mulse.Modules and copy dependencies into the Service plugin directory.
- The quick API diff reports no source-breaking changes. Build the solution to include both independently built plugins, check the resolved references, and run tests covering Excel and runtime encryption/backup.

**Done when**: Both references resolve to 11.0.0 and affected projects build and tests pass.
