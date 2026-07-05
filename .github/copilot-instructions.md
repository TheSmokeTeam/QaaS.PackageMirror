# Copilot instructions — QaaS.PackageMirror

Read `AGENTS.md` at the repo root first — it documents the mirror layout, Tools CLI, and the source-repo artifact contract.

Essentials:
- Five C# projects: mirror app, ConfigurationStub, FamilySchemas generator, Tests, and Tools CLI (`sync-restored-packages` / `generate-family-schemas` / `publish-mirror-release`).
- `packages/` is rebuilt wholesale by the sync — never hand-place files there; `state/` and `CHANGELOG.md` are workflow-managed.
- Source repos must upload a `restored-packages` artifact with `restore-artifact-metadata.json` on stable tags — that contract feeds everything.
- Schemas published under `schemas/{runner,mocker}-family/latest/` are consumed by editors and qaas-docs — keep `schema.json`/`docs-manifest.json`/`hook-catalog.json` shapes stable.
- Test workflow changes via `workflow_dispatch` with publish-skipping inputs.
- Conventional commits.
