# Service container publishing

`Service` uses the .NET SDK's built-in container publishing support. From the repository root, create a container archive without requiring a local Docker daemon:

```powershell
dotnet publish Service\Service.csproj -c Release -t:PublishContainer -p:ContainerArchiveOutputPath=bin\Release\net11.0\container\mulse-service.tar.gz
```

Load the archive into Docker with `docker load --input Service\bin\Release\net11.0\container\mulse-service.tar.gz`. The project pins the .NET 11 ASP.NET Nano Server image and `win-x64`; running the image requires a Windows container host compatible with Windows Server 2025.

For a manual Dockerfile build, run `docker build -f Service\Dockerfile -t mulse-service:manual .` from the repository root. The SDK-published archive is the replacement for the removed Visual Studio Docker launch profile.
