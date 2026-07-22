using Xunit;

namespace QaaS.PackageMirror.Tests;

public class WorkflowContractTests
{
    private static readonly string[] SourceArchiveFolders =
    [
        "qaas-framework-source",
        "qaas-mocker-communicationobjects-source",
        "qaas-runner-source",
        "qaas-mocker-source",
        "qaas-common-assertions-source",
        "qaas-common-generators-source",
        "qaas-common-probes-source",
        "qaas-common-processors-source",
    ];

    [Fact]
    public void ManualDispatch_DefaultsToAFullReleaseAndDocsSync()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("default: true", GetBlock(workflow, "      publish_release:"));
        Assert.Contains("default: true", GetBlock(workflow, "      create_docs_pr:"));
        Assert.Contains("default: false", GetBlock(workflow, "      docs_drift_check_only:"));

        var releaseStep = GetBlock(workflow, "      - name: Publish mirror release");
        Assert.Contains("github.event_name == 'workflow_dispatch'", releaseStep);
        Assert.Contains("inputs.publish_release", releaseStep);
        Assert.Contains("!inputs.docs_drift_check_only", releaseStep);
        Assert.Contains("steps.docs_zim.outputs.ready == 'true'", releaseStep);
        Assert.Contains("publish-mirror-release", releaseStep);
        Assert.Contains("--source-archives-root", releaseStep);
        Assert.Contains("--docs-zim-root", releaseStep);
        Assert.DoesNotContain("--previous-packages-root", workflow);
        Assert.DoesNotContain("new-deps-packages.zip", workflow);

        var docsDecisionStep = GetBlock(workflow, "      - name: Decide whether to sync docs");
        Assert.Contains("inputs.create_docs_pr", docsDecisionStep);
        Assert.Contains("inputs.docs_drift_check_only", docsDecisionStep);
    }

    [Fact]
    public void PushPath_RemainsFastButStillBuildsAndTestsEverything()
    {
        var workflow = ReadWorkflow();

        Assert.Contains("branches:\n      - master", workflow);
        Assert.Contains("- QaaS.PackageMirror.Tests/**", workflow);
        Assert.DoesNotContain("if:", GetBlock(workflow, "      - name: Build mirror solution"));
        Assert.DoesNotContain("if:", GetBlock(workflow, "      - name: Test mirror solution"));

        foreach (
            var stepName in new[]
            {
                "Refresh current branch before update",
                "Rebuild mirror contents",
                "Validate mirror outputs",
            }
        )
        {
            Assert.Contains(
                "if: github.event_name == 'workflow_dispatch'",
                GetBlock(workflow, $"      - name: {stepName}")
            );
        }
    }

    [Fact]
    public void FullSync_RetainsMirrorSourceReleaseAndOfflineDocsContracts()
    {
        var workflow = ReadWorkflow();

        var rebuildStep = GetBlock(workflow, "      - name: Rebuild mirror contents");
        Assert.Contains("sync-restored-packages", rebuildStep);
        Assert.Contains("--github-token", rebuildStep);

        var validationStep = GetBlock(workflow, "      - name: Validate mirror outputs");
        foreach (
            var requiredOutput in new[]
            {
                "packages/qaas",
                "packages/not-qaas",
                "schemas/runner-family/latest/schema.json",
                "schemas/runner-family/latest/docs-manifest.json",
                "schemas/runner-family/latest/hook-catalog.json",
                "schemas/mocker-family/latest/schema.json",
                "schemas/mocker-family/latest/docs-manifest.json",
                "schemas/mocker-family/latest/hook-catalog.json",
            }
        )
        {
            Assert.Contains(requiredOutput, validationStep);
        }

        var sourceStep = GetBlock(workflow, "      - name: Prepare sanitized source archives");
        foreach (var sourceFolder in SourceArchiveFolders)
        {
            Assert.Contains(sourceFolder, sourceStep);
        }
        Assert.Contains("qaas-source-code.zip", sourceStep);
        Assert.Contains("Test-CiPath", sourceStep);

        var offlineDocsStep = GetBlock(workflow, "      - name: Download latest qaas-docs ZIM");
        Assert.Contains("--pattern 'qaas-docs.zim'", offlineDocsStep);
        Assert.DoesNotContain("qaas-docs-zim-provenance.json", offlineDocsStep);
        Assert.DoesNotContain("qaas-docs-image.tgz", offlineDocsStep);
        Assert.Contains("gh release download", offlineDocsStep);
        Assert.Contains("gh run download", offlineDocsStep);
        Assert.DoesNotContain("sync-docs-zim-provenance", offlineDocsStep);
    }

    [Fact]
    public void DocsSync_RetainsGenerationValidationAndPullRequestStages()
    {
        var workflow = ReadWorkflow();

        foreach (
            var stepName in new[]
            {
                "Resolve docs source refs",
                "Checkout qaas-docs",
                "Use latest docs generator",
                "Checkout runner source",
                "Checkout mocker source",
                "Checkout framework source",
                "Checkout assertions source",
                "Checkout generators source",
                "Checkout probes source",
                "Checkout processors source",
                "Regenerate or check docs from mirrored source refs",
                "Write or validate docs ZIM provenance",
                "Validate generated docs v2 contract",
                "Validate generated docs PR size",
                "Commit docs updates",
                "Push docs feature branch",
                "Create docs pull request",
            }
        )
        {
            Assert.Contains($"      - name: {stepName}", workflow);
        }

        var generationStep = GetBlock(
            workflow,
            "      - name: Regenerate or check docs from mirrored source refs"
        );
        Assert.Contains("generate-reference-docs", generationStep);
        Assert.Contains("--build-site", generationStep);
        Assert.Contains("$generatorArguments += '--check'", generationStep);
        Assert.Contains("restore -- Snapshots", generationStep);
        Assert.DoesNotContain("*-sections", generationStep);

        var docsValidationStep = GetBlock(
            workflow,
            "      - name: Validate generated docs v2 contract"
        );
        foreach (
            var validator in new[]
            {
                "check-frontmatter.sh",
                "markdownlint-cli2",
                "check-verification-markers.py",
                "check-reference-skeleton.py",
                "check-page-headings.py",
                "check-heading-anchors.py",
                "check-example-density.py",
                "check-nav-structure.py",
                "test_check_yaml_indentation.py",
                "check_yaml_indentation.py",
                "validate-yaml-snippets.sh",
            }
        )
        {
            Assert.Contains(validator, docsValidationStep);
        }
        Assert.Contains("docs/_meta", docsValidationStep);
        Assert.Contains("ai_summary", docsValidationStep);

        var pullRequestStep = GetBlock(workflow, "      - name: Create docs pull request");
        Assert.Contains("gh pr create", pullRequestStep);
        Assert.Contains("--repo TheSmokeTeam/qaas-docs", pullRequestStep);
        Assert.Contains("--base master", pullRequestStep);
    }

    private static string ReadWorkflow()
    {
        var repositoryRoot = FindRepositoryRoot();
        return File.ReadAllText(
                Path.Combine(repositoryRoot, ".github", "workflows", "sync-packages.yml")
            )
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string GetBlock(string workflow, string header)
    {
        var start = workflow.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find workflow block '{header.Trim()}'.");

        var headerIndent = header.Length - header.TrimStart().Length;
        var nextLine = workflow.IndexOf('\n', start);
        if (nextLine < 0)
        {
            return workflow[start..];
        }

        var end = nextLine + 1;
        while (end < workflow.Length)
        {
            var followingLine = workflow.IndexOf('\n', end);
            if (followingLine < 0)
            {
                followingLine = workflow.Length;
            }

            var line = workflow[end..followingLine];
            if (
                !string.IsNullOrWhiteSpace(line)
                && line.Length - line.TrimStart().Length <= headerIndent
            )
            {
                break;
            }

            end = followingLine + 1;
        }

        return workflow[start..Math.Min(end, workflow.Length)];
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

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
