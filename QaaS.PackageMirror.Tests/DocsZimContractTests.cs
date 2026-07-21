using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace QaaS.PackageMirror.Tests;

public class DocsZimContractTests
{
    private const string ProvenanceFileName = "qaas-docs-zim-provenance.json";

    [Fact]
    public void SyncDocsZimProvenance_WritesCanonicalContractAndCheckAcceptsIt()
    {
        var docsRoot = CreateTemporaryDirectory();

        try
        {
            var writeResult = RunTool(
                $"sync-docs-zim-provenance --docs-root \"{docsRoot}\" --docs-updated-date-utc 2026-07-13"
            );

            Assert.Equal(0, writeResult.ExitCode);
            var provenancePath = Path.Combine(docsRoot, ProvenanceFileName);
            var json = File.ReadAllText(provenancePath);
            var provenance = JsonNode.Parse(json)!.AsObject();
            var zim = provenance["zim"]!.AsObject();

            Assert.Equal(1, provenance["schemaVersion"]!.GetValue<int>());
            Assert.Equal("2026-07-13", provenance["docsUpdatedDateUtc"]!.GetValue<string>());
            Assert.Equal("QaaS Documantation", zim["name"]!.GetValue<string>());
            Assert.Equal("Complete QaaS Documantation", zim["title"]!.GetValue<string>());
            Assert.Equal("2026-07-13", zim["description"]!.GetValue<string>());
            Assert.Equal("qaas-docs.zim", zim["fileName"]!.GetValue<string>());
            Assert.DoesNotContain("\r\n", json);

            var checkResult = RunTool(
                $"sync-docs-zim-provenance --docs-root \"{docsRoot}\" --check"
            );
            Assert.Equal(0, checkResult.ExitCode);
            Assert.Contains("Validated docs ZIM provenance:", checkResult.StandardOutput);
        }
        finally
        {
            Directory.Delete(docsRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("name", "Other name", "zim.name")]
    [InlineData("title", "Other title", "zim.title")]
    [InlineData("description", "2026-07-12", "zim.description")]
    [InlineData("fileName", "qaas-docs-1.2.3.zim", "zim.fileName")]
    public void SyncDocsZimProvenance_CheckRejectsMetadataDrift(
        string propertyName,
        string value,
        string expectedErrorProperty
    )
    {
        var docsRoot = CreateTemporaryDirectory();

        try
        {
            Assert.Equal(
                0,
                RunTool(
                    $"sync-docs-zim-provenance --docs-root \"{docsRoot}\" --docs-updated-date-utc 2026-07-13"
                ).ExitCode
            );
            var provenancePath = Path.Combine(docsRoot, ProvenanceFileName);
            var provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!.AsObject();
            provenance["zim"]![propertyName] = value;
            File.WriteAllText(
                provenancePath,
                provenance.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            );

            var checkResult = RunTool(
                $"sync-docs-zim-provenance --docs-root \"{docsRoot}\" --check"
            );

            Assert.Equal(3, checkResult.ExitCode);
            Assert.Contains($"invalid '{expectedErrorProperty}'", checkResult.StandardError);
        }
        finally
        {
            Directory.Delete(docsRoot, recursive: true);
        }
    }

    [Fact]
    public void SyncDocsZimProvenance_RejectsNonCanonicalDate()
    {
        var docsRoot = CreateTemporaryDirectory();

        try
        {
            var result = RunTool(
                $"sync-docs-zim-provenance --docs-root \"{docsRoot}\" --docs-updated-date-utc 13-07-2026"
            );

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("yyyy-MM-dd", result.StandardError);
            Assert.False(File.Exists(Path.Combine(docsRoot, ProvenanceFileName)));
        }
        finally
        {
            Directory.Delete(docsRoot, recursive: true);
        }
    }

    [Fact]
    public void SyncWorkflow_PersistsDocsMetadataButPublishesOnlyTheZim()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "sync-packages.yml")
        );

        Assert.Contains("$workflowRun.created_at", workflow);
        Assert.Contains("ToUniversalTime().ToString(", workflow);
        Assert.Contains("'yyyy-MM-dd'", workflow);
        Assert.Contains("docs_updated_date_utc=$docsUpdatedDateUtc", workflow);
        Assert.Contains("'qaas-docs.zim'", workflow);
        Assert.Contains("sync-docs-zim-provenance", workflow);
        Assert.Contains("--check", workflow);
        Assert.Contains("steps.docs_zim.outputs.ready == 'true'", workflow);
        Assert.Contains("github.event_name == 'workflow_dispatch'", workflow);
        Assert.DoesNotContain("--pattern 'qaas-docs-zim-provenance.json'", workflow);
        Assert.DoesNotContain("--pattern 'qaas-docs-image.tgz'", workflow);
        Assert.DoesNotContain("Skipping this automatic mirror release", workflow);
        Assert.Contains(
            "canonical nested-list indentation owned by this PackageMirror workflow",
            workflow
        );
        Assert.Contains("write the fixed qaas-docs.zim provenance contract", workflow);
    }

    private static ProcessResult RunTool(string arguments)
    {
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" {arguments}",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start mirror tools CLI.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static string GetMirrorToolsDllPath(string repositoryRoot) =>
        Path.Combine(
            repositoryRoot,
            "QaaS.PackageMirror.Tools",
            "bin",
            "Release",
            "net10.0",
            "QaaS.PackageMirror.Tools.dll"
        );

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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "qaas-docs-zim-contract-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
