# QaaS.PackageMirror

`QaaS.PackageMirror` is the central mirror repository for restored NuGet package trees and generated family JSON schemas produced by the QaaS source repositories.

Each sync rebuilds `packages/` from the latest successful restore artifact of every tracked source repository that currently has a usable `restored-packages` artifact. The rebuild keeps all currently used external package versions under `packages/not-qaas`, keeps only the latest version of each QaaS package under `packages/qaas` while excluding `QaaS.Configuration` and template packages, prefers stable source tags for every tracked repository except `QaaS.Runner`, regenerates the latest Runner and Mocker family schemas under `schemas/`, rewrites the per-repository files in `state/`, publishes a fresh GitHub release marked as latest with the full QaaS bootstrap package set excluding `QaaS.Configuration`, `QaaS.ElasticBootstrap`, and template packages plus a `new-deps` external dependency delta, and appends dependency version changes to `CHANGELOG.md`.

## What this repository contains

- `QaaS.PackageMirror.sln`: solution file for the mirror utility
- `QaaS.PackageMirror/`: the console application that applies package layout and retention rules to a combined restore tree
- `QaaS.PackageMirror.FamilySchemas/`: the console application that generates the Runner and Mocker family JSON schemas from mirrored package versions
- `QaaS.PackageMirror.Tools/`: the documented C# CLI that replaces the old mirror PowerShell scripts
- `packages/qaas/<package-id>/<version>/...`: latest mirrored versions for packages whose ID contains the `qaas` token, except excluded bootstrap-only packages such as `QaaS.Configuration` and templates
- `packages/not-qaas/<package-id>/<version>/...`: all currently used non-QaaS package versions across tracked products
- `schemas/<family>/latest/{schema.json,docs-manifest.json,hook-catalog.json}`: the published schema plus the stable docs contracts used by `qaas-docs`
- `state/`: one state file per source repository, recording the source run and package set used in the last full rebuild
- `.github/workflows/sync-packages.yml`: the workflow that validates mirror changes, publishes releases, and opens synced qaas-docs PRs
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

`sync-packages.yml` is the only workflow in this repository. It runs:

- on pushes to `master` that touch the mirror workflow or implementation
- on manual `workflow_dispatch`

For each full sync it:

1. builds and tests the mirror solution before publishing or pushing anything
2. finds the latest successful `CI` run with a non-expired `restored-packages` artifact for each tracked repository
3. prefers stable source tags for every tracked repository except `QaaS.Runner`, then downloads and combines the latest usable `restored-packages` artifacts into a single restore tree, skipping tracked repositories that do not currently have a usable restore artifact
4. deletes the current mirror package folders before rebuilding so stale external package versions do not survive
5. rebuilds `packages/not-qaas` with all currently used non-QaaS package versions and `packages/qaas` with only the latest allowed QaaS package versions
6. regenerates `schemas/runner-family/latest` and `schemas/mocker-family/latest` from the mirrored package set
7. verifies that both schema families and both package buckets were produced before publishing anything
8. updates `state/`, `README.md`, and `CHANGELOG.md`
9. commits and pushes the updated mirror contents back to the current branch if anything changed
10. downloads the latest `qaas-docs-*.zim` asset from the `TheSmokeTeam/qaas-docs` latest release, or falls back to the latest successful master `docs.yml` ZIM artifact when the latest release does not have a ZIM yet
11. creates a fresh GitHub release marked as latest with `qaas-packages.zip` containing the full QaaS bootstrap package set except `QaaS.Configuration`, `QaaS.ElasticBootstrap`, and template packages, `not-qaas-packages.zip` containing the full current external dependency package set, `new-deps-packages.zip` containing only non-QaaS package versions missing from the previous package baseline under a `new-deps/` root, the Runner and Mocker schema download assets, the latest qaas-docs ZIM asset, and grouped QaaS package versions when release publishing is enabled for that run
12. regenerates the qaas-docs reference docs from the mirrored Runner, Mocker, Framework, Assertions, Generators, Probes, and Processors source tags, bundles the stable schema download assets into the docs site, pushes a new docs feature branch, and opens a qaas-docs pull request
13. on manual runs, can skip release publishing or docs PR creation through workflow inputs while still validating and rebuilding the mirror

## Family schema generation

The generated schemas are intended for editor integration, including Rider/IntelliJ JSON schema mapping.

- `runner-family` is generated from `QaaS.Runner`, `QaaS.Common.Generators`, `QaaS.Common.Assertions`, and `QaaS.Common.Probes`
- `mocker-family` is generated from `QaaS.Mocker`, `QaaS.Common.Generators`, and `QaaS.Common.Processors`

Each family output contains:

- `latest/schema.json`: the schema users should normally download and apply
- `latest/docs-manifest.json`: the stable section contract used to render configuration-reference pages
- `latest/hook-catalog.json`: the stable hook contract used to render hook-reference pages

To regenerate the schemas locally without running a full GitHub sync:

```powershell
dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- generate-family-schemas --mirror-root $PWD
```

To preview the next release assets locally without publishing them:

```powershell
gh release download --repo TheSmokeTeam/qaas-docs --pattern '*.zim' --dir .\qaas-docs-zim

dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- publish-mirror-release `
  --workspace-root $PWD `
  --github-repository TheSmokeTeam/QaaS.PackageMirror `
  --docs-zim-root .\qaas-docs-zim `
  --skip-publish
```

## Secrets

This repository needs a single Actions secret:

- `PACKAGE_MIRROR_TOKEN`

That token must be able to:

- read workflow runs and artifacts from the tracked source repositories
- push commits to `TheSmokeTeam/QaaS.PackageMirror`
- push feature branches and create pull requests in `TheSmokeTeam/qaas-docs`
