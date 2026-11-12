# Cryptography package upgrade results

- Updated System.Security.Cryptography.Pkcs and System.Security.Cryptography.Xml from 10.0.15 to 11.0.0 in their respective plugin projects; both resolved at 11.0.0.
- `dotnet build Mulse.slnx`: succeeded, 0 warnings and 0 errors. Visual Studio workspace build also succeeded.
- `dotnet test Service.Tests\Service.Tests.csproj --no-build`: 2,100 passed, 0 failed, 0 skipped.
- No source changes were required. Integration with an external SQL Server was not separately exercised.
