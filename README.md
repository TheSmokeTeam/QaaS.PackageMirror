# QaaS.PackageMirror

`QaaS.PackageMirror` is the central mirror repository for restored NuGet package trees produced by the QaaS source repositories.

Each sync rebuilds `packages/` from the latest successful restore artifact of every tracked source repository. The rebuild keeps all currently used external package versions under `packages/not-qaas`, keeps only the latest version of each QaaS package under `packages/qaas`, updates the per-repository files in `state/`, and appends dependency version changes to `CHANGELOG.md`.

## What this repository contains

- `QaaS.PackageMirror.sln`: solution file for the mirror utility
- `QaaS.PackageMirror/`: the console application that applies package layout and retention rules to a combined restore tree
- `packages/qaas/<package-id>/<version>/...`: latest mirrored versions for packages whose ID contains the `qaas` token
- `packages/not-qaas/<package-id>/<version>/...`: all currently used non-QaaS package versions across tracked products
- `state/`: one state file per source repository, recording the source run and package set used in the last full rebuild
- `scripts/Sync-RestoredPackages.ps1`: downloads the latest restore artifact for each tracked repository, rebuilds `packages/`, and refreshes `state/`
- `scripts/Push-MirroredPackages.ps1`: pushes mirrored `.nupkg` files and, when present, matching `.snupkg` files
- `.github/workflows/sync-packages.yml`: the workflow that runs the full rebuild on a schedule or by manual dispatch
- `CHANGELOG.md`: dependency version changes written in the format:

```text
Package Name: <name>
Version: X.X.X -> X.X.X
Origin: <workflow run URL>
```

## Tracked source repositories

- TheSmokeTeam/QaaS.Common.Assertions
- TheSmokeTeam/QaaS.Common.Generators
- TheSmokeTeam/QaaS.Common.Probes
- TheSmokeTeam/QaaS.Common.Processors
- TheSmokeTeam/QaaS.Framework
- TheSmokeTeam/QaaS.Mocker
- TheSmokeTeam/Qaas.Mocker.CommunicationObjects
- TheSmokeTeam/QaaS.Runner

## Source repository contract

Each source repository CI workflow should:

1. restore packages into `${{ github.workspace }}\RestoredPackages`
2. support `workflow_dispatch` so CI can also be triggered manually through the GitHub API
3. on stable tags `X.X.X`, write `restore-artifact-metadata.json` into that folder
4. upload that folder as an artifact named `restored-packages`

## Mirror workflow behavior

`sync-packages.yml` runs:

- once every 7 days
- on manual `workflow_dispatch`

For each full sync it:

1. finds the latest successful `CI` run with a non-expired `restored-packages` artifact for each tracked repository
2. downloads and combines those artifacts into a single restore tree
3. deletes the current mirror package folders before rebuilding so stale external package versions do not survive
4. rebuilds `packages/not-qaas` with all currently used non-QaaS package versions and `packages/qaas` with only the latest QaaS package versions
5. updates `state/`, `README.md`, and `CHANGELOG.md`
6. commits the result to `master` if anything changed

## Pushing mirrored packages and symbols

Use `scripts/Push-MirroredPackages.ps1` after downloading package files into the mirror layout.

```powershell
.\scripts\Push-MirroredPackages.ps1 `
  -PackagesRoot .\packages `
  -Bucket qaas `
  -Source https://nuget.example/v3/index.json `
  -ApiKey $env:NUGET_API_KEY `
  -PushSymbols `
  -SymbolSource https://symbols.example/v3/index.json `
  -SymbolApiKey $env:SYMBOLS_API_KEY
```

To make symbol pushes work, the download step must save the `.snupkg` next to the matching `.nupkg`. The script then calls `dotnet nuget push` with `--symbol-source` and `--symbol-api-key`, which causes `dotnet` to push the paired symbol package together with the main package.

## Secrets

This repository needs a single Actions secret:

- `PACKAGE_MIRROR_TOKEN`

That token must be able to:

- read workflow runs and artifacts from the tracked source repositories
- push commits to `TheSmokeTeam/QaaS.PackageMirror`
