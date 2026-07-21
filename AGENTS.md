# AGENTS.md — QaaS.PackageMirror

Guidance for AI agents working in this repository.

## What this repo is

The **central mirror** for QaaS NuGet packages and family JSON schemas — the backbone of air-gapped deployments and deterministic docs generation. CI of tracked source repos uploads `restored-packages` artifacts; the mirror workflow rebuilds `packages/` and `schemas/`, commits, publishes a GitHub release, and opens a docs-refresh PR on qaas-docs.

## Layout

| Path | Purpose |
|---|---|
| `QaaS.PackageMirror/` | console app applying package layout/retention rules |
| `QaaS.PackageMirror.ConfigurationStub/` | stub project for QaaS.Configuration internal variant support |
| `QaaS.PackageMirror.FamilySchemas/` | console app generating runner/mocker family schemas |
| `QaaS.PackageMirror.Tests/` | NUnit test project |
| `QaaS.PackageMirror.Tools/` | CLI: `sync-restored-packages`, `generate-family-schemas`, `publish-mirror-release`, `sync-docs-zim-provenance` |
| `packages/qaas/<id>/<version>/` | latest QaaS packages (excludes bootstrap-only QaaS.Configuration + templates) |
| `packages/not-qaas/<id>/<version>/` | ALL currently-used external packages |
| `schemas/{runner,mocker}-family/latest/` | `schema.json`, `docs-manifest.json`, `hook-catalog.json` |
| `state/` | per-source-repo JSON recording last synced run |
| `.github/workflows/sync-packages.yml` | the single unified workflow |

## Usage

```powershell
dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- sync-restored-packages --github-token <token>
dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- generate-family-schemas --mirror-root $PWD
dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- publish-mirror-release --workspace-root $PWD --github-repository TheSmokeTeam/QaaS.PackageMirror --skip-publish
dotnet run --project .\QaaS.PackageMirror.Tools\QaaS.PackageMirror.Tools.csproj -- sync-docs-zim-provenance --docs-root ..\qaas-docs --docs-updated-date-utc 2026-07-13
```

## Critical gotchas

- **Source-repo contract**: every tracked repo's CI must restore into `RestoredPackages/`, support `workflow_dispatch`, write `restore-artifact-metadata.json` on stable tags, and upload artifact `restored-packages`. Breaking this contract in a source repo silently starves the mirror.
- Tracked repos: Framework, Runner, Mocker, Qaas.Mocker.CommunicationObjects, Common.{Assertions,Generators,Probes,Processors}. Stable tags preferred for all except QaaS.Runner.
- The sync **deletes and rebuilds** `packages/` wholesale — never hand-place packages; they'll be wiped.
- Mirror keeps only the **latest** QaaS package versions but ALL external dependency versions in use.
- Air-gap pairing: QaaS.Configuration internal variants are rebuilt with the SAME package id+version — consumers must clear NuGet cache (`dotnet nuget locals all --clear`) when variants swap.
- `CHANGELOG.md` and `state/` are workflow-managed; manual edits will be overwritten.
- qaas-docs offline assets are a checked three-file bundle: keep `qaas-docs-zim-provenance.json` canonical and publish it with `qaas-docs.zim` and `qaas-docs-image.tgz`; PackageMirror rejects incomplete bundles or ZIM metadata drift.

## Process

Changes here affect release/docs automation for the whole ecosystem — test workflow changes via `workflow_dispatch` with the `publish_release`, `create_docs_pr`, and `docs_drift_check_only` inputs to control what is published. QaaS harness pipeline for non-trivial changes (rubric ≥7/10); conventional commits.
