# QaaS.Runner.Template

Installable `dotnet new` template for new QaaS runner repos.

## Install

```bash
dotnet new install QaaS.Runner.Template
# or
dotnet new install .\QaaS.Runner.Template.<version>.nupkg
# or, from this repo root
dotnet new install .
```

## Create

```bash
dotnet new qaas-runner -n MyCompany.QaaS.Runner
```

The generated repo includes:
- `NuGet.config` pointing at `nuget.org`
- `QaaS.Runner` with `Version="*"` so restore pulls the latest stable version from the configured feed
- a minimal valid `test.qaas.yaml`
- Rider launch args for `run test.qaas.yaml`

If you restore from a private feed or local Artifactory, update the generated `NuGet.config` before the first restore.

## Pack Locally

```bash
dotnet pack .\QaaS.Runner.Template.Package.csproj -p:PackageVersion=1.3.0 -o .\artifacts\package
dotnet new install .\artifacts\package\QaaS.Runner.Template.1.3.0.nupkg
dotnet new qaas-runner -n MyCompany.QaaS.Runner
```
