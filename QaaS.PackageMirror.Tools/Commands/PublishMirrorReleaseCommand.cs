using System.IO.Compression;
using System.Text;
using System.Text.Json;
using QaaS.PackageMirror.Tools.Infrastructure;

namespace QaaS.PackageMirror.Tools.Commands;

/// <summary>
/// Builds the package mirror release asset bundle and optionally publishes it as the latest GitHub release.
/// </summary>
internal sealed class PublishMirrorReleaseCommand : ICommandHandler
{
    private static readonly string[] TrackedRepositories =
    [
        "TheSmokeTeam/QaaS.Common.Assertions",
        "TheSmokeTeam/QaaS.Common.Generators",
        "TheSmokeTeam/QaaS.Common.Probes",
        "TheSmokeTeam/QaaS.Common.Processors",
        "TheSmokeTeam/QaaS.Framework",
        "TheSmokeTeam/QaaS.Mocker",
        "TheSmokeTeam/Qaas.Mocker.CommunicationObjects",
        "TheSmokeTeam/QaaS.Runner",
    ];

    private static readonly string[] FamilyIds = ["runner-family", "mocker-family"];

    private static readonly string[] FamilyJsonFileNames = ["schema.json"];

    private static readonly string[] RequiredSourceArchiveDirectoryNames = TrackedRepositories
        .Select(GetSourceArchiveDirectoryName)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly HashSet<string> ExcludedQaasBootstrapPackageNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "qaas.configuration",
        "qaas.elasticbootstrap",
        "qaas.mocker.template",
        "qaas.runner.template",
    };

    private static readonly HashSet<string> ExcludedDirectoryNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "src",
        "source",
        "sources",
        "contentFiles",
    };

    private static readonly HashSet<string> ExcludedExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ".cs",
        ".csx",
        ".csproj",
        ".fs",
        ".fsx",
        ".fsproj",
        ".vb",
        ".vbproj",
        ".c",
        ".cc",
        ".cpp",
        ".cxx",
        ".h",
        ".hpp",
        ".java",
        ".kt",
        ".js",
        ".jsx",
        ".ts",
        ".tsx",
        ".proto",
    };

    private static readonly HashSet<string> ExcludedCiDirectoryNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ".github",
        ".gitlab",
        ".circleci",
        ".azuredevops",
        ".buildkite",
        ".teamcity",
    };

    private static readonly HashSet<string> ExcludedCiFileNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ".appveyor.yml",
        ".drone.yml",
        ".gitlab-ci.yml",
        ".travis.yml",
        "appveyor.yml",
        "azure-pipelines.yml",
        "azure-pipelines.yaml",
        "buildkite.yml",
        "Jenkinsfile",
    };

    /// <summary>
    /// Builds the release asset set and optionally publishes it as the latest GitHub release for the mirror repository.
    /// </summary>
    public async Task<int> ExecuteAsync(CommandArguments arguments)
    {
        var workspaceRoot = arguments.GetOptionalPath("--workspace-root") ?? FindRepositoryRoot();
        var githubRepository = arguments.GetOptionalValue("--github-repository");
        var branchName = arguments.GetOptionalValue("--branch-name") ?? "master";
        var releaseTag = arguments.GetOptionalValue("--release-tag");
        var releaseTagPrefix = arguments.GetOptionalValue("--release-tag-prefix") ?? "mirror";
        var githubToken = arguments.GetOptionalValue("--github-token");
        var previousPackagesRoot = arguments.GetOptionalPath("--previous-packages-root");
        var sourceArchivesRoot = arguments.GetOptionalPath("--source-archives-root");
        var docsZimRoot = arguments.GetOptionalPath("--docs-zim-root");
        var skipPublish = arguments.HasFlag("--skip-publish");

        if (string.IsNullOrWhiteSpace(githubRepository))
        {
            throw new InvalidOperationException("--github-repository is required.");
        }

        if (!skipPublish && string.IsNullOrWhiteSpace(githubToken))
        {
            throw new InvalidOperationException(
                "GitHub token is required unless --skip-publish is used."
            );
        }

        var packagesRoot = Path.Combine(workspaceRoot, "packages");
        var qaasPackagesRoot = Path.Combine(packagesRoot, "qaas");
        var notQaasPackagesRoot = Path.Combine(packagesRoot, "not-qaas");
        var schemasRoot = Path.Combine(workspaceRoot, "schemas");
        var stateRoot = Path.Combine(workspaceRoot, "state");

        EnsureDirectoryExists(qaasPackagesRoot, "QaaS packages directory");
        EnsureDirectoryExists(notQaasPackagesRoot, "non-QaaS packages directory");
        var schemaAssets = GetSchemaAssets(schemasRoot);
        var sourceArchivePaths = GetSourceArchivePaths(sourceArchivesRoot);
        var docsZimAssetPaths = GetDocsZimAssetPaths(docsZimRoot, required: !skipPublish);

        if (previousPackagesRoot is not null && !Directory.Exists(previousPackagesRoot))
        {
            throw new DirectoryNotFoundException(
                $"Previous packages root '{previousPackagesRoot}' does not exist."
            );
        }

        var israelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
        var releaseTime = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, israelTimeZone);
        var releaseName = releaseTime.ToString("yyyy-MM-dd HH:mm:ss");
        releaseTag ??= $"{releaseTagPrefix}-{releaseTime:yyyyMMdd-HHmmss}";

        var assetRoot = Path.Combine(
            Path.GetTempPath(),
            $"qaas-package-mirror-release-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(assetRoot);

        try
        {
            var currentQaasPackageVersions = GetPackageVersionSetFromDirectory(qaasPackagesRoot);
            var currentNotQaasPackageVersions = GetPackageVersionSetFromDirectory(
                notQaasPackagesRoot
            );
            var previousNotQaasPackageVersions = GetPreviousPackageVersionSet(
                workspaceRoot,
                previousPackagesRoot,
                "not-qaas"
            );
            var releaseQaasPackageVersions = GetFilteredQaasBootstrapVersionSet(
                currentQaasPackageVersions
            );
            var releaseNotQaasPackageVersions = currentNotQaasPackageVersions;
            var releaseNewDependencyPackageVersions = GetNewPackageVersionSet(
                currentNotQaasPackageVersions,
                previousNotQaasPackageVersions
            );

            var qaasZipPath = Path.Combine(assetRoot, "qaas-packages.zip");
            var notQaasZipPath = Path.Combine(assetRoot, "not-qaas-packages.zip");
            var newDepsZipPath = Path.Combine(assetRoot, "new-deps-packages.zip");
            var notesPath = Path.Combine(assetRoot, "release-notes.md");
            var releasePackagesRoot = Path.Combine(assetRoot, "packages");
            var releaseQaasRoot = Path.Combine(releasePackagesRoot, "qaas");
            var releaseNotQaasRoot = Path.Combine(releasePackagesRoot, "not-qaas");
            var releaseNewDepsRoot = Path.Combine(releasePackagesRoot, "new-deps");

            CopyReleasePackageTree(qaasPackagesRoot, releaseQaasRoot, releaseQaasPackageVersions);
            CopyReleasePackageTree(
                notQaasPackagesRoot,
                releaseNotQaasRoot,
                releaseNotQaasPackageVersions
            );
            CopyReleasePackageTree(
                notQaasPackagesRoot,
                releaseNewDepsRoot,
                releaseNewDependencyPackageVersions
            );
            CreateZipArchive(releasePackagesRoot, "qaas", qaasZipPath);
            CreateZipArchive(releasePackagesRoot, "not-qaas", notQaasZipPath);
            CreateZipArchive(releasePackagesRoot, "new-deps", newDepsZipPath);
            var schemaAssetPaths = CopySchemaAssets(schemaAssets, assetRoot);
            var runnerSchemaAssetPath = schemaAssetPaths.Single(path =>
                Path.GetFileName(path).Equals("runner-family-schema.json", StringComparison.Ordinal)
            );
            var mockerSchemaAssetPath = schemaAssetPaths.Single(path =>
                Path.GetFileName(path).Equals("mocker-family-schema.json", StringComparison.Ordinal)
            );
            var releaseAssetPaths = BuildReleaseAssetPaths(
                qaasZipPath,
                notQaasZipPath,
                newDepsZipPath,
                schemaAssetPaths,
                sourceArchivePaths,
                docsZimAssetPaths
            );

            var qaasPackageMap = BuildQaasPackageMap(qaasPackagesRoot, releaseQaasPackageVersions);
            File.WriteAllText(notesPath, BuildReleaseNotes(stateRoot, qaasPackageMap));

            if (skipPublish)
            {
                Console.WriteLine($"Release name: {releaseName}");
                Console.WriteLine($"Release tag: {releaseTag}");
                Console.WriteLine(
                    $"QaaS bootstrap package versions included: {releaseQaasPackageVersions.Count}"
                );
                Console.WriteLine(
                    $"Not-QaaS dependency package versions included: {releaseNotQaasPackageVersions.Count}"
                );
                Console.WriteLine(
                    $"New Not-QaaS dependency package versions included: {releaseNewDependencyPackageVersions.Count}"
                );
                Console.WriteLine($"Source archives included: {sourceArchivePaths.Count}");
                Console.WriteLine($"Schema assets included: {schemaAssetPaths.Count}");
                Console.WriteLine($"Docs ZIM assets included: {docsZimAssetPaths.Count}");
                Console.WriteLine($"Release assets included: {releaseAssetPaths.Count}");
                Console.WriteLine($"QaaS zip: {qaasZipPath}");
                Console.WriteLine($"Not-QaaS zip: {notQaasZipPath}");
                Console.WriteLine($"New deps zip: {newDepsZipPath}");
                foreach (var sourceArchivePath in sourceArchivePaths)
                {
                    Console.WriteLine($"Source archive: {sourceArchivePath}");
                }

                foreach (var schemaAssetPath in schemaAssetPaths)
                {
                    Console.WriteLine($"Schema asset: {schemaAssetPath}");
                }

                foreach (var docsZimAssetPath in docsZimAssetPaths)
                {
                    Console.WriteLine($"Docs ZIM asset: {docsZimAssetPath}");
                }

                Console.WriteLine($"Runner schema asset: {runnerSchemaAssetPath}");
                Console.WriteLine($"Mocker schema asset: {mockerSchemaAssetPath}");
                Console.WriteLine($"Notes file: {notesPath}");
                return 0;
            }

            var environment = new Dictionary<string, string?> { ["GH_TOKEN"] = githubToken };

            var viewResult = await ProcessRunner.RunAsync(
                "gh",
                ["release", "view", releaseTag, "--repo", githubRepository],
                workspaceRoot,
                environment,
                throwOnFailure: false
            );
            if (viewResult.ExitCode == 0)
            {
                throw new InvalidOperationException($"Release tag '{releaseTag}' already exists.");
            }

            await ProcessRunner.RunAsync(
                "gh",
                [
                    "release",
                    "create",
                    releaseTag,
                    .. releaseAssetPaths,
                    "--repo",
                    githubRepository,
                    "--target",
                    branchName,
                    "--title",
                    releaseName,
                    "--notes-file",
                    notesPath,
                    "--latest",
                ],
                workspaceRoot,
                environment
            );

            Console.WriteLine(
                $"Release URL: https://github.com/{githubRepository}/releases/tag/{releaseTag}"
            );
            return 0;
        }
        finally
        {
            if (!skipPublish && Directory.Exists(assetRoot))
            {
                Directory.Delete(assetRoot, recursive: true);
            }
        }
    }

    private static IReadOnlyList<SchemaAsset> GetSchemaAssets(string schemasRoot)
    {
        var assets = new List<SchemaAsset>();
        foreach (var familyId in FamilyIds)
        {
            foreach (var fileName in FamilyJsonFileNames)
            {
                var sourcePath = Path.Combine(schemasRoot, familyId, "latest", fileName);
                EnsureFileExists(sourcePath, $"{familyId} {fileName}");

                var assetName = fileName.Equals("schema.json", StringComparison.Ordinal)
                    ? $"{familyId}-schema.json"
                    : $"{familyId}-{Path.GetFileNameWithoutExtension(fileName)}.json";
                assets.Add(new SchemaAsset(sourcePath, assetName));
            }
        }

        return assets;
    }

    private static IReadOnlyList<string> BuildReleaseAssetPaths(
        string qaasZipPath,
        string notQaasZipPath,
        string newDepsZipPath,
        IReadOnlyList<string> schemaAssetPaths,
        IReadOnlyList<string> sourceArchivePaths,
        IReadOnlyList<string> docsZimAssetPaths
    )
    {
        var releaseAssetPaths = new List<string> { qaasZipPath, notQaasZipPath, newDepsZipPath };
        releaseAssetPaths.AddRange(schemaAssetPaths);
        releaseAssetPaths.AddRange(sourceArchivePaths);
        releaseAssetPaths.AddRange(docsZimAssetPaths);
        return releaseAssetPaths;
    }

    private static IReadOnlyList<string> GetDocsZimAssetPaths(string? docsZimRoot, bool required)
    {
        if (string.IsNullOrWhiteSpace(docsZimRoot))
        {
            if (required)
            {
                throw new InvalidOperationException(
                    "--docs-zim-root is required when publishing a mirror release."
                );
            }

            return [];
        }

        if (!Directory.Exists(docsZimRoot))
        {
            throw new DirectoryNotFoundException($"Docs ZIM root '{docsZimRoot}' does not exist.");
        }

        var docsZimAssetPaths = Directory
            .EnumerateFiles(docsZimRoot, "*.zim", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (docsZimAssetPaths.Length != 1)
        {
            throw new InvalidOperationException(
                $"Docs ZIM root '{docsZimRoot}' must contain exactly one .zim file, but found {docsZimAssetPaths.Length}."
            );
        }

        return docsZimAssetPaths;
    }

    private static IReadOnlyList<string> CopySchemaAssets(
        IReadOnlyList<SchemaAsset> schemaAssets,
        string assetRoot
    )
    {
        var assetPaths = new List<string>();
        foreach (var schemaAsset in schemaAssets)
        {
            var destinationPath = Path.Combine(assetRoot, schemaAsset.AssetName);
            File.Copy(schemaAsset.SourcePath, destinationPath, overwrite: true);
            assetPaths.Add(destinationPath);
        }

        return assetPaths;
    }

    private static IReadOnlyList<string> GetSourceArchivePaths(string? sourceArchivesRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceArchivesRoot))
        {
            return [];
        }

        if (!Directory.Exists(sourceArchivesRoot))
        {
            throw new DirectoryNotFoundException(
                $"Source archives root '{sourceArchivesRoot}' does not exist."
            );
        }

        var sourceArchivePaths = Directory
            .EnumerateFiles(sourceArchivesRoot, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sourceArchivePaths.Length == 0)
        {
            throw new InvalidOperationException(
                $"Source archives root '{sourceArchivesRoot}' does not contain any .zip files."
            );
        }

        if (sourceArchivePaths.Length > 1)
        {
            throw new InvalidOperationException(
                $"Source archives root '{sourceArchivesRoot}' must contain one combined source archive, but found {sourceArchivePaths.Length} .zip files."
            );
        }

        foreach (var sourceArchivePath in sourceArchivePaths)
        {
            ValidateSourceArchive(sourceArchivePath);
        }

        return sourceArchivePaths;
    }

    private static void ValidateSourceArchive(string sourceArchivePath)
    {
        var topLevelDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(sourceArchivePath);
        foreach (var entry in archive.Entries)
        {
            if (IsCiPath(entry.FullName))
            {
                throw new InvalidOperationException(
                    $"Source archive '{sourceArchivePath}' contains CI path '{entry.FullName}'. Source release assets must not contain CI configuration."
                );
            }

            var segments = entry
                .FullName.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 1)
            {
                topLevelDirectories.Add(segments[0]);
            }
        }

        var missingDirectoryNames = RequiredSourceArchiveDirectoryNames
            .Where(directoryName => !topLevelDirectories.Contains(directoryName))
            .ToArray();
        if (missingDirectoryNames.Length > 0)
        {
            throw new InvalidOperationException(
                $"Source archive '{sourceArchivePath}' is missing source folders for tracked repositories: {string.Join(", ", missingDirectoryNames)}."
            );
        }
    }

    private static bool IsCiPath(string relativePath)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => ExcludedCiDirectoryNames.Contains(segment)))
        {
            return true;
        }

        return segments.Length > 0 && ExcludedCiFileNames.Contains(segments[^1]);
    }

    private static string GetSourceArchiveDirectoryName(string repository)
    {
        var repositoryName = repository.Split('/').Last();
        var tokens = repositoryName.Split(
            ['.', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        return string.Join('-', tokens.Select(token => token.ToLowerInvariant())) + "-source";
    }

    /// <summary>
    /// Builds a package lookup from the mirrored QaaS bucket for release-note generation.
    /// </summary>
    private static Dictionary<string, ReleasedPackage> BuildQaasPackageMap(
        string qaasPackagesRoot,
        HashSet<string> releaseQaasPackageVersions
    )
    {
        var packageMap = new Dictionary<string, ReleasedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageDirectory in Directory.EnumerateDirectories(qaasPackagesRoot))
        {
            var packageName = Path.GetFileName(packageDirectory);
            foreach (
                var versionDirectory in Directory
                    .EnumerateDirectories(packageDirectory)
                    .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            )
            {
                var version = Path.GetFileName(versionDirectory);
                if (
                    !releaseQaasPackageVersions.Contains(NewPackageVersionKey(packageName, version))
                )
                {
                    continue;
                }

                packageMap[packageName.ToLowerInvariant()] = new ReleasedPackage(
                    packageName,
                    version
                );
                break;
            }
        }

        return packageMap;
    }

    /// <summary>
    /// Recreates the grouped release notes format that the mirror release previously emitted from PowerShell.
    /// </summary>
    private static string BuildReleaseNotes(
        string stateRoot,
        IReadOnlyDictionary<string, ReleasedPackage> qaasPackageMap
    )
    {
        var releaseLines = new List<string>
        {
            "# Included QaaS bootstrap packages by solution",
            string.Empty,
        };

        if (qaasPackageMap.Count == 0)
        {
            releaseLines.Add("No QaaS bootstrap packages were included in this release.");
            releaseLines.Add(string.Empty);
        }

        foreach (var repository in TrackedRepositories)
        {
            var statePath = Path.Combine(stateRoot, $"{repository.Replace('/', '_')}.json");
            if (!File.Exists(statePath))
            {
                continue;
            }

            var state = JsonSerializer.Deserialize<StateFile>(
                File.ReadAllText(statePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            var repositoryPackages = (state?.Packages ?? [])
                .Where(package => IsQaasPackageName(package.Name))
                .Select(package => package.Name.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (repositoryPackages.Length == 0)
            {
                continue;
            }

            var includedRepositoryPackages = repositoryPackages
                .Where(packageName => qaasPackageMap.ContainsKey(packageName))
                .ToArray();
            if (includedRepositoryPackages.Length == 0)
            {
                continue;
            }

            releaseLines.Add($"## {repository.Split('/').Last()}");
            foreach (var packageName in includedRepositoryPackages)
            {
                var package = qaasPackageMap[packageName];
                releaseLines.Add($"- {package.Name} version {package.Version}");
            }

            releaseLines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, releaseLines) + Environment.NewLine;
    }

    /// <summary>
    /// Copies only the package files that belong in the public release archives.
    /// </summary>
    private static void CopyReleasePackageTree(
        string sourceDirectory,
        string destinationDirectory,
        HashSet<string> includedVersionKeys
    )
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (
            var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
        )
        {
            var fileInfo = new FileInfo(file);
            if (fileInfo.Name.Equals("README.md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(sourceDirectory, fileInfo.FullName);
            var relativeSegments = relativePath.Split(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );
            if (relativeSegments.Length < 2)
            {
                continue;
            }

            var packageVersionKey = NewPackageVersionKey(relativeSegments[0], relativeSegments[1]);
            if (!includedVersionKeys.Contains(packageVersionKey))
            {
                continue;
            }

            if (relativeSegments.Any(segment => ExcludedDirectoryNames.Contains(segment)))
            {
                continue;
            }

            if (ExcludedExtensions.Contains(fileInfo.Extension))
            {
                continue;
            }

            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(fileInfo.FullName, destinationPath, overwrite: true);
        }
    }

    /// <summary>
    /// Creates a zip archive whose entry names preserve the bucket directory at the archive root.
    /// </summary>
    private static void CreateZipArchive(
        string parentDirectory,
        string childDirectoryName,
        string destinationPath
    )
    {
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        var childDirectory = Path.Combine(parentDirectory, childDirectoryName);
        using var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        foreach (
            var file in Directory.EnumerateFiles(childDirectory, "*", SearchOption.AllDirectories)
        )
        {
            var entryName = Path.GetRelativePath(parentDirectory, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
    }

    private static HashSet<string> GetPackageVersionSetFromDirectory(string rootDirectory)
    {
        var packageVersions = NewCaseInsensitiveSet();
        if (!Directory.Exists(rootDirectory))
        {
            return packageVersions;
        }

        foreach (var packageDirectory in Directory.EnumerateDirectories(rootDirectory))
        {
            foreach (var versionDirectory in Directory.EnumerateDirectories(packageDirectory))
            {
                packageVersions.Add(
                    NewPackageVersionKey(
                        Path.GetFileName(packageDirectory),
                        Path.GetFileName(versionDirectory)
                    )
                );
            }
        }

        return packageVersions;
    }

    private static HashSet<string> GetPreviousPackageVersionSet(
        string workspaceRoot,
        string? previousPackagesRoot,
        string bucket
    )
    {
        if (!string.IsNullOrWhiteSpace(previousPackagesRoot))
        {
            return GetPackageVersionSetFromDirectory(
                ResolvePreviousPackageBucketRoot(previousPackagesRoot, bucket)
            );
        }

        var repositoryRoot = GetGitRepositoryRoot(workspaceRoot);
        if (repositoryRoot is null)
        {
            return NewCaseInsensitiveSet();
        }

        var gitRef = ResolvePreviousPackagesGitRef(repositoryRoot);
        if (gitRef is null)
        {
            return NewCaseInsensitiveSet();
        }

        return GetPackageVersionSetFromGitTree(repositoryRoot, gitRef, bucket);
    }

    private static string ResolvePreviousPackageBucketRoot(
        string previousPackagesRoot,
        string bucket
    )
    {
        var directBucketRoot = Path.Combine(previousPackagesRoot, bucket);
        if (Directory.Exists(directBucketRoot))
        {
            return directBucketRoot;
        }

        var nestedBucketRoot = Path.Combine(previousPackagesRoot, "packages", bucket);
        return Directory.Exists(nestedBucketRoot) ? nestedBucketRoot : directBucketRoot;
    }

    private static string? GetGitRepositoryRoot(string path)
    {
        var current = File.Exists(path) ? new FileInfo(path).Directory : new DirectoryInfo(path);
        while (current is not null)
        {
            if (
                Directory.Exists(Path.Combine(current.FullName, ".git"))
                || File.Exists(Path.Combine(current.FullName, ".git"))
            )
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? ResolvePreviousPackagesGitRef(string repositoryRoot)
    {
        var packagesStatus = ProcessRunner
            .RunAsync(
                "git",
                ["-C", repositoryRoot, "status", "--porcelain", "--", "packages"],
                repositoryRoot,
                throwOnFailure: false,
                echoOutput: false
            )
            .GetAwaiter()
            .GetResult();
        if (
            packagesStatus.ExitCode == 0
            && !string.IsNullOrWhiteSpace(packagesStatus.StandardOutput)
        )
        {
            return "HEAD";
        }

        return GitRefExists(repositoryRoot, "HEAD^") ? "HEAD^" : null;
    }

    private static bool GitRefExists(string repositoryRoot, string gitRef)
    {
        var result = ProcessRunner
            .RunAsync(
                "git",
                ["-C", repositoryRoot, "rev-parse", "--verify", "--quiet", gitRef],
                repositoryRoot,
                throwOnFailure: false,
                echoOutput: false
            )
            .GetAwaiter()
            .GetResult();
        return result.ExitCode == 0;
    }

    private static HashSet<string> GetPackageVersionSetFromGitTree(
        string repositoryRoot,
        string gitRef,
        string bucket
    )
    {
        var packageVersions = NewCaseInsensitiveSet();
        var result = ProcessRunner
            .RunAsync(
                "git",
                [
                    "-C",
                    repositoryRoot,
                    "ls-tree",
                    "-r",
                    "--name-only",
                    gitRef,
                    "--",
                    $"packages/{bucket}",
                ],
                repositoryRoot,
                throwOnFailure: false,
                echoOutput: false
            )
            .GetAwaiter()
            .GetResult();
        if (result.ExitCode != 0)
        {
            return packageVersions;
        }

        foreach (
            var treePath in result.StandardOutput.Split(
                ["\r\n", "\n"],
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            var segments = treePath.Split('/', '\\');
            if (
                segments.Length < 4
                || !segments[0].Equals("packages", StringComparison.OrdinalIgnoreCase)
                || !segments[1].Equals(bucket, StringComparison.OrdinalIgnoreCase)
            )
            {
                continue;
            }

            packageVersions.Add(NewPackageVersionKey(segments[2], segments[3]));
        }

        return packageVersions;
    }

    private static HashSet<string> GetNewPackageVersionSet(
        HashSet<string> currentPackages,
        HashSet<string> previousPackages
    )
    {
        var packageVersions = NewCaseInsensitiveSet();
        foreach (var packageVersion in currentPackages)
        {
            if (!previousPackages.Contains(packageVersion))
            {
                packageVersions.Add(packageVersion);
            }
        }

        return packageVersions;
    }

    private static HashSet<string> GetFilteredQaasBootstrapVersionSet(
        HashSet<string> currentPackages
    )
    {
        var packageVersions = NewCaseInsensitiveSet();
        foreach (var packageVersion in currentPackages)
        {
            var segments = packageVersion.Split('/');
            if (segments.Length < 2)
            {
                continue;
            }

            if (ExcludedQaasBootstrapPackageNames.Contains(segments[0]))
            {
                continue;
            }

            packageVersions.Add(packageVersion);
        }

        return packageVersions;
    }

    private static bool IsQaasPackageName(string packageName)
    {
        return packageName
            .Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Contains("qaas", StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> NewCaseInsensitiveSet() => new(StringComparer.OrdinalIgnoreCase);

    private static string NewPackageVersionKey(string packageName, string version) =>
        $"{packageName}/{version}";

    private static void EnsureDirectoryExists(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Missing {description} at {path}");
        }
    }

    private static void EnsureFileExists(string path, string description)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Missing {description} at {path}", path);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "QaaS.PackageMirror.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the QaaS.PackageMirror repository root."
        );
    }

    private sealed record ReleasedPackage(string Name, string Version);

    private sealed record SchemaAsset(string SourcePath, string AssetName);

    private sealed class StateFile
    {
        public List<StatePackage> Packages { get; set; } = [];
    }

    private sealed class StatePackage
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }
}
