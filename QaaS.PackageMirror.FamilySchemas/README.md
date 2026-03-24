# QaaS.PackageMirror.FamilySchemas

This project generates the published family schemas for the mirror and emits the stable docs contracts that `qaas-docs` needs beside them.

## Outputs

For each family the generator writes:

- `schema.json`
- `docs-manifest.json`
- `hook-catalog.json`

## Why the docs contracts live here

The mirror generator already has the full schema graph, section ordering, and hook discovery results in memory.
Emitting the docs contracts here keeps the documentation pipeline deterministic and prevents `qaas-docs` from reverse-engineering the same information later.

## Consumer

`qaas-docs/tools/QaaS.Docs.Generator` consumes these files to render configuration reference pages.

## Validation flow

The mirror-side contract is validated in two ways:

1. locally by rebuilding `QaaS.PackageMirror.FamilySchemas` and rerunning `scripts/Generate-FamilySchemas.ps1`
2. in CI on pull requests by regenerating the family schemas from the checked-in mirrored packages and failing if the regenerated artifacts do not match the committed ones
