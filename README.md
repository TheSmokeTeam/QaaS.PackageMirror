# QaaS.PackageMirror

This repository is updated automatically from source package repositories.

Flow:

1. A source repository CI run restores packages into a dedicated folder.
2. On a mirror tag `X.X.X` or `X.X.X-alpha.N`, that workflow uploads the restored package cache as an artifact.
3. `QaaS.PackageMirror` periodically checks the source repositories for a new successful tagged artifact.
4. `QaaS.PackageMirror` downloads the exact artifact from that source workflow run.
5. The mirror updates `packages/`, `state/`, and `CHANGELOG.md`, then commits to `master`.

Repository layout:

- `QaaS.PackageMirror.sln`: solution file
- `QaaS.PackageMirror/`: console utility used by the mirror workflow
- `packages/`: restored package cache copied from source CI artifacts
- `state/`: per-source-repository package snapshots used for changelog diffs
- `scripts/push-to-artifactory.ps1`: helper to import mirrored packages into a feed

`CHANGELOG.md` entries are written in this format:

```text
Package Name: <name>
Version: X.X.X -> X.X.X
Origin: <workflow run URL>
```
