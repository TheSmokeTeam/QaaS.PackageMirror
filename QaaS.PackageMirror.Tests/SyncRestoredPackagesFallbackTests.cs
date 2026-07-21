using System.Text.Json;
using QaaS.PackageMirror.Tools.Commands;
using Xunit;

namespace QaaS.PackageMirror.Tests;

public sealed class SyncRestoredPackagesFallbackTests
{
    private const string SourceRepository = "TheSmokeTeam/QaaS.Runner";

    [Fact]
    public void PreserveFallback_UsesRetainedQaasVersionWhenStateVersionWasRemovedByRetention()
    {
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            var stateRoot = Path.Combine(workspaceRoot, "state");
            var stagedStateRoot = Path.Combine(workspaceRoot, "staged-state");
            var combinedRoot = Path.Combine(workspaceRoot, "combined");
            Directory.CreateDirectory(stateRoot);
            Directory.CreateDirectory(stagedStateRoot);
            Directory.CreateDirectory(combinedRoot);

            Directory.CreateDirectory(
                Path.Combine(
                    workspaceRoot,
                    "packages",
                    "qaas",
                    "qaas.framework.configurations",
                    "1.6.1"
                )
            );
            WritePackage(workspaceRoot, "qaas", "qaas.framework.configurations", "1.6.2");
            WritePackage(workspaceRoot, "not-qaas", "newtonsoft.json", "13.0.3");
            WriteState(
                stateRoot,
                ("qaas.framework.configurations", "1.6.1"),
                ("newtonsoft.json", "13.0.3")
            );

            var preserved = SyncRestoredPackagesCommand.TryPreserveExistingRepositoryPackages(
                workspaceRoot,
                stateRoot,
                stagedStateRoot,
                combinedRoot,
                SourceRepository
            );

            Assert.True(preserved);
            Assert.True(
                File.Exists(
                    Path.Combine(
                        combinedRoot,
                        "qaas.framework.configurations",
                        "1.6.2",
                        "package.marker"
                    )
                )
            );
            Assert.False(
                Directory.Exists(
                    Path.Combine(combinedRoot, "qaas.framework.configurations", "1.6.1")
                )
            );
            Assert.True(
                File.Exists(
                    Path.Combine(combinedRoot, "newtonsoft.json", "13.0.3", "package.marker")
                )
            );
            Assert.True(
                File.Exists(Path.Combine(stagedStateRoot, "TheSmokeTeam_QaaS.Runner.json"))
            );
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void PreserveFallback_StillRejectsMissingExternalPackageVersions()
    {
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            var stateRoot = Path.Combine(workspaceRoot, "state");
            var stagedStateRoot = Path.Combine(workspaceRoot, "staged-state");
            var combinedRoot = Path.Combine(workspaceRoot, "combined");
            Directory.CreateDirectory(stateRoot);
            Directory.CreateDirectory(stagedStateRoot);
            Directory.CreateDirectory(combinedRoot);
            WriteState(stateRoot, ("newtonsoft.json", "13.0.3"));

            var exception = Assert.Throws<DirectoryNotFoundException>(() =>
                SyncRestoredPackagesCommand.TryPreserveExistingRepositoryPackages(
                    workspaceRoot,
                    stateRoot,
                    stagedStateRoot,
                    combinedRoot,
                    SourceRepository
                )
            );

            Assert.Contains("newtonsoft.json", exception.Message);
            Assert.Contains("13.0.3", exception.Message);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "qaas-package-mirror-fallback-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WritePackage(
        string workspaceRoot,
        string bucket,
        string packageName,
        string version
    )
    {
        var packageDirectory = Path.Combine(
            workspaceRoot,
            "packages",
            bucket,
            packageName,
            version
        );
        Directory.CreateDirectory(packageDirectory);
        File.WriteAllText(Path.Combine(packageDirectory, "package.marker"), version);
    }

    private static void WriteState(
        string stateRoot,
        params (string Name, string Version)[] packages
    )
    {
        var state = new
        {
            repository = SourceRepository,
            tag = "4.6.2",
            origin = "https://example.test/run/1",
            runId = "1",
            packages = packages.Select(package => new
            {
                name = package.Name,
                version = package.Version,
            }),
        };
        File.WriteAllText(
            Path.Combine(stateRoot, "TheSmokeTeam_QaaS.Runner.json"),
            JsonSerializer.Serialize(state)
        );
    }
}
