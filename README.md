# QaaS.PackageMirror

This repository mirrors restored NuGet package caches from QaaS package repositories. It is designed so the source repository CI does the restore once and uploads that exact restored package cache as an artifact. `QaaS.PackageMirror` then pulls that artifact on its own and commits it to `master`.

## Repository layout

- `QaaS.PackageMirror.sln`: solution file
- `QaaS.PackageMirror/`: console app used by the mirror workflow
- `packages/`: mirrored restored package cache, stored as `packages/<package-id>/<version>/...`
- `state/`: per-source-repository package snapshot used to calculate changelog diffs
- `.github/workflows/sync-packages.yml`: mirror workflow triggered by source repositories
- `scripts/push-to-artifactory.ps1`: placeholder helper and note about current format

## How the flow works

1. A source repository, for example `QaaS.Framework`, restores packages into a dedicated path during CI.
2. If the run is for a mirror tag matching `X.X.X` or `X.X.X-alpha.N`, CI uploads that restore folder as an artifact.
3. `QaaS.PackageMirror` runs on a schedule or manual dispatch and looks for the latest successful source run that published the restore artifact.
4. `QaaS.PackageMirror` downloads the artifact from that exact source workflow run and runs the console utility.
5. The utility updates:
   - `packages/`
   - `state/<source-repo>.json`
   - `README.md`
   - `CHANGELOG.md`
6. The mirror workflow commits the changes to `master`.

## Why this does not use GitHub Actions cache

GitHub Actions cache is a poor fit here because you want one repository to consume another repository's restored package payload deterministically. Artifacts are tied to the exact workflow run that produced them, so the mirror can fetch the exact restored package set for `QaaS.Framework` tag `X.Y.Z` without re-running restore and without guessing which cache key to use.

## Why the secret is needed

There is one cross-repository operation here:

1. `QaaS.PackageMirror` needs permission to read workflow runs and download a workflow artifact from `QaaS.Framework`.

The default `GITHUB_TOKEN` is scoped to the current repository, so it cannot reliably read private workflow artifacts from another repository. The `PACKAGE_MIRROR_TOKEN` secret is the credential that allows that cross-repo read.

Recommended token scope:

- Fine-grained PAT or GitHub App token
- Repository access: `QaaS.Framework` and `QaaS.PackageMirror`
- Permissions:
  - `Actions: Read`
  - `Contents: Read and Write`
  - `Metadata: Read`

Store `PACKAGE_MIRROR_TOKEN` only in `QaaS.PackageMirror`.

## QaaS.Framework walkthrough

### 1. Add the secret

In `QaaS.PackageMirror` only:

1. Go to `Settings -> Secrets and variables -> Actions`.
2. Create a new repository secret named `PACKAGE_MIRROR_TOKEN`.
3. Paste the PAT or GitHub App token value.

### 2. Push the mirror repo changes

Push this repository with:

- [sync-packages.yml](D:\QaaS\QaaS.PackageMirror\.github\workflows\sync-packages.yml)
- [Program.cs](D:\QaaS\QaaS.PackageMirror\QaaS.PackageMirror\Program.cs)
- [QaaS.PackageMirror.sln](D:\QaaS\QaaS.PackageMirror\QaaS.PackageMirror.sln)

### 3. Push the QaaS.Framework CI change

The updated workflow lives at [ci.yml](D:\QaaS\QaaS.Framework\.github\workflows\ci.yml).

Important behavior:

- restore happens into a dedicated folder in the workflow workspace
- only mirror tags `X.X.X` and `X.X.X-alpha.N` upload the restored package artifact
- the source repo does not dispatch or call `QaaS.PackageMirror`

### 4. Test with a mirror tag

In `QaaS.Framework`:

1. Create a test tag such as `1.2.3-alpha.2`.
2. Push the tag.
3. Wait for the `CI` workflow to finish successfully.
4. In `QaaS.PackageMirror`, either wait for the scheduled run or start `Sync Restored Packages` manually from the Actions tab.
5. Confirm `master` now contains updated content under `packages/` and a new section in `CHANGELOG.md`.

The changelog entries are written exactly like this:

```text
Package Name: <name>
Version: X.X.X -> X.X.X
Origin: <link to the source workflow run>
```

## Current limitation

This version stores the extracted restored package cache, not `.nupkg` files. That matches your latest request and avoids re-restoring in the mirror. If you later want direct `dotnet nuget push` into Artifactory, the source workflow should also upload the original `.nupkg` payload or download cache alongside the extracted restore tree.
