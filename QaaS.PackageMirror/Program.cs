using System.Text;
using System.Text.Json;
using NuGet.Versioning;

var arguments = CliArguments.Parse(args);
if (!arguments.IsValid(out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage: --artifact-root <path> --mirror-root <path> --source-repo <owner/repo> --source-tag <X.X.X> --origin <url> --source-run-id <runId>");
    return 1;
}

var artifactRoot = Path.GetFullPath(arguments.ArtifactRoot!);
var mirrorRoot = Path.GetFullPath(arguments.MirrorRoot!);
var sourceRepo = arguments.SourceRepo!;
var sourceTag = arguments.SourceTag!;
var origin = arguments.Origin!;
var sourceRunId = arguments.SourceRunId!;

if (!Directory.Exists(artifactRoot))
{
    Console.Error.WriteLine($"Artifact root '{artifactRoot}' does not exist.");
    return 2;
}

Directory.CreateDirectory(Path.Combine(mirrorRoot, "packages"));
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
if (string.Equals(previousSnapshot.RunId, sourceRunId, StringComparison.Ordinal))
{
    Console.WriteLine($"Run {sourceRunId} for {sourceRepo} was already processed.");
    return 0;
}

var changedPackages = ChangeLogBuilder.Build(previousSnapshot, incomingPackages, origin);

PackageCopier.CopyIntoMirror(artifactRoot, Path.Combine(mirrorRoot, "packages"));
DocumentationWriter.WriteReadme(mirrorRoot);
DocumentationWriter.AppendChangeLog(mirrorRoot, sourceRepo, sourceTag, origin, changedPackages);
PackageSnapshot.WriteState(statePath, sourceRepo, sourceTag, origin, sourceRunId, incomingPackages);

Console.WriteLine($"Processed {incomingPackages.Packages.Count} packages for {sourceRepo} {sourceTag}.");
Console.WriteLine($"Detected {changedPackages.Count} package version changes.");
return 0;

internal sealed class CliArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? ArtifactRoot => Get("--artifact-root");
    public string? MirrorRoot => Get("--mirror-root");
    public string? SourceRepo => Get("--source-repo");
    public string? SourceTag => Get("--source-tag");
    public string? Origin => Get("--origin");
    public string? SourceRunId => Get("--source-run-id");

    public static CliArguments Parse(string[] args)
    {
        var parsed = new CliArguments();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                break;
            }

            parsed._values[args[i]] = args[i + 1];
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
                "--source-run-id"
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
}

internal sealed class PackageSnapshot
{
    public string? RunId { get; init; }
    public List<PackageVersion> Packages { get; init; } = [];

    // Artifacts are uploaded as a restore cache tree: <package-id>/<version>/...
    public static PackageSnapshot LoadFromFolder(string artifactRoot)
    {
        var packages = new List<PackageVersion>();
        foreach (var packageDirectory in Directory.EnumerateDirectories(artifactRoot))
        {
            var packageName = Path.GetFileName(packageDirectory);
            foreach (var versionDirectory in Directory.EnumerateDirectories(packageDirectory))
            {
                var version = Path.GetFileName(versionDirectory);
                packages.Add(new PackageVersion(packageName, version));
            }
        }

        return new PackageSnapshot
        {
            Packages = packages
                .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    public static PackageSnapshot LoadFromState(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return new PackageSnapshot();
        }

        var json = File.ReadAllText(statePath);
        var state = JsonSerializer.Deserialize<PersistedState>(json, JsonDefaults.Options);
        return new PackageSnapshot
        {
            RunId = state?.RunId,
            Packages = state?.Packages ?? []
        };
    }

    public static void WriteState(string statePath, string sourceRepo, string sourceTag, string origin, string sourceRunId, PackageSnapshot snapshot)
    {
        var state = new PersistedState
        {
            Repository = sourceRepo,
            Tag = sourceTag,
            Origin = origin,
            RunId = sourceRunId,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Packages = snapshot.Packages
        };

        var json = JsonSerializer.Serialize(state, JsonDefaults.Indented);
        File.WriteAllText(statePath, json + Environment.NewLine);
    }
}

internal static class PackageCopier
{
    // The mirror keeps the restored package tree exactly as the source CI produced it.
    public static void CopyIntoMirror(string artifactRoot, string packagesRoot)
    {
        foreach (var sourceDirectory in Directory.EnumerateDirectories(artifactRoot))
        {
            var packageName = Path.GetFileName(sourceDirectory);
            var targetPackageDirectory = Path.Combine(packagesRoot, packageName);
            Directory.CreateDirectory(targetPackageDirectory);

            foreach (var sourceVersionDirectory in Directory.EnumerateDirectories(sourceDirectory))
            {
                var version = Path.GetFileName(sourceVersionDirectory);
                var targetVersionDirectory = Path.Combine(targetPackageDirectory, version);
                CopyDirectory(sourceVersionDirectory, targetVersionDirectory);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
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

internal static class ChangeLogBuilder
{
    // The changelog tracks the latest resolved version per package id per source repository.
    public static List<ChangeLogEntry> Build(PackageSnapshot previousSnapshot, PackageSnapshot currentSnapshot, string origin)
    {
        var previousByPackage = previousSnapshot.Packages
            .GroupBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => SelectLatest(group), StringComparer.OrdinalIgnoreCase);

        var currentByPackage = currentSnapshot.Packages
            .GroupBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => SelectLatest(group), StringComparer.OrdinalIgnoreCase);

        return currentByPackage
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Where(entry => !string.Equals(previousByPackage.GetValueOrDefault(entry.Key), entry.Value, StringComparison.OrdinalIgnoreCase))
            .Select(entry => new ChangeLogEntry(
                entry.Key,
                previousByPackage.GetValueOrDefault(entry.Key) ?? "none",
                entry.Value,
                origin))
            .ToList();
    }

    private static string SelectLatest(IEnumerable<PackageVersion> packages)
    {
        return packages
            .Select(package => package.Version)
            .OrderByDescending(version => NuGetVersion.Parse(version))
            .First();
    }
}

internal static class DocumentationWriter
{
    public static void WriteReadme(string mirrorRoot)
    {
        var readmePath = Path.Combine(mirrorRoot, "README.md");
        var content = """
# QaaS.PackageMirror

`QaaS.PackageMirror` is the central mirror repository for restored NuGet package trees produced by the QaaS source repositories.

The source repositories do not know that this mirror exists. Their only responsibility is to publish a `restored-packages` workflow artifact when CI runs on a mirror tag. This repository then pulls those artifacts on its own schedule or on manual demand, copies the restored package tree into `packages/`, records the latest processed run in `state/`, and appends dependency version changes to `CHANGELOG.md`.

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
- TheSmokeTeam/QaaS.JsonSchemaExtensions
- TheSmokeTeam/QaaS.Mocker
- TheSmokeTeam/Qaas.Mocker.CommunicationObjects
- TheSmokeTeam/QaaS.Runner

## Source repository contract

Each source repository CI workflow should:

1. restore packages into `${{ github.workspace }}\RestoredPackages`
2. support `workflow_dispatch` so CI can also be triggered manually through the GitHub API
3. on mirror tags `X.X.X` or `X.X.X-alpha.N`, write `restore-artifact-metadata.json` into that folder
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
""";

        File.WriteAllText(readmePath, content + Environment.NewLine);
    }

    public static void AppendChangeLog(string mirrorRoot, string sourceRepo, string sourceTag, string origin, IReadOnlyCollection<ChangeLogEntry> changes)
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
            builder.AppendLine($"## {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC | {sourceRepo} | {sourceTag}");
            builder.AppendLine();

            foreach (var change in changes)
            {
                builder.AppendLine($"Package Name: {change.PackageName}");
                builder.AppendLine($"Version: {change.FromVersion} -> {change.ToVersion}");
                builder.AppendLine($"Origin: {origin}");
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
}

internal sealed class PersistedState
{
    public string Repository { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public List<PackageVersion> Packages { get; set; } = [];
}

internal sealed record PackageVersion(string Name, string Version);
internal sealed record ChangeLogEntry(string PackageName, string FromVersion, string ToVersion, string Origin);

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
