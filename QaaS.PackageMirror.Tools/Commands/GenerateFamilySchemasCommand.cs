using QaaS.PackageMirror.Tools.Infrastructure;

namespace QaaS.PackageMirror.Tools.Commands;

/// <summary>
/// Generates the stable runner and mocker family schemas from the mirrored package tree.
/// </summary>
internal sealed class GenerateFamilySchemasCommand : ICommandHandler
{
    private static readonly FamilyDefinition[] Families =
    [
        new(
            "runner-family",
            [
                "QaaS.Runner",
                "QaaS.Common.Generators",
                "QaaS.Common.Assertions",
                "QaaS.Common.Probes"
            ]),
        new(
            "mocker-family",
            [
                "QaaS.Mocker",
                "QaaS.Common.Generators",
                "QaaS.Common.Processors"
            ])
    ];

    /// <summary>
    /// Resolves the latest mirrored package versions for each family and forwards them into the schema generator.
    /// </summary>
    public async Task<int> ExecuteAsync(CommandArguments arguments)
    {
        var mirrorRoot = arguments.GetOptionalPath("--mirror-root") ?? FindRepositoryRoot();
        var outputRoot = arguments.GetOptionalPath("--output-root") ?? Path.Combine(mirrorRoot, "schemas");
        var snapshotId = arguments.GetOptionalValue("--snapshot-id") ?? DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var triggerRepository = arguments.GetOptionalValue("--trigger-repo");
        var triggerTag = arguments.GetOptionalValue("--trigger-tag");
        var triggerRunId = arguments.GetOptionalValue("--trigger-run-id");
        var triggerOrigin = arguments.GetOptionalValue("--trigger-origin");
        var generatorProject = Path.Combine(mirrorRoot, "QaaS.PackageMirror.FamilySchemas", "QaaS.PackageMirror.FamilySchemas.csproj");
        var packagesRoot = Path.Combine(mirrorRoot, "packages");

        if (!File.Exists(generatorProject))
        {
            throw new FileNotFoundException($"Schema generator project not found at {generatorProject}", generatorProject);
        }

        Directory.CreateDirectory(outputRoot);
        DeleteFileIfPresent(Path.Combine(outputRoot, "index.json"));
        foreach (var family in Families)
        {
            var latestDirectory = Path.Combine(outputRoot, family.Id, "latest");
            DeleteFileIfPresent(Path.Combine(latestDirectory, "metadata.json"));
            DeleteFileIfPresent(Path.Combine(latestDirectory, "docs-diff.json"));

            var argumentsList = new List<string>
            {
                "run",
                "--project",
                generatorProject,
                "--configuration",
                "Release",
                "--no-launch-profile",
                "--",
                "--family",
                family.Id,
                "--packages-root",
                packagesRoot,
                "--output-root",
                outputRoot,
                "--snapshot-id",
                snapshotId
            };

            AppendOptionalArgument(argumentsList, "--trigger-repo", triggerRepository);
            AppendOptionalArgument(argumentsList, "--trigger-tag", triggerTag);
            AppendOptionalArgument(argumentsList, "--trigger-run-id", triggerRunId);
            AppendOptionalArgument(argumentsList, "--trigger-origin", triggerOrigin);

            foreach (var packageId in family.Packages.OrderBy(value => value, StringComparer.Ordinal))
            {
                var version = GetLatestFamilyPackageVersion(mirrorRoot, packageId);
                argumentsList.Add("--package");
                argumentsList.Add($"{packageId}={version}");
            }

            await ProcessRunner.RunAsync("dotnet", argumentsList, mirrorRoot);
        }

        Console.WriteLine($"Generated family schemas into {outputRoot}");
        return 0;
    }

    /// <summary>
    /// Selects the newest mirrored package directory for a family member package.
    /// </summary>
    private static string GetLatestFamilyPackageVersion(string mirrorRoot, string packageId)
    {
        var packageDirectory = Path.Combine(mirrorRoot, "packages", "qaas", packageId.ToLowerInvariant());
        if (!Directory.Exists(packageDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Could not find mirrored package directory for {packageId} at {packageDirectory}");
        }

        var version = Directory.EnumerateDirectories(packageDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"No versions found for mirrored package {packageId}");
        }

        return version;
    }

    /// <summary>
    /// Removes stale script-era artifacts that are no longer part of the stable family output contract.
    /// </summary>
    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Appends an option only when the value is present so the forwarded CLI surface matches the legacy script behavior.
    /// </summary>
    private static void AppendOptionalArgument(List<string> arguments, string option, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        arguments.Add(option);
        arguments.Add(value);
    }

    /// <summary>
    /// Finds the repository root from the compiled tool output location.
    /// </summary>
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

        throw new DirectoryNotFoundException("Could not locate the QaaS.PackageMirror repository root.");
    }

    private sealed record FamilyDefinition(string Id, IReadOnlyList<string> Packages);
}
