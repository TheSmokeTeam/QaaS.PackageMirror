# Update Local Elastic Parameters

This folder contains a Bash helper that creates a new package mirror folder from an existing `packages/qaas` tree, but with rebuilt `QaaS.Framework.*` packages whose default Elastic logging parameters are changed.

The script does **not** rewrite the whole C# files. It patches only the specific defaults that control the framework logging behavior:

- `QaaS.Framework.Executions/Options/LoggerOptions.cs`
  - `send-logs`
  - `elastic-uri`
  - `elastic-username`
  - `elastic-password`
- `QaaS.Framework.Executions/Constants.cs`
  - default arguments for `AddQaaSElasticSink(...)`

## What The Script Needs

The script needs:

1. A local checkout of the `QaaS.Framework` source repository.
2. A local mirror folder that looks like `packages/qaas`.
3. Bash, Python 3, `dotnet`, and `unzip`.

It uses the source checkout because changing the default values reliably requires rebuilding the framework packages. Patching the mirrored `.nupkg` binaries directly is not a safe or maintainable approach.

## Files

- Script: [update-local-elastic-parameters.sh](/D:/QaaS/_isolated/QaaS.PackageMirror/scripts/update%20local%20elastic%20parameters/update-local-elastic-parameters.sh)

## Configuration

Edit the configuration block at the top of the script if you want hard-coded defaults:

- `FRAMEWORK_REPO_ROOT`
- `OUTPUT_FOLDER_NAME`
- `VERSION_MODE`
- `VERSION_SUFFIX`
- `DEFAULT_SEND_LOGS`
- `DEFAULT_ELASTIC_URI`
- `DEFAULT_ELASTIC_USERNAME`
- `DEFAULT_ELASTIC_PASSWORD`

### Version behavior

- `VERSION_MODE="same"`
  - rebuilds the framework packages with the same version found in `packages/qaas/qaas.framework.executions/<version>`
- `VERSION_MODE="suffix"`
  - rebuilds with `<base-version>-<VERSION_SUFFIX>`

If you keep the same version, make sure your internal feed is the only source or has priority over any public source.

## Inputs

The script requires only one runtime argument:

```bash
./update-local-elastic-parameters.sh <packages-qaas-folder>
```

It also accepts optional runtime overrides:

```bash
./update-local-elastic-parameters.sh <packages-qaas-folder> [elastic-uri] [elastic-username] [elastic-password]
```

If you do not pass those overrides, the values from the top of the script are used.

## What The Script Does

1. Detects the current mirrored `QaaS.Framework.Executions` version from the input `packages/qaas` folder.
2. Temporarily patches only the relevant defaults in the local `QaaS.Framework` source checkout.
3. Runs `dotnet pack` for `QaaS.Framework.sln` with symbols.
4. Creates a new sibling output folder next to the input `packages/qaas` folder.
5. Copies the original `packages/qaas` contents into that new output folder.
6. Replaces the `QaaS.Framework.*` packages in the new output folder with the rebuilt ones.
7. Restores the source files in the `QaaS.Framework` checkout.

The original input mirror is left unchanged.

## Output

If your input is:

```text
packages/qaas
```

and `OUTPUT_FOLDER_NAME="qaas-local-elastic"`, the script creates:

```text
packages/qaas-local-elastic
```

That new folder is a copy of the original mirror with only the `QaaS.Framework.*` packages replaced.

## Example

Using only the hard-coded values from the top of the script:

```bash
"/c/Program Files/Git/bin/bash.exe" -lc '/d/QaaS/_isolated/QaaS.PackageMirror/scripts/update\ local\ elastic\ parameters/update-local-elastic-parameters.sh /d/QaaS/_isolated/QaaS.PackageMirror/packages/qaas'
```

Overriding the URI, username, and password at runtime:

```bash
"/c/Program Files/Git/bin/bash.exe" -lc '/d/QaaS/_isolated/QaaS.PackageMirror/scripts/update\ local\ elastic\ parameters/update-local-elastic-parameters.sh /d/QaaS/_isolated/QaaS.PackageMirror/packages/qaas http://localhost:9200 elastic-user elastic-pass'
```

## Replicating In An Airgapped Environment

1. Mirror the public packages into your internal `packages/qaas` folder.
2. Check out the exact `QaaS.Framework` source you want to rebuild.
3. Set the hard-coded defaults at the top of the script, or pass runtime overrides.
4. Run the script against your mirrored `packages/qaas` folder.
5. Publish or sync the resulting output folder to your internal feed or artifact store.
6. Point your runner restore path or NuGet feed at the generated output folder or the feed populated from it.

If your airgapped environment uses Artifactory instead of a folder feed, treat the output folder as the staging area and publish its rebuilt `QaaS.Framework.*` packages to Artifactory from there.
