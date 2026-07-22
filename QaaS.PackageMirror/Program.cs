using System.Text;
using System.Text.Json;
using NuGet.Versioning;

var arguments = CliArguments.Parse(args);
if (!arguments.IsValid(out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(
        "Usage: --artifact-root <path> --mirror-root <path> --source-repo <owner/repo> --source-tag <tag> --origin <url> --source-run-id <runId> [--reset-packages] [--skip-duplicate-check] [--skip-state-write]"
    );
    return 1;
}

var artifactRoot = Path.GetFullPath(arguments.ArtifactRoot!);
var mirrorRoot = Path.GetFullPath(arguments.MirrorRoot!);
var sourceRepo = arguments.SourceRepo!;
var sourceTag = arguments.SourceTag!;
var origin = arguments.Origin!;
var sourceRunId = arguments.SourceRunId!;
var packagesRoot = Path.Combine(mirrorRoot, "packages");

if (!Directory.Exists(artifactRoot))
{
    Console.Error.WriteLine($"Artifact root '{artifactRoot}' does not exist.");
    return 2;
}

Directory.CreateDirectory(packagesRoot);
Directory.CreateDirectory(Path.Combine(mirrorRoot, "state"));

var incomingPackages = PackageSnapshot.LoadFromFolder(artifactRoot);
if (incomingPackages.Packages.Count == 0)
{
    Console.Error.WriteLine($"No restored packages were found under '{artifactRoot}'.");
    return 3;
}

var stateKey = sourceRepo.Replace('/', '_');
var statePath = Path.Combine(mirrorRoot, "state", $"{stateKey}.json");
var previousSnapshot = PackageSnapshot.LoadFromState(statePath);
if (
    !arguments.SkipDuplicateCheck
    && string.Equals(previousSnapshot.RunId, sourceRunId, StringComparison.Ordinal)
)
{
    Console.WriteLine($"Run {sourceRunId} for {sourceRepo} was already processed.");
    return 0;
}

var mirroredPackages = PackageSnapshot.LoadFromFolder(packagesRoot);
if (arguments.ResetPackages && Directory.Exists(packagesRoot))
{
    Directory.Delete(packagesRoot, recursive: true);
    Directory.CreateDirectory(packagesRoot);
}

PackageCopier.CopyIntoMirror(artifactRoot, packagesRoot);
PackageRetention.ApplyRetentionPolicy(packagesRoot);

var currentPackages = PackageSnapshot.LoadFromFolder(packagesRoot);
var changedPackages = ChangeLogBuilder.Build(mirroredPackages, currentPackages, origin);

DocumentationWriter.WriteReadme(mirrorRoot);
DocumentationWriter.AppendChangeLog(mirrorRoot, sourceRepo, sourceTag, changedPackages);

if (!arguments.SkipStateWrite)
{
    PackageSnapshot.WriteState(
        statePath,
        sourceRepo,
        sourceTag,
        origin,
        sourceRunId,
        incomingPackages
    );
}

Console.WriteLine(
    $"Processed {incomingPackages.Packages.Count} packages for {sourceRepo} {sourceTag}."
);
Console.WriteLine($"Detected {changedPackages.Count} package version changes.");
Console.WriteLine($"Mirror now stores {currentPackages.Packages.Count} package/version entries.");
return 0;

internal sealed class CliArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public string? ArtifactRoot => Get("--artifact-root");
    public string? MirrorRoot => Get("--mirror-root");
    public string? SourceRepo => Get("--source-repo");
    public string? SourceTag => Get("--source-tag");
    public string? Origin => Get("--origin");
    public string? SourceRunId => Get("--source-run-id");
    public bool ResetPackages => HasFlag("--reset-packages");
    public bool SkipDuplicateCheck => HasFlag("--skip-duplicate-check");
    public bool SkipStateWrite => HasFlag("--skip-state-write");

    public static CliArguments Parse(string[] args)
    {
        var parsed = new CliArguments();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (
                index + 1 < args.Length
                && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            )
            {
                parsed._values[argument] = args[index + 1];
                index++;
                continue;
            }

            parsed._flags.Add(argument);
        }

        return parsed;
    }

    public bool IsValid(out string error)
    {
        var missing = new[]
        {
            "--artifact-root",
            "--mirror-root",
            "--source-repo",
            "--source-tag",
            "--origin",
            "--source-run-id",
        }
            .Where(key => string.IsNullOrWhiteSpace(Get(key)))
            .ToArray();

        if (missing.Length > 0)
        {
            error = $"Missing required arguments: {string.Join(", ", missing)}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private string? Get(string key) => _values.GetValueOrDefault(key);

    private bool HasFlag(string key) => _flags.Contains(key);
}

internal sealed class PackageSnapshot
{
    public string? RunId { get; init; }
    public List<PackageVersion> Packages { get; init; } = [];

    public static PackageSnapshot LoadFromFolder(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return new PackageSnapshot();
        }

        var packages = PackageLayout
            .EnumeratePackageDirectories(rootPath)
            .Where(directory => !PackageExclusions.IsExcludedFromMirror(directory.PackageName))
            .SelectMany(directory =>
                Directory
                    .EnumerateDirectories(directory.DirectoryPath)
                    .Select(versionDirectory => new PackageVersion(
                        directory.PackageName,
                        Path.GetFileName(versionDirectory)
                    ))
            )
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PackageSnapshot { Packages = packages };
    }

    public static PackageSnapshot LoadFromState(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return new PackageSnapshot();
        }

        var json = File.ReadAllText(statePath);
        var state = JsonSerializer.Deserialize<PersistedState>(json, JsonDefaults.Options);
        return new PackageSnapshot { RunId = state?.RunId, Packages = state?.Packages ?? [] };
    }

    public static void WriteState(
        string statePath,
        string sourceRepo,
        string sourceTag,
        string origin,
        string sourceRunId,
        PackageSnapshot snapshot
    )
    {
        var state = new PersistedState
        {
            Repository = sourceRepo,
            Tag = sourceTag,
            Origin = origin,
            RunId = sourceRunId,
            Packages = snapshot.Packages,
        };

        var json = JsonSerializer.Serialize(state, JsonDefaults.Indented);
        File.WriteAllText(statePath, json + Environment.NewLine);
    }
}

internal static class PackageCopier
{
    public static void CopyIntoMirror(string artifactRoot, string packagesRoot)
    {
        foreach (var sourceDirectory in Directory.EnumerateDirectories(artifactRoot))
        {
            var packageName = Path.GetFileName(sourceDirectory);
            if (PackageExclusions.IsExcludedFromMirror(packageName))
            {
                continue;
            }

            var targetPackageDirectory = PackageLayout.GetBucketedPackageDirectory(
                packagesRoot,
                packageName
            );
            Directory.CreateDirectory(targetPackageDirectory);

            foreach (var sourceVersionDirectory in Directory.EnumerateDirectories(sourceDirectory))
            {
                var version = Path.GetFileName(sourceVersionDirectory);
                var targetVersionDirectory = Path.Combine(targetPackageDirectory, version);
                CopyDirectory(sourceVersionDirectory, targetVersionDirectory);
            }
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var targetFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var targetDirectory = Path.Combine(destination, Path.GetFileName(directory));
            CopyDirectory(directory, targetDirectory);
        }
    }
}

internal static class PackageRetention
{
    public static void ApplyRetentionPolicy(string packagesRoot)
    {
        if (!Directory.Exists(packagesRoot))
        {
            return;
        }

        var qaasRoot = Path.Combine(packagesRoot, "qaas");
        if (Directory.Exists(qaasRoot))
        {
            DeleteExcludedPackageDirectories(qaasRoot);
            RetainLatestVersionPerPackage(qaasRoot);
        }

        DeleteEmptyDirectories(packagesRoot);
    }

    private static void DeleteExcludedPackageDirectories(string bucketRoot)
    {
        foreach (var packageDirectory in Directory.EnumerateDirectories(bucketRoot).ToList())
        {
            if (PackageExclusions.IsExcludedFromMirror(Path.GetFileName(packageDirectory)))
            {
                Directory.Delete(packageDirectory, recursive: true);
            }
        }
    }

    private static void RetainLatestVersionPerPackage(string bucketRoot)
    {
        var packageVersions = Directory
            .EnumerateDirectories(bucketRoot)
            .SelectMany(packageDirectory =>
                Directory
                    .EnumerateDirectories(packageDirectory)
                    .Select(versionDirectory => new PackageVersionLocation(
                        Path.GetFileName(packageDirectory),
                        Path.GetFileName(versionDirectory),
                        versionDirectory
                    ))
            )
            .ToList();

        foreach (
            var packageGroup in packageVersions.GroupBy(
                package => package.PackageName,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            var latest = packageGroup
                .OrderByDescending(package => NuGetVersion.Parse(package.Version))
                .First();

            foreach (var version in packageGroup)
            {
                if (PathUtility.AreEquivalent(version.VersionDirectory, latest.VersionDirectory))
                {
                    continue;
                }

                Directory.Delete(version.VersionDirectory, recursive: true);
            }
        }
    }

    private static void DeleteEmptyDirectories(string rootDirectory)
    {
        foreach (var childDirectory in Directory.EnumerateDirectories(rootDirectory).ToList())
        {
            DeleteIfEmptyRecursive(childDirectory);
        }
    }

    private static bool DeleteIfEmptyRecursive(string directory)
    {
        foreach (var childDirectory in Directory.EnumerateDirectories(directory).ToList())
        {
            DeleteIfEmptyRecursive(childDirectory);
        }

        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return false;
        }

        Directory.Delete(directory);
        return true;
    }
}

internal static class PackageLayout
{
    private static readonly HashSet<string> BucketNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "qaas",
        "not-qaas",
    };

    public static IEnumerable<PackageDirectoryLocation> EnumeratePackageDirectories(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        foreach (var topLevelDirectory in Directory.EnumerateDirectories(rootPath))
        {
            var topLevelName = Path.GetFileName(topLevelDirectory);
            if (BucketNames.Contains(topLevelName))
            {
                foreach (var packageDirectory in Directory.EnumerateDirectories(topLevelDirectory))
                {
                    yield return new PackageDirectoryLocation(
                        Path.GetFileName(packageDirectory),
                        packageDirectory
                    );
                }

                continue;
            }

            yield return new PackageDirectoryLocation(topLevelName, topLevelDirectory);
        }
    }

    public static string GetBucketedPackageDirectory(string packagesRoot, string packageName)
    {
        var bucket = PackageClassifier.GetBucket(packageName);
        return Path.Combine(packagesRoot, bucket, packageName);
    }
}

internal static class PackageExclusions
{
    private static readonly HashSet<string> ExcludedMirrorPackageNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "qaas.configuration",
        "qaas.mocker.template",
        "qaas.runner.e2etests",
        "qaas.runner.template",
    };

    public static bool IsExcludedFromMirror(string packageName) =>
        ExcludedMirrorPackageNames.Contains(packageName);
}

internal static class PackageClassifier
{
    public static string GetBucket(string packageName) =>
        IsQaasPackage(packageName) ? "qaas" : "not-qaas";

    private static bool IsQaasPackage(string packageName)
    {
        return packageName
            .Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "qaas", StringComparison.OrdinalIgnoreCase));
    }
}

internal static class ChangeLogBuilder
{
    public static List<ChangeLogEntry> Build(
        PackageSnapshot previousSnapshot,
        PackageSnapshot currentSnapshot,
        string origin
    )
    {
        var previousByPackage = previousSnapshot
            .Packages.GroupBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => SelectLatest(group),
                StringComparer.OrdinalIgnoreCase
            );

        var currentByPackage = currentSnapshot
            .Packages.GroupBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => SelectLatest(group),
                StringComparer.OrdinalIgnoreCase
            );

        return currentByPackage
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Where(entry =>
                !string.Equals(
                    previousByPackage.GetValueOrDefault(entry.Key),
                    entry.Value,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Select(entry => new ChangeLogEntry(
                entry.Key,
                previousByPackage.GetValueOrDefault(entry.Key) ?? "none",
                entry.Value,
                origin
            ))
            .ToList();
    }

    private static string SelectLatest(IEnumerable<PackageVersion> packages)
    {
        return packages
            .Select(package => package.Version)
            .OrderByDescending(NuGetVersion.Parse)
            .First();
    }
}

internal static class DocumentationWriter
{
    public static void WriteReadme(string mirrorRoot)
    {
        var readmePath = Path.Combine(mirrorRoot, "README.md");
        File.WriteAllText(readmePath, ReadmeContent.Value + Environment.NewLine);
    }

    public static void AppendChangeLog(
        string mirrorRoot,
        string sourceRepo,
        string sourceTag,
        IReadOnlyCollection<ChangeLogEntry> changes
    )
    {
        var path = Path.Combine(mirrorRoot, "CHANGELOG.md");
        const string header = "# CHANGELOG";
        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var existingBody = existing.StartsWith(header, StringComparison.Ordinal)
            ? existing[header.Length..].TrimStart('\r', '\n')
            : existing.TrimStart('\r', '\n');

        if (changes.Count > 0)
        {
            var builder = new StringBuilder();
            builder.AppendLine(
                $"## {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC | {sourceRepo} | {sourceTag}"
            );
            builder.AppendLine();

            foreach (var change in changes)
            {
                builder.AppendLine($"Package Name: {change.PackageName}");
                builder.AppendLine($"Version: {change.FromVersion} -> {change.ToVersion}");
                builder.AppendLine($"Origin: {change.Origin}");
                builder.AppendLine();
            }

            var combined = string.IsNullOrWhiteSpace(existingBody)
                ? builder.ToString().TrimEnd() + Environment.NewLine
                : builder + existingBody;

            File.WriteAllText(path, header + Environment.NewLine + Environment.NewLine + combined);
            return;
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, header + Environment.NewLine);
        }
    }

    private static class ReadmeContent
    {
        public static readonly string Value = """
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
12. regenerates the qaas-docs reference docs from the mirrored Runner, Mocker, Framework, Assertions, Generators, Probes, and Processors source tags, enforces canonical two-space YAML and nested-list indentation before any docs branch is pushed, records the docs generation run date in the ZIM provenance contract, bundles the stable schema download assets into the docs site, pushes a new docs feature branch, and opens a qaas-docs pull request
13. can skip release publishing or docs PR creation through workflow inputs while still validating and rebuilding the mirror

The binary ZIM ownership boundary is strict: only qaas-docs CI builds `qaas-docs.zim`. PackageMirror treats the downloaded file as an opaque release input and may only copy, normalize the filename, and republish those existing bytes. It never invokes a ZIM builder; if no single qaas-docs CI ZIM is available, release publishing fails.

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
""";
    }
}

internal sealed class PersistedState
{
    public string Repository { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public List<PackageVersion> Packages { get; set; } = [];
}

internal sealed record PackageVersion(string Name, string Version);

internal sealed record ChangeLogEntry(
    string PackageName,
    string FromVersion,
    string ToVersion,
    string Origin
);

internal sealed record PackageDirectoryLocation(string PackageName, string DirectoryPath);

internal sealed record PackageVersionLocation(
    string PackageName,
    string Version,
    string VersionDirectory
);

internal static class PathUtility
{
    public static bool AreEquivalent(string left, string right)
    {
        var leftPath = Path.GetFullPath(left)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rightPath = Path.GetFullPath(right)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
