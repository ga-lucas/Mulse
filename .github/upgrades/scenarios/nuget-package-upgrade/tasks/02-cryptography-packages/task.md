# 02-cryptography-packages: Upgrade cryptography dependencies

In `Mulse.Dbms.SqlServer` and `Mulse.Excel`, update System.Security.Cryptography.Pkcs and System.Security.Cryptography.Xml from 10.0.15 to 11.0.0. Assessment API diffs reported no source-breaking changes; validate plugin builds and behavior.

**Done when**: Both references resolve to 11.0.0 and affected projects build and tests pass.
