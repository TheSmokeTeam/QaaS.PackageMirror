using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace QaaS.PackageMirror.Tests;

public class CommandCoverageTests
{
    private static readonly Type PublishMirrorReleaseCommandType =
        Type.GetType("QaaS.PackageMirror.Tools.Commands.PublishMirrorReleaseCommand, QaaS.PackageMirror.Tools", throwOnError: true)!;

    private static readonly Type SyncRestoredPackagesCommandType =
        Type.GetType("QaaS.PackageMirror.Tools.Commands.SyncRestoredPackagesCommand, QaaS.PackageMirror.Tools", throwOnError: true)!;

    [Fact]
    public void PublishMirrorRelease_HelperMethods_FilterAndDiffPackageSetsCaseInsensitively()
    {
        var filterMethod = PublishMirrorReleaseCommandType
            .GetMethod("GetFilteredQaasBootstrapVersionSet", BindingFlags.Static | BindingFlags.NonPublic)!;
        var diffMethod = PublishMirrorReleaseCommandType
            .GetMethod("GetNewPackageVersionSet", BindingFlags.Static | BindingFlags.NonPublic)!;

        var currentQaasPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "QaaS.Runner/4.2.0",
            "qaas.elasticbootstrap/1.0.0",
            "QaaS.Runner.Template/1.4.0"
        };
        var currentNotQaasPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Other.Sample/1.0.0",
            "Other.Sample/1.1.0",
            "Another.Sample/2.0.0"
        };
        var previousNotQaasPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "other.sample/1.0.0"
        };

        var filteredQaasPackages =
            (HashSet<string>)filterMethod.Invoke(null, [currentQaasPackages])!;
        var newNotQaasPackages =
            (HashSet<string>)diffMethod.Invoke(null, [currentNotQaasPackages, previousNotQaasPackages])!;

        Assert.Contains("QaaS.Runner/4.2.0", filteredQaasPackages);
        Assert.Contains("QaaS.Runner.Template/1.4.0", filteredQaasPackages);
        Assert.DoesNotContain("qaas.elasticbootstrap/1.0.0", filteredQaasPackages);

        Assert.DoesNotContain("Other.Sample/1.0.0", newNotQaasPackages);
        Assert.Contains("Other.Sample/1.1.0", newNotQaasPackages);
        Assert.Contains("Another.Sample/2.0.0", newNotQaasPackages);
    }

    [Theory]
    [InlineData("1.2.3", false, true)]
    [InlineData("1.2.3-alpha.1", false, false)]
    [InlineData("1.2.3-alpha.1", true, true)]
    [InlineData("release-1.2.3", true, false)]
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    public void SyncRestoredPackages_IsAcceptedTag_RespectsStableAndPrereleaseRules(
        string? tagName,
        bool allowPrerelease,
        bool expected)
    {
        var isAcceptedTagMethod = SyncRestoredPackagesCommandType
            .GetMethod("IsAcceptedTag", BindingFlags.Static | BindingFlags.NonPublic)!;

        var accepted = (bool)isAcceptedTagMethod.Invoke(null, [tagName, allowPrerelease])!;

        Assert.Equal(expected, accepted);
    }

    [Fact]
    public void SyncRestoredPackages_SelectLatestStableTag_PicksHighestStableSemanticVersion()
    {
        var selectLatestStableTagMethod = SyncRestoredPackagesCommandType
            .GetMethod("SelectLatestStableTag", BindingFlags.Static | BindingFlags.NonPublic)!;

        var selectedTag = (string?)selectLatestStableTagMethod.Invoke(
            null,
            [new string?[] { "1.4.0", "1.3.9", "1.4.0-alpha.1", "2.0.0", null }]);

        Assert.Equal("2.0.0", selectedTag);
    }

    [Fact]
    public void PublishMirrorRelease_ResolvePreviousPackagesGitRef_UsesHeadWhenPackagesDirtyOtherwiseHeadCaret()
    {
        var repositoryRoot = CreateTemporaryDirectory();

        try
        {
            RunGit(repositoryRoot, "init");
            RunGit(repositoryRoot, "config user.email codex@example.test");
            RunGit(repositoryRoot, "config user.name Codex");

            Directory.CreateDirectory(Path.Combine(repositoryRoot, "packages", "qaas", "QaaS.Runner", "1.0.0"));
            File.WriteAllText(Path.Combine(repositoryRoot, "packages", "qaas", "QaaS.Runner", "1.0.0", "QaaS.Runner.nupkg"), "v1");
            RunGit(repositoryRoot, "add .");
            RunGit(repositoryRoot, "commit -m initial");

            File.WriteAllText(Path.Combine(repositoryRoot, "README.md"), "second commit");
            RunGit(repositoryRoot, "add README.md");
            RunGit(repositoryRoot, "commit -m second");

            var resolveMethod = PublishMirrorReleaseCommandType
                .GetMethod("ResolvePreviousPackagesGitRef", BindingFlags.Static | BindingFlags.NonPublic)!;

            var cleanGitRef = (string?)resolveMethod.Invoke(null, [repositoryRoot]);
            Assert.Equal("HEAD^", cleanGitRef);

            File.WriteAllText(Path.Combine(repositoryRoot, "packages", "qaas", "QaaS.Runner", "1.0.0", "QaaS.Runner.nupkg"), "v2");

            var dirtyGitRef = (string?)resolveMethod.Invoke(null, [repositoryRoot]);
            Assert.Equal("HEAD", dirtyGitRef);
        }
        finally
        {
            DeleteTemporaryDirectory(repositoryRoot);
        }
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Failed to start git.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"git {arguments} failed in '{workingDirectory}'.{Environment.NewLine}{standardOutput}{Environment.NewLine}{standardError}");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "qaas-package-mirror-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
