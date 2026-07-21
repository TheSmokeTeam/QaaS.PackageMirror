using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QaaS.PackageMirror.Tools.Infrastructure;

namespace QaaS.PackageMirror.Tools.Commands;

/// <summary>
/// Downloads the latest tracked package artifacts, rebuilds the mirror package tree, and refreshes the stable family schemas.
/// </summary>
internal sealed class SyncRestoredPackagesCommand : ICommandHandler
{
    private static readonly TrackedRepositoryDefinition[] TrackedRepositories =
    [
        new("TheSmokeTeam/QaaS.Common.Assertions", "CI", true, "restored-packages-artifact"),
        new("TheSmokeTeam/QaaS.Common.Generators", "CI", true, "restored-packages-artifact"),
        new("TheSmokeTeam/QaaS.Common.Probes", "CI", true, "restored-packages-artifact"),
        new("TheSmokeTeam/QaaS.Common.Processors", "CI", true, "restored-packages-artifact"),
        new("TheSmokeTeam/QaaS.Framework", "CI", true, "restored-packages-artifact"),
        new("TheSmokeTeam/QaaS.Mocker", "CI", true, "restored-packages-artifact"),
        new(
            "TheSmokeTeam/Qaas.Mocker.CommunicationObjects",
            "CI",
            true,
            "restored-packages-artifact"
        ),
        new("TheSmokeTeam/QaaS.Runner", "CI", true, "restored-packages-artifact"),
    ];

    private static readonly Regex StableTagPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+$",
        RegexOptions.Compiled
    );
    private static readonly Regex PrereleaseTagPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled
    );
    private static readonly HashSet<string> ExcludedMirrorPackageNames = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "qaas.configuration",
        "qaas.mocker.template",
        "qaas.runner.e2etests",
        "qaas.runner.template",
    };

    /// <summary>
    /// Downloads the latest tracked artifacts, rebuilds the mirror contents, and refreshes the stable family schemas.
    /// </summary>
    public async Task<int> ExecuteAsync(CommandArguments arguments)
    {
        var sourceRepository = arguments.GetOptionalValue("--source-repository");
        var githubToken = arguments.GetOptionalValue("--github-token");
        if (string.IsNullOrWhiteSpace(githubToken))
        {
            throw new InvalidOperationException("--github-token is required.");
        }

        if (!string.IsNullOrWhiteSpace(sourceRepository))
        {
            throw new InvalidOperationException(
                "Targeted sync is not supported. The mirror is rebuilt from the full tracked repository set on every run."
            );
        }

        var workspaceRoot = FindRepositoryRoot();
        var incomingRoot = Path.Combine(workspaceRoot, "incoming");
        var combinedRoot = Path.Combine(incomingRoot, "combined");
        var stateRoot = Path.Combine(workspaceRoot, "state");
        var stagedStateRoot = Path.Combine(incomingRoot, "state");

        RecreateDirectory(incomingRoot);
        Directory.CreateDirectory(combinedRoot);
        Directory.CreateDirectory(stagedStateRoot);
        Directory.CreateDirectory(stateRoot);

        var processedRepositories = new List<string>();
        using var client = CreateGitHubClient(githubToken);

        foreach (var trackedRepository in TrackedRepositories)
        {
            var repositoryKey = trackedRepository.SourceRepository.Replace('/', '_');
            var artifactExtractRoot = Path.Combine(incomingRoot, repositoryKey);

            switch (trackedRepository.SourceKind)
            {
                case "restored-packages-artifact":
                {
                    Console.WriteLine(
                        $"Resolving latest artifact for {trackedRepository.SourceRepository}"
                    );
                    ArtifactContext? artifactContext = null;
                    RestoreArtifactMetadata? metadata = null;
                    var artifactZipPath = Path.Combine(incomingRoot, $"{repositoryKey}.zip");

                    foreach (
                        var candidate in await GetLatestArtifactContextsAsync(
                            client,
                            trackedRepository
                        )
                    )
                    {
                        try
                        {
                            CleanupDirectory(artifactExtractRoot);
                            if (File.Exists(artifactZipPath))
                            {
                                File.Delete(artifactZipPath);
                            }

                            await DownloadFileAsync(
                                client,
                                candidate.Artifact.ArchiveDownloadUrl,
                                artifactZipPath
                            );
                            ZipFile.ExtractToDirectory(
                                artifactZipPath,
                                artifactExtractRoot,
                                overwriteFiles: true
                            );

                            var metadataPath = Path.Combine(
                                artifactExtractRoot,
                                "restore-artifact-metadata.json"
                            );
                            if (!File.Exists(metadataPath))
                            {
                                throw new FileNotFoundException(
                                    $"Missing restore artifact metadata file: {metadataPath}",
                                    metadataPath
                                );
                            }

                            metadata =
                                JsonSerializer.Deserialize<RestoreArtifactMetadata>(
                                    File.ReadAllText(metadataPath),
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                                )
                                ?? throw new InvalidOperationException(
                                    $"Could not deserialize {metadataPath}."
                                );
                        }
                        catch (Exception exception)
                        {
                            metadata = null;
                            Console.Error.WriteLine(
                                $"Skipping restored-packages artifact from {trackedRepository.SourceRepository} run {candidate.Run.Id} because it could not be downloaded or extracted. {exception.GetType().Name}: {exception.Message}"
                            );
                            continue;
                        }

                        if (!IsAcceptedTag(metadata.Tag, trackedRepository.AllowPrerelease))
                        {
                            metadata = null;
                            continue;
                        }

                        artifactContext = candidate;
                        break;
                    }

                    if (artifactContext is null || metadata is null)
                    {
                        if (
                            TryPreserveExistingRepositoryPackages(
                                workspaceRoot,
                                stateRoot,
                                stagedStateRoot,
                                combinedRoot,
                                trackedRepository.SourceRepository
                            )
                        )
                        {
                            processedRepositories.Add(trackedRepository.SourceRepository);
                            break;
                        }

                        Console.Error.WriteLine(
                            $"Skipping {trackedRepository.SourceRepository} because no successful restored-packages artifact is currently available and no existing mirror state could be preserved."
                        );
                        continue;
                    }

                    CopyPackageTree(artifactExtractRoot, combinedRoot);
                    var packageVersions = GetPackageVersions(artifactExtractRoot);
                    WriteStateFile(
                        stagedStateRoot,
                        metadata.Repository,
                        metadata.Tag,
                        artifactContext.Run.HtmlUrl,
                        artifactContext.Run.Id.ToString(),
                        packageVersions
                    );
                    processedRepositories.Add(trackedRepository.SourceRepository);
                    break;
                }
                case "release-package-asset":
                {
                    Console.WriteLine(
                        $"Resolving latest release package for {trackedRepository.SourceRepository}"
                    );
                    var releaseContext = await GetLatestReleasePackageContextAsync(
                        client,
                        trackedRepository
                    );
                    if (releaseContext is null)
                    {
                        Console.Error.WriteLine(
                            $"Skipping {trackedRepository.SourceRepository} because no stable package release with both .nupkg and .snupkg assets is currently available."
                        );
                        continue;
                    }

                    Directory.CreateDirectory(artifactExtractRoot);
                    var assetPaths = new List<string>();
                    foreach (var asset in releaseContext.Assets)
                    {
                        var assetPath = Path.Combine(incomingRoot, asset.Name);
                        await DownloadFileAsync(client, asset.BrowserDownloadUrl, assetPath);
                        assetPaths.Add(assetPath);
                    }

                    CopyReleasePackageAssetsIntoArtifactRoot(assetPaths, artifactExtractRoot);
                    CopyPackageTree(artifactExtractRoot, combinedRoot);
                    var packageVersions = GetPackageVersions(artifactExtractRoot);
                    WriteStateFile(
                        stagedStateRoot,
                        trackedRepository.SourceRepository,
                        releaseContext.Release.TagName,
                        releaseContext.Release.HtmlUrl,
                        releaseContext.Release.Id.ToString(),
                        packageVersions
                    );
                    processedRepositories.Add(trackedRepository.SourceRepository);
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Unsupported source kind '{trackedRepository.SourceKind}' for {trackedRepository.SourceRepository}."
                    );
            }
        }

        if (processedRepositories.Count == 0)
        {
            Console.WriteLine(
                "No tracked repositories exposed usable package artifacts. Existing mirror contents were left unchanged."
            );
            CleanupDirectory(incomingRoot);
            return 0;
        }

        var fullSyncTag = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var githubRepository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var githubRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        var fullSyncOrigin =
            !string.IsNullOrWhiteSpace(githubRepository) && !string.IsNullOrWhiteSpace(githubRunId)
                ? $"https://github.com/{githubRepository}/actions/runs/{githubRunId}"
                : "manual-local-sync";

        await ProcessRunner.RunAsync(
            "dotnet",
            [
                "run",
                "--project",
                Path.Combine(workspaceRoot, "QaaS.PackageMirror", "QaaS.PackageMirror.csproj"),
                "--",
                "--artifact-root",
                combinedRoot,
                "--mirror-root",
                workspaceRoot,
                "--source-repo",
                "TheSmokeTeam/QaaS.PackageMirror.FullSync",
                "--source-tag",
                fullSyncTag,
                "--origin",
                fullSyncOrigin,
                "--source-run-id",
                fullSyncTag,
                "--reset-packages",
                "--skip-duplicate-check",
                "--skip-state-write",
            ],
            workspaceRoot
        );

        await new GenerateFamilySchemasCommand().ExecuteAsync(
            CommandArguments.Parse([
                "--mirror-root",
                workspaceRoot,
                "--snapshot-id",
                fullSyncTag,
                "--trigger-repo",
                "TheSmokeTeam/QaaS.PackageMirror.FullSync",
                "--trigger-tag",
                fullSyncTag,
                "--trigger-run-id",
                fullSyncTag,
                "--trigger-origin",
                fullSyncOrigin,
            ])
        );

        ReplaceStateFiles(stagedStateRoot, stateRoot);
        CleanupDirectory(incomingRoot);

        return 0;
    }

    /// <summary>
    /// Creates a GitHub API client configured with the token and API headers required by the sync workflow.
    /// </summary>
    private static HttpClient CreateGitHubClient(string githubToken)
    {
        var client = new HttpClient
        {
            DefaultRequestHeaders =
            {
                Accept = { MediaTypeWithQualityHeaderValue.Parse("application/vnd.github+json") },
                Authorization = new AuthenticationHeaderValue("Bearer", githubToken),
            },
        };
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QaaS.PackageMirror.Tools/1.0");
        return client;
    }

    /// <summary>
    /// Finds successful restore artifacts for a tracked repository ordered from newest to oldest run.
    /// </summary>
    private static async Task<IReadOnlyList<ArtifactContext>> GetLatestArtifactContextsAsync(
        HttpClient client,
        TrackedRepositoryDefinition repository
    )
    {
        var artifactContexts = new List<ArtifactContext>();
        var runsResponse = await InvokeGitHubApiWithRetryAsync(
            async () =>
            {
                using var response = await client.GetAsync(
                    $"https://api.github.com/repos/{repository.SourceRepository}/actions/runs?per_page=30"
                );
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                return await JsonSerializer.DeserializeAsync<WorkflowRunsResponse>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
            },
            $"Fetching workflow runs for {repository.SourceRepository}"
        );

        foreach (var run in runsResponse?.WorkflowRuns ?? [])
        {
            if (
                !string.Equals(run.Name, repository.SourceWorkflowName, StringComparison.Ordinal)
                || !string.Equals(run.Conclusion, "success", StringComparison.Ordinal)
            )
            {
                continue;
            }

            var artifactsResponse = await InvokeGitHubApiWithRetryAsync(
                async () =>
                {
                    using var response = await client.GetAsync(
                        $"https://api.github.com/repos/{repository.SourceRepository}/actions/runs/{run.Id}/artifacts"
                    );
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    return await JsonSerializer.DeserializeAsync<ArtifactsResponse>(
                        stream,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                },
                $"Fetching artifacts for {repository.SourceRepository} run {run.Id}"
            );

            var artifact = artifactsResponse?.Artifacts?.FirstOrDefault(candidate =>
                candidate.Name == "restored-packages" && !candidate.Expired
            );
            if (artifact is not null)
            {
                artifactContexts.Add(new ArtifactContext(run, artifact));
            }
        }

        return artifactContexts;
    }

    /// <summary>
    /// Finds the latest release that exposes both the package and symbol package assets.
    /// </summary>
    private static async Task<ReleaseContext?> GetLatestReleasePackageContextAsync(
        HttpClient client,
        TrackedRepositoryDefinition repository
    )
    {
        var releases = await InvokeGitHubApiWithRetryAsync(
            async () =>
            {
                using var response = await client.GetAsync(
                    $"https://api.github.com/repos/{repository.SourceRepository}/releases?per_page=20"
                );
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                return await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
            },
            $"Fetching releases for {repository.SourceRepository}"
        );

        foreach (var release in releases ?? [])
        {
            if (release.Draft || release.Prerelease)
            {
                continue;
            }

            if (!IsAcceptedTag(release.TagName, repository.AllowPrerelease))
            {
                continue;
            }

            var packageAsset = release.Assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                && !asset.Name.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase)
            );
            var symbolPackageAsset = release.Assets.FirstOrDefault(asset =>
                asset.Name.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase)
            );
            if (packageAsset is null || symbolPackageAsset is null)
            {
                continue;
            }

            return new ReleaseContext(release, [packageAsset, symbolPackageAsset]);
        }

        return null;
    }

    /// <summary>
    /// Matches either a stable tag or, when allowed, a prerelease tag.
    /// </summary>
    private static bool IsAcceptedTag(string? tagName, bool allowPrerelease)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        return StableTagPattern.IsMatch(tagName)
            || (allowPrerelease && PrereleaseTagPattern.IsMatch(tagName));
    }

    /// <summary>
    /// Downloads a GitHub artifact or release asset to disk.
    /// </summary>
    private static async Task DownloadFileAsync(
        HttpClient client,
        string uri,
        string destinationPath
    )
    {
        await InvokeGitHubApiWithRetryAsync(
            async () =>
            {
                using var response = await client.GetAsync(uri);
                response.EnsureSuccessStatusCode();
                await using var destination = File.Create(destinationPath);
                await using var source = await response.Content.ReadAsStreamAsync();
                await source.CopyToAsync(destination);
                return true;
            },
            $"Downloading '{Path.GetFileName(destinationPath)}'"
        );
    }

    /// <summary>
    /// Retries transient GitHub API failures with exponential backoff.
    /// </summary>
    private static async Task<T> InvokeGitHubApiWithRetryAsync<T>(
        Func<Task<T>> operation,
        string description,
        int maxAttempts = 5,
        int initialDelaySeconds = 2
    )
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception) when (attempt < maxAttempts)
            {
                var delaySeconds = Math.Min(
                    30,
                    initialDelaySeconds * (int)Math.Pow(2, attempt - 1)
                );
                Console.Error.WriteLine(
                    $"{description} failed on attempt {attempt} of {maxAttempts}. Retrying in {delaySeconds} seconds. {exception.GetType().Name}: {exception.Message}"
                );
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
        }

        return await operation();
    }

    private static void CopyPackageTree(string sourceRoot, string destinationRoot)
    {
        foreach (var packageDirectory in Directory.EnumerateDirectories(sourceRoot))
        {
            if (IsExcludedFromMirror(Path.GetFileName(packageDirectory)))
            {
                continue;
            }

            var targetPackageDirectory = Path.Combine(
                destinationRoot,
                Path.GetFileName(packageDirectory)
            );
            Directory.CreateDirectory(targetPackageDirectory);

            foreach (var versionDirectory in Directory.EnumerateDirectories(packageDirectory))
            {
                var targetVersionDirectory = Path.Combine(
                    targetPackageDirectory,
                    Path.GetFileName(versionDirectory)
                );
                DirectoryCopy(versionDirectory, targetVersionDirectory);
            }
        }
    }

    internal static bool TryPreserveExistingRepositoryPackages(
        string workspaceRoot,
        string stateRoot,
        string stagedStateRoot,
        string combinedRoot,
        string sourceRepository
    )
    {
        var statePath = Path.Combine(stateRoot, $"{sourceRepository.Replace('/', '_')}.json");
        if (!File.Exists(statePath))
        {
            return false;
        }

        var state =
            JsonSerializer.Deserialize<StateFile>(
                File.ReadAllText(statePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? throw new InvalidOperationException($"Could not deserialize {statePath}.");

        foreach (var package in state.Packages)
        {
            if (IsExcludedFromMirror(package.Name))
            {
                continue;
            }

            var sourceVersionDirectory = ResolveExistingPackageVersionDirectory(
                workspaceRoot,
                package
            );
            if (sourceVersionDirectory is null)
            {
                var retainedQaasVersionDirectory = ResolveRetainedQaasPackageVersionDirectory(
                    workspaceRoot,
                    package.Name
                );
                if (retainedQaasVersionDirectory is not null)
                {
                    var retainedVersion = Path.GetFileName(retainedQaasVersionDirectory);
                    var retainedTargetDirectory = Path.Combine(
                        combinedRoot,
                        package.Name,
                        retainedVersion
                    );
                    DirectoryCopy(retainedQaasVersionDirectory, retainedTargetDirectory);
                    Console.Error.WriteLine(
                        $"Preserved retained QaaS package '{package.Name}' version '{retainedVersion}' for {sourceRepository} instead of superseded version '{package.Version}'."
                    );
                    continue;
                }

                throw new DirectoryNotFoundException(
                    $"Could not preserve package '{package.Name}' version '{package.Version}' for {sourceRepository} because it does not exist in packages/qaas or packages/not-qaas."
                );
            }

            var targetVersionDirectory = Path.Combine(combinedRoot, package.Name, package.Version);
            DirectoryCopy(sourceVersionDirectory, targetVersionDirectory);
        }

        WriteStateFile(
            stagedStateRoot,
            state.Repository,
            state.Tag,
            state.Origin,
            state.RunId,
            state.Packages.Where(package => !IsExcludedFromMirror(package.Name)).ToList()
        );
        Console.Error.WriteLine(
            $"Preserved existing mirrored packages for {sourceRepository} because no usable restored-packages artifact could be downloaded."
        );
        return true;
    }

    private static string? ResolveExistingPackageVersionDirectory(
        string workspaceRoot,
        StatePackage package
    )
    {
        foreach (var packageBucket in new[] { "qaas", "not-qaas" })
        {
            var candidate = Path.Combine(
                workspaceRoot,
                "packages",
                packageBucket,
                package.Name,
                package.Version
            );
            if (IsPopulatedPackageVersionDirectory(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ResolveRetainedQaasPackageVersionDirectory(
        string workspaceRoot,
        string packageName
    )
    {
        if (!IsQaasPackageName(packageName))
        {
            return null;
        }

        var packageDirectory = Path.Combine(workspaceRoot, "packages", "qaas", packageName);
        if (!Directory.Exists(packageDirectory))
        {
            return null;
        }

        var retainedVersionDirectories = Directory
            .EnumerateDirectories(packageDirectory)
            .Where(IsPopulatedPackageVersionDirectory)
            .ToList();
        if (retainedVersionDirectories.Count > 1)
        {
            throw new InvalidOperationException(
                $"Expected the latest-only QaaS retention policy to leave one populated version of '{packageName}', but found {retainedVersionDirectories.Count}."
            );
        }

        return retainedVersionDirectories.SingleOrDefault();
    }

    private static bool IsPopulatedPackageVersionDirectory(string versionDirectory)
    {
        return Directory.Exists(versionDirectory)
            && Directory.EnumerateFiles(versionDirectory, "*", SearchOption.AllDirectories).Any();
    }

    private static bool IsQaasPackageName(string packageName)
    {
        return packageName
            .Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Contains("qaas", StringComparer.OrdinalIgnoreCase);
    }

    private static List<StatePackage> GetPackageVersions(string artifactRoot)
    {
        var packageVersions = new List<StatePackage>();
        foreach (var packageDirectory in Directory.EnumerateDirectories(artifactRoot))
        {
            if (IsExcludedFromMirror(Path.GetFileName(packageDirectory)))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(packageDirectory))
            {
                packageVersions.Add(
                    new StatePackage
                    {
                        Name = Path.GetFileName(packageDirectory),
                        Version = Path.GetFileName(versionDirectory),
                    }
                );
            }
        }

        return packageVersions
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsExcludedFromMirror(string packageName) =>
        ExcludedMirrorPackageNames.Contains(packageName);

    private static void WriteStateFile(
        string stateRoot,
        string repository,
        string tag,
        string origin,
        string runId,
        List<StatePackage> packages
    )
    {
        var statePath = Path.Combine(stateRoot, $"{repository.Replace('/', '_')}.json");
        var state = new StateFile
        {
            Repository = repository,
            Tag = tag,
            Origin = origin,
            RunId = runId,
            Packages = packages,
        };

        File.WriteAllText(
            statePath,
            JsonSerializer.Serialize(
                state,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }
            ) + Environment.NewLine
        );
    }

    private static void RecreateDirectory(string path)
    {
        CleanupDirectory(path);
        Directory.CreateDirectory(path);
    }

    private static void CleanupDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void ReplaceStateFiles(string stagedStateRoot, string stateRoot)
    {
        Directory.CreateDirectory(stateRoot);
        foreach (
            var stateFile in Directory.EnumerateFiles(
                stateRoot,
                "*.json",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            File.Delete(stateFile);
        }

        foreach (
            var stagedStateFile in Directory.EnumerateFiles(
                stagedStateRoot,
                "*.json",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            File.Copy(
                stagedStateFile,
                Path.Combine(stateRoot, Path.GetFileName(stagedStateFile)),
                overwrite: true
            );
        }
    }

    private static void CopyReleasePackageAssetsIntoArtifactRoot(
        IReadOnlyList<string> packagePaths,
        string artifactRoot
    )
    {
        if (packagePaths.Count == 0)
        {
            throw new InvalidOperationException("At least one package asset path is required.");
        }

        var primaryPackagePath = packagePaths.FirstOrDefault(path =>
            path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase)
        );
        if (primaryPackagePath is null)
        {
            throw new InvalidOperationException(
                "Unable to determine the primary .nupkg package asset."
            );
        }

        var packageIdentity = GetPackageIdentity(primaryPackagePath);
        var targetVersionDirectory = Path.Combine(
            artifactRoot,
            packageIdentity.PackageId,
            packageIdentity.Version
        );
        if (Directory.Exists(targetVersionDirectory))
        {
            Directory.Delete(targetVersionDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetVersionDirectory);
        foreach (var packagePath in packagePaths)
        {
            File.Copy(
                packagePath,
                Path.Combine(targetVersionDirectory, Path.GetFileName(packagePath)),
                overwrite: true
            );
        }
    }

    private static PackageIdentity GetPackageIdentity(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var nuspecEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)
        );
        if (nuspecEntry is null)
        {
            throw new InvalidOperationException(
                $"Unable to determine package identity from '{packagePath}' because it does not contain a .nuspec file."
            );
        }

        using var stream = nuspecEntry.Open();
        using var reader = new StreamReader(stream);
        var nuspec = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
        var metadataNode = nuspec
            .Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "metadata");
        if (metadataNode is null)
        {
            throw new InvalidOperationException(
                $"Unable to determine package identity from '{packagePath}' because the .nuspec metadata element is missing."
            );
        }

        var packageId = metadataNode
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "id")
            ?.Value;
        var version = metadataNode
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "version")
            ?.Value;
        if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException(
                $"Unable to determine package identity from '{packagePath}' because the .nuspec id/version is missing."
            );
        }

        return new PackageIdentity(packageId, version);
    }

    private static void DirectoryCopy(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(
                file,
                Path.Combine(destinationDirectory, Path.GetFileName(file)),
                overwrite: true
            );
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            DirectoryCopy(
                directory,
                Path.Combine(destinationDirectory, Path.GetFileName(directory))
            );
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

    private sealed record TrackedRepositoryDefinition(
        string SourceRepository,
        string SourceWorkflowName,
        bool AllowPrerelease,
        string SourceKind
    );

    private sealed record ArtifactContext(WorkflowRun Run, Artifact Artifact);

    private sealed record ReleaseContext(GitHubRelease Release, List<ReleaseAsset> Assets);

    private sealed record PackageIdentity(string PackageId, string Version);

    private sealed class WorkflowRunsResponse
    {
        [JsonPropertyName("workflow_runs")]
        public List<WorkflowRun> WorkflowRuns { get; set; } = [];
    }

    private sealed class ArtifactsResponse
    {
        public List<Artifact> Artifacts { get; set; } = [];
    }

    private sealed class WorkflowRun
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Conclusion { get; set; } = string.Empty;

        [JsonPropertyName("head_branch")]
        public string HeadBranch { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;
    }

    private sealed class Artifact
    {
        public string Name { get; set; } = string.Empty;
        public bool Expired { get; set; }

        [JsonPropertyName("archive_download_url")]
        public string ArchiveDownloadUrl { get; set; } = string.Empty;
    }

    private sealed class GitHubRelease
    {
        public long Id { get; set; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = string.Empty;

        public bool Draft { get; set; }
        public bool Prerelease { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = string.Empty;

        public List<ReleaseAsset> Assets { get; set; } = [];
    }

    private sealed class ReleaseAsset
    {
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;
    }

    private sealed class RestoreArtifactMetadata
    {
        public string Repository { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
    }

    private sealed class StateFile
    {
        public string Repository { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public List<StatePackage> Packages { get; set; } = [];
    }

    private sealed class StatePackage
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
    }
}
