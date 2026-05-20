# QaaS Configuration

`QaaS.Configuration` is a small NuGet package that supplies fallback configuration defaults to QaaS packages.

The package is intentionally separate from `QaaS.Framework`. The framework keeps its normal flag-driven behavior, and this package only fills in defaults when a run did not already provide explicit values.

## How it works

1. A consuming QaaS package declares a NuGet dependency on `QaaS.Configuration`.
2. When a consuming app restores that package, NuGet also restores `QaaS.Configuration`.
3. This package injects a small module initializer into the consuming build through `buildTransitive`.
4. On application startup, that initializer calls `QaaS.Configuration.ConfigurationBootstrap.Register()`.
5. `ConfigurationBootstrap.Register()` calls `QaaS.Framework.Executions.ExecutionLogging.RegisterDefaults(...)` by reflection.
6. Later, `QaaS.Framework.Executions` uses Elastic defaults only when the run did not already specify `send-logs`, `elastic-uri`, `elastic-username`, `elastic-password`, or a logger configuration file.

The existing Elastic sink behavior in `QaaS.Framework` is unchanged. ReportPortal defaults are exposed as static values for consumers that opt into reading them.

## Where to change defaults

The built-in values live in:

- `QaaS.Configuration/ElasticDefaults.cs`
- `QaaS.Configuration/ReportPortalDefaults.cs`

Public/default package values should stay:

- `ElasticDefaults.SendLogs = false`
- `ElasticDefaults.ElasticUri = null`
- `ElasticDefaults.ElasticUsername = null`
- `ElasticDefaults.ElasticPassword = null`
- `ReportPortalDefaults.Enabled = true`
- `ReportPortalDefaults.ReportPortalUri = null`
- `ReportPortalDefaults.ReportPortalApiKey = null`

For the air-gapped variant, edit those files or use `QaaS.Configuration.Tools`, then rebuild the package with the same package ID and the same version.

## Public package behavior

The public package should be published as:

- Package ID: `QaaS.Configuration`
- Built-in Elastic defaults: disabled / null values
- Built-in ReportPortal defaults: enabled / null values

That version is safe to publish publicly because it does not contain any classified endpoint or credentials.

## Air-gapped package behavior

Inside the air-gapped environment, publish another package with:

- the same package ID: `QaaS.Configuration`
- the same version as the public package
- different values in `ElasticDefaults.cs` and/or `ReportPortalDefaults.cs`

Your Artifactory virtual NuGet source must resolve the air-gapped configuration repo before the mirrored public repo. That way the virtual feed serves the internal package, not the public package.

Example air-gapped defaults:

```csharp
public static class ElasticDefaults
{
    public static bool SendLogs => true;
    public static string? ElasticUri => "http://your-internal-elastic:9200";
    public static string? ElasticUsername => null;
    public static string? ElasticPassword => null;
}

public static class ReportPortalDefaults
{
    public static bool Enabled => true;
    public static string? ReportPortalUri => "https://your-internal-reportportal";
    public static string? ReportPortalApiKey => "your-api-key";
}
```

## Breaking rename

This repository was renamed from `QaaS.ElasticBootstrap` to `QaaS.Configuration`.

Breaking public API changes:

- Package ID changed from `QaaS.ElasticBootstrap` to `QaaS.Configuration`.
- Namespace changed from `QaaS.ElasticBootstrap` to `QaaS.Configuration`.
- `ElasticBootstrapDefaults` was renamed to `ElasticDefaults`.
- `Bootstrap` was renamed to `ConfigurationBootstrap`.

## CI and publish

This repository includes a GitHub Actions workflow that matches the QaaS package publishing pattern used in other repos:

- it restores, builds, and tests on every push and pull request
- it packs only when a Git tag is pushed
- it publishes to NuGet.org using the repository secret `NUGET_AUTH_TOKEN`

## Build

Build:

```powershell
dotnet build .\QaaS.Configuration.sln -c Release
```

Test:

```powershell
dotnet test .\QaaS.Configuration.sln -c Release
```

Pack:

```powershell
dotnet pack .\QaaS.Configuration\QaaS.Configuration.csproj `
  -c Release `
  -p:PackageVersion=1.0.0 `
  -p:Version=1.0.0
```

Example push to a NuGet source:

```powershell
dotnet nuget push .\QaaS.Configuration\bin\Release\QaaS.Configuration.1.0.0.nupkg `
  --source <your-source-name> `
  --skip-duplicate
```

For air-gapped use, do not change the public workflow. Instead, use `QaaS.Configuration.Tools` to rebuild the same package ID and version with your internal defaults, then push that internal package to the higher-priority Artifactory source.

```powershell
dotnet run --project .\QaaS.Configuration.Tools\QaaS.Configuration.Tools.csproj -- `
  --package-version 1.0.0 `
  --send-logs true `
  --elastic-uri http://your-internal-elastic:9200 `
  --reportportal-enabled true `
  --reportportal-uri https://your-internal-reportportal `
  --reportportal-api-key <key> `
  --push-to-artifactory true `
  --artifactory-source https://your-artifactory.example/api/nuget/qaas-local `
  --artifactory-api-key <key>
```
