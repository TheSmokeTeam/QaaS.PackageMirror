# QaaS.PackageMirror.Tools

`QaaS.PackageMirror.Tools` replaces the repository-owned PowerShell automation with documented C# entry points.

Repository path:

- `QaaS.PackageMirror.Tools`

## Commands

```powershell
dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- sync-restored-packages --github-token <token>
dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- generate-family-schemas --mirror-root $PWD
dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- publish-mirror-release --workspace-root $PWD --github-repository TheSmokeTeam/QaaS.PackageMirror --skip-publish
dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- sync-docs-zim-provenance --docs-root ..\qaas-docs --docs-updated-date-utc 2026-07-13
```

Use `help --command <name>` to print the full option list for a specific command.

## What each command owns

- `sync-restored-packages`: downloads the latest accepted restore artifacts from the tracked QaaS repositories, rebuilds `packages/`, rewrites `state/`, and regenerates stable family schemas.
- `generate-family-schemas`: resolves the latest mirrored hook and product package versions and forwards them into `QaaS.PackageMirror.FamilySchemas`.
- `publish-mirror-release`: prepares the full release zips, the incremental `new-deps-packages.zip`, schema download assets, the qaas-docs ZIM/provenance/image offline bundle, and grouped release notes, and can publish the GitHub release when credentials are provided.
- `sync-docs-zim-provenance`: writes the exact OpenZIM metadata and fixed asset filename contract for a UTC docs-updated date, or validates an existing contract with `--check`.

## Documentation contract

- The README documents the operator-facing command surface and the purpose of each subcommand.
- The C# entrypoints and shared infrastructure carry XML documentation comments so the replacement remains maintainable and reviewable.
- The tool keeps the same command names and option patterns as the removed PowerShell scripts so existing workflows do not break.
