using System.Text.Json;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

internal static class SchemaArtifactUtilities
{
    public static PreviousFamilyArtifacts? TryLoadPreviousArtifacts(string latestDirectory)
    {
        var metadataPath = Path.Combine(latestDirectory, "metadata.json");
        var docsManifestPath = Path.Combine(latestDirectory, "docs-manifest.json");
        var hookCatalogPath = Path.Combine(latestDirectory, "hook-catalog.json");
        if (!File.Exists(metadataPath) || !File.Exists(docsManifestPath) || !File.Exists(hookCatalogPath))
        {
            return null;
        }

        var metadata = JsonSerializer.Deserialize<FamilySchemaMetadata>(File.ReadAllText(metadataPath), JsonDefaults.Indented);
        var docsManifest = JsonSerializer.Deserialize<FamilyDocsManifest>(File.ReadAllText(docsManifestPath), JsonDefaults.Indented);
        var hookCatalog = JsonSerializer.Deserialize<FamilyHookCatalog>(File.ReadAllText(hookCatalogPath), JsonDefaults.Indented);
        if (metadata is null || docsManifest is null || hookCatalog is null)
        {
            return null;
        }

        return new PreviousFamilyArtifacts(metadata, docsManifest, hookCatalog);
    }

    public static FamilyDocsDiff BuildDocsDiff(PreviousFamilyArtifacts? previous, FamilySchemaResult current)
    {
        var previousSections = previous?.DocsManifest.Sections.Select(section => section.Id)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var currentSections = current.DocsManifest.Sections.Select(section => section.Id)
            .ToHashSet(StringComparer.Ordinal);

        var previousHooks = previous?.HookCatalog.HookTypes
            .Select(BuildHookKey)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var currentHooks = current.HookCatalog.HookTypes
            .Select(BuildHookKey)
            .ToHashSet(StringComparer.Ordinal);

        var previousPackages = previous?.Metadata.Packages.ToDictionary(package => package.PackageId, package => package.Version,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentPackages = current.Metadata.Packages.ToDictionary(package => package.PackageId, package => package.Version,
            StringComparer.OrdinalIgnoreCase);

        var allPackageIds = previousPackages.Keys
            .Union(currentPackages.Keys, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var packageChanges = allPackageIds
            .Select(packageId =>
            {
                previousPackages.TryGetValue(packageId, out var previousVersion);
                currentPackages.TryGetValue(packageId, out var currentVersion);
                return new PackageVersionDiff(packageId, previousVersion, currentVersion);
            })
            .Where(diff => !string.Equals(diff.PreviousVersion, diff.CurrentVersion, StringComparison.OrdinalIgnoreCase))
            .OrderBy(diff => diff.PackageId, StringComparer.Ordinal)
            .ToArray();

        return new FamilyDocsDiff(
            current.Metadata.FamilyId,
            current.Metadata.GeneratedAtUtc,
            previous?.Metadata.SnapshotId,
            current.Metadata.SnapshotId,
            previous is not null,
            previous?.Metadata.PackageSignatureHash != current.Metadata.PackageSignatureHash,
            previousSections.Except(currentSections).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            currentSections.Except(previousSections).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            previousHooks.Except(currentHooks).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            currentHooks.Except(previousHooks).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            packageChanges);
    }

    public static void WriteIndex(string outputRoot)
    {
        var familyDirectories = Directory.Exists(outputRoot)
            ? Directory.EnumerateDirectories(outputRoot)
            : [];

        var families = familyDirectories
            .Select(directory => TryBuildIndexEntry(outputRoot, directory))
            .Where(entry => entry is not null)
            .Cast<FamilySchemasIndexEntry>()
            .OrderBy(entry => entry.FamilyId, StringComparer.Ordinal)
            .ToArray();

        var index = new FamilySchemasIndex(DateTimeOffset.UtcNow, families);
        var indexPath = Path.Combine(outputRoot, "index.json");
        File.WriteAllText(indexPath, JsonSerializer.Serialize(index, JsonDefaults.Indented) + Environment.NewLine);
    }

    private static FamilySchemasIndexEntry? TryBuildIndexEntry(string outputRoot, string familyDirectory)
    {
        var latestDirectory = Path.Combine(familyDirectory, "latest");
        var metadataPath = Path.Combine(latestDirectory, "metadata.json");
        var docsManifestPath = Path.Combine(latestDirectory, "docs-manifest.json");
        var hookCatalogPath = Path.Combine(latestDirectory, "hook-catalog.json");
        if (!File.Exists(metadataPath) || !File.Exists(docsManifestPath) || !File.Exists(hookCatalogPath))
        {
            return null;
        }

        var metadata = JsonSerializer.Deserialize<FamilySchemaMetadata>(File.ReadAllText(metadataPath), JsonDefaults.Indented);
        if (metadata is null)
        {
            return null;
        }

        return new FamilySchemasIndexEntry(
            metadata.FamilyId,
            new FamilyLatestArtifactPaths(
                RelativeTo(outputRoot, Path.Combine(latestDirectory, "schema.json")),
                RelativeTo(outputRoot, metadataPath),
                RelativeTo(outputRoot, docsManifestPath),
                RelativeTo(outputRoot, hookCatalogPath),
                RelativeTo(outputRoot, Path.Combine(latestDirectory, "docs-diff.json"))));
    }

    private static string BuildHookKey(HookCatalogEntry hook)
    {
        return $"{hook.HookKind}:{hook.Title}";
    }

    private static string RelativeTo(string root, string fullPath)
    {
        return Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
    }
}

internal sealed record PreviousFamilyArtifacts(
    FamilySchemaMetadata Metadata,
    FamilyDocsManifest DocsManifest,
    FamilyHookCatalog HookCatalog);

internal sealed record FamilyDocsDiff(
    string FamilyId,
    DateTimeOffset GeneratedAtUtc,
    string? PreviousSnapshotId,
    string CurrentSnapshotId,
    bool HasPreviousSnapshot,
    bool PackageSignatureChanged,
    IReadOnlyList<string> RemovedSections,
    IReadOnlyList<string> AddedSections,
    IReadOnlyList<string> RemovedHooks,
    IReadOnlyList<string> AddedHooks,
    IReadOnlyList<PackageVersionDiff> PackageChanges);

internal sealed record PackageVersionDiff(
    string PackageId,
    string? PreviousVersion,
    string? CurrentVersion);

internal sealed record FamilySchemasIndex(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<FamilySchemasIndexEntry> Families);

internal sealed record FamilySchemasIndexEntry(
    string FamilyId,
    FamilyLatestArtifactPaths Latest);

internal sealed record FamilyLatestArtifactPaths(
    string SchemaPath,
    string MetadataPath,
    string DocsManifestPath,
    string HookCatalogPath,
    string DocsDiffPath);
