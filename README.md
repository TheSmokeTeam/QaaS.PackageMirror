# QaaS.PackageMirror

`QaaS.PackageMirror` is the central mirror repository for restored NuGet package trees and generated family JSON schemas produced by the QaaS source repositories.

Each sync rebuilds `packages/` from the latest successful restore artifact of every tracked source repository that currently has a usable `restored-packages` artifact. The rebuild keeps all currently used external package versions under `packages/not-qaas`, keeps only the latest version of each QaaS package under `packages/qaas` while excluding `QaaS.Configuration`, template packages, and non-distribution test projects, prefers stable source tags for every tracked repository except `QaaS.Runner`, regenerates the latest Runner and Mocker family schemas under `schemas/`, rewrites the per-repository files in `state/`, publishes a fresh GitHub release marked as latest with one full QaaS package archive and one full non-QaaS package archive, and appends dependency version changes to `CHANGELOG.md`.

## What this repository contains

- `QaaS.PackageMirror.sln`: solution file for the mirror utility
- `QaaS.PackageMirror/`: the console application that applies package layout and retention rules to a combined restore tree
- `QaaS.PackageMirror.FamilySchemas/`: the console application that generates the Runner and Mocker family JSON schemas from mirrored package versions
- `QaaS.PackageMirror.Tools/`: the documented C# CLI that replaces the old mirror PowerShell scripts
- `packages/qaas/<package-id>/<version>/...`: latest mirrored versions for packages whose ID contains the `qaas` token, except excluded packages such as `QaaS.Configuration`, templates, and `QaaS.Runner.E2ETests`
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

- a fast validation path on pushes to `master` that touch the mirror workflow or implementation
- the full mirror sync on manual `workflow_dispatch`

The push path is intentionally limited to checkout, .NET setup, build, and tests. It validates changes to the mirror implementation without rebuilding packages, publishing a release, or opening docs PRs.

Manual `workflow_dispatch` keeps the complete PackageMirror behavior. For each full sync it:

1. builds and tests the mirror solution before publishing or pushing anything
2. finds the latest successful `CI` run with a non-expired `restored-packages` artifact for each tracked repository
3. prefers stable source tags for every tracked repository except `QaaS.Runner`, then downloads and combines the latest usable `restored-packages` artifacts into a single restore tree; when a source artifact has expired, the sync preserves that repository's saved package state, reusing the populated QaaS version retained by the mirror when an older QaaS dependency was intentionally removed by latest-only retention, while still requiring exact saved versions for external dependencies
4. deletes the current mirror package folders before rebuilding so stale external package versions do not survive
5. rebuilds `packages/not-qaas` with all currently used non-QaaS package versions and `packages/qaas` with only the latest allowed QaaS package versions
6. regenerates `schemas/runner-family/latest` and `schemas/mocker-family/latest` from the mirrored package set
7. verifies that both schema families and both package buckets were produced before publishing anything
8. updates `state/`, `README.md`, and `CHANGELOG.md`
9. commits and pushes the updated mirror contents back to the current branch if anything changed
10. downloads the latest `qaas-docs.zim` asset from the `TheSmokeTeam/qaas-docs` latest release, or falls back to the latest successful master `docs.yml` offline-docs artifact when the latest release does not contain exactly one ZIM
11. normalizes the ZIM filename and creates a fresh GitHub release marked as latest with `qaas-packages.zip` containing the full QaaS bootstrap package set except `QaaS.Configuration`, `QaaS.ElasticBootstrap`, and template packages, `not-qaas-packages.zip` containing the full current external dependency package set, the Runner and Mocker schema download assets, the sanitized source archive, and `qaas-docs.zim`; no dependency-delta, ZIM-provenance, or docs-image assets are published
12. regenerates the qaas-docs reference docs from the mirrored Runner, Mocker, Framework, Assertions, Generators, Probes, and Processors source tags, records the docs generation run date in the ZIM provenance contract, bundles the stable schema download assets into the docs site, pushes a new docs feature branch, and opens a qaas-docs pull request
13. can skip release publishing or docs PR creation through workflow inputs while still validating and rebuilding the mirror

## Docs ZIM contract

Every generated qaas-docs branch carries `qaas-docs-zim-provenance.json`. The contract records schema version `1`, `docsUpdatedDateUtc` as the UTC calendar date of the PackageMirror workflow run's GitHub `created_at` timestamp in exact `yyyy-MM-dd` form, and the ZIM metadata that qaas-docs must embed:

- name: `QaaS Documantation`
- title: `Complete QaaS Documantation`
- description: exactly the same `yyyy-MM-dd` value as `docsUpdatedDateUtc`
- file name: `qaas-docs.zim`

`sync-docs-zim-provenance` writes the contract during docs regeneration and validates the committed contract during drift-only runs. This metadata belongs to the qaas-docs generation flow; `publish-mirror-release` publishes only the ZIM copied to the fixed `qaas-docs.zim` filename.

The fast push path never downloads or republishes docs assets and never opens a docs PR. A manual run with `publish_release: true` requires exactly one qaas-docs ZIM but does not require or publish its provenance or image archive.

## Workflow performance

The fast push path preserves implementation validation without producing release or docs side effects.

The manual full sync is network-bound: it queries source repository workflow artifacts, downloads restored packages, rebuilds `packages/` and `schemas/`, optionally publishes a GitHub release, checks out source repositories, regenerates qaas-docs, validates the generated docs contract, and opens the synced docs PR.

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
gh release download --repo TheSmokeTeam/qaas-docs --pattern 'qaas-docs.zim' --dir .\qaas-docs-zim

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
