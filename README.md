# QaaS.PackageMirror

`QaaS.PackageMirror` is the central mirror repository for restored NuGet package trees produced by the QaaS source repositories.

The source repositories do not know that this mirror exists. Their only responsibility is to publish a `restored-packages` workflow artifact when CI runs on a stable tag. This repository then pulls those artifacts on its own schedule or on manual demand, copies the restored package tree into `packages/`, records the latest processed run in `state/`, and appends dependency version changes to `CHANGELOG.md`.

## What this repository contains

- `QaaS.PackageMirror.sln`: solution file for the mirror utility
- `QaaS.PackageMirror/`: the console application that merges a downloaded restore artifact into the mirror
- `packages/`: the mirrored restored package tree stored as `packages/<package-id>/<version>/...`
- `state/`: one state file per source repository, used to detect already-processed runs and build changelog diffs
- `.github/workflows/sync-packages.yml`: the workflow that polls all tracked source repositories and updates `master`
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

The mirror does not require a dispatch call from the source repository.

## Mirror workflow behavior

`sync-packages.yml` runs:

- once every 7 days
- on manual `workflow_dispatch`

For each tracked repository it:

1. finds the latest successful `CI` run with a non-expired `restored-packages` artifact
2. downloads that artifact
3. reads the metadata file to determine source repository and tag
4. runs the local console utility to merge the package tree and update `state/` and `CHANGELOG.md`
5. commits the result to `master` if anything changed

## Secrets

This repository needs a single Actions secret:

- `PACKAGE_MIRROR_TOKEN`

That token must be able to:

- read workflow runs and artifacts from the tracked source repositories
- push commits to `TheSmokeTeam/QaaS.PackageMirror`

The source repositories do not need a mirror secret.
