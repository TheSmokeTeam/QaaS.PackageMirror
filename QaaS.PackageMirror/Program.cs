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
""";

        File.WriteAllText(readmePath, content + Environment.NewLine);
    }

    public static void AppendChangeLog(string mirrorRoot, string sourceRepo, string sourceTag, string origin, IReadOnlyCollection<ChangeLogEntry> changes)
    {
        var path = Path.Combine(mirrorRoot, "CHANGELOG.md");
        var builder = new StringBuilder();

        if (!File.Exists(path))
        {
            builder.AppendLine("# CHANGELOG");
            builder.AppendLine();
        }

        if (changes.Count > 0)
        {
            builder.AppendLine($"## {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC | {sourceRepo} | {sourceTag}");
            builder.AppendLine();

            foreach (var change in changes)
            {
                builder.AppendLine($"Package Name: {change.PackageName}");
                builder.AppendLine($"Version: {change.FromVersion} -> {change.ToVersion}");
                builder.AppendLine($"Origin: {origin}");
                builder.AppendLine();
            }
        }

        var existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        File.WriteAllText(path, builder + existing);
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
