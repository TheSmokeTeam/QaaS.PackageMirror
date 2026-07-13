using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using NJsonSchema;
using Xunit;

namespace QaaS.PackageMirror.Tests;

public class IntegrationTests
{
    private static readonly string[] FamilyIds = ["runner-family", "mocker-family"];

    private static readonly string[] FamilyJsonFileNames =
    [
        "schema.json",
        "docs-manifest.json",
        "hook-catalog.json",
    ];

    private static readonly string[] RequiredSourceArchiveFolderNames =
    [
        "qaas-common-assertions-source",
        "qaas-common-generators-source",
        "qaas-common-probes-source",
        "qaas-common-processors-source",
        "qaas-framework-source",
        "qaas-mocker-source",
        "qaas-mocker-communicationobjects-source",
        "qaas-runner-source",
    ];

    [Fact]
    public async Task FamilySchemaGenerator_AllowsCustomHooksDuplicateGeneratorsAndNamedServerTypes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var outputRoot = CreateTemporaryDirectory();

        try
        {
            await RunFamilySchemaGenerator(repositoryRoot, "mocker-family", outputRoot);
            await RunFamilySchemaGenerator(repositoryRoot, "runner-family", outputRoot);

            var mockerSchema = await JsonSchema.FromFileAsync(
                Path.Combine(outputRoot, "mocker-family", "latest", "schema.json")
            );
            var runnerSchema = await JsonSchema.FromFileAsync(
                Path.Combine(outputRoot, "runner-family", "latest", "schema.json")
            );
            var runnerDocsManifest = JsonNode.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(outputRoot, "runner-family", "latest", "docs-manifest.json")
                )
            )!;

            var dataSourcesSchema = mockerSchema.Properties["DataSources"];
            var stubsSchema = mockerSchema.Properties["Stubs"];
            var serverSchema = mockerSchema.Properties["Server"];
            var serversSchema = mockerSchema.Properties["Servers"];
            var assertionsSchema = runnerSchema.Properties["Assertions"];
            var reportersSchema = runnerSchema.Properties["Reporters"];
            var probesSchema = ResolveArrayItemSchema(
                runnerSchema.Properties["Sessions"]
            ).Properties["Probes"];
            var generatorSelector = ResolveArrayItemSchema(dataSourcesSchema).Properties[
                "Generator"
            ];
            var processorSelector = ResolveArrayItemSchema(stubsSchema).Properties["Processor"];
            var assertionSelector = ResolveArrayItemSchema(assertionsSchema).Properties[
                "Assertion"
            ];
            var probeSelector = ResolveArrayItemSchema(probesSchema).Properties["Probe"];
            var sessionSchema = ResolveArrayItemSchema(runnerSchema.Properties["Sessions"]);
            var storageSchema = ResolveArrayItemSchema(runnerSchema.Properties["Storages"]);

            Assert.False(serverSchema.Properties.ContainsKey("Type"));
            Assert.False(ResolveArrayItemSchema(serversSchema).Properties.ContainsKey("Type"));
            AssertKnownValuesContainOnlySimpleNames(generatorSelector);
            AssertKnownValuesContainOnlySimpleNames(processorSelector);
            AssertKnownValuesContainOnlySimpleNames(assertionSelector);
            AssertKnownValuesContainOnlySimpleNames(probeSelector);
            AssertNoEnumSuggestionsContainNumericValues(runnerSchema);
            AssertNoEnumSuggestionsContainNumericValues(mockerSchema);
            AssertRunnerDocsManifestContainsSectionFor(
                runnerDocsManifest,
                reportersSchema,
                "Reporters"
            );

            Assert.Equal(
                "Optional stage number that decides when the runner waits for this session to complete. If omitted, the session becomes visible only after its own stage completes. If set, the runner defers waiting until the configured future stage is reached.",
                sessionSchema.Properties["RunUntilStage"].Description
            );
            Assert.Equal(
                "Optional per-stage configuration for the session's internal action stages. Use this to override timing around a specific stage number without changing the action order.",
                sessionSchema.Properties["Stages"].Description
            );
            Assert.Equal(
                "The internal session stage number this configuration applies to.",
                ResolveArrayItemSchema(sessionSchema.Properties["Stages"])
                    .Properties["StageNumber"]
                    .Description
            );
            Assert.DoesNotContain(
                "ProcessorSpecificConfiguration",
                ResolveArrayItemSchema(stubsSchema).Properties.Keys
            );
            Assert.Contains(
                "ProcessorConfiguration",
                ResolveArrayItemSchema(stubsSchema).Properties.Keys
            );
            Assert.DoesNotContain(
                "ProcessorSpecificConfiguration",
                ResolveArrayItemSchema(stubsSchema)
                    .AnyOf.SelectMany(branch => branch.Properties.Keys)
            );
            Assert.Contains(
                ResolveArrayItemSchema(stubsSchema).AnyOf,
                branch => branch.Properties.ContainsKey("ProcessorConfiguration")
            );
            Assert.Contains("JsonStorageFormat", storageSchema.Properties.Keys);

            Assert.Empty(
                dataSourcesSchema.Validate(
                    """
                    [
                      {
                        "Name": "source-a",
                        "Generator": "Json",
                        "GeneratorConfiguration": {
                          "JsonDataSourceName": "payload-a"
                        }
                      },
                      {
                        "Name": "source-b",
                        "Generator": "Json",
                        "GeneratorConfiguration": {
                          "JsonDataSourceName": "payload-b"
                        }
                      },
                      {
                        "Name": "source-custom",
                        "Generator": "Contoso.CustomGenerator",
                        "GeneratorConfiguration": {
                          "Enabled": true
                        }
                      }
                    ]
                    """
                )
            );

            Assert.Empty(
                dataSourcesSchema.Validate(
                    """
                    [
                      {
                        "Name": "source-legacy",
                        "Generator": "QaaS.Common.Generators.JsonGenerators.Json",
                        "GeneratorConfiguration": {
                          "JsonDataSourceName": "payload-legacy"
                        }
                      }
                    ]
                    """
                )
            );

            Assert.Empty(
                stubsSchema.Validate(
                    """
                    [
                      {
                        "Name": "stub-custom",
                        "Processor": "Contoso.CustomProcessor",
                        "ProcessorConfiguration": {
                          "Retries": 2
                        }
                      }
                    ]
                    """
                )
            );

            Assert.Empty(
                assertionsSchema.Validate(
                    """
                    [
                      {
                        "Name": "custom-assertion",
                        "Assertion": "Contoso.CustomAssertion",
                        "AssertionConfiguration": {
                          "ExpectedStatus": "ok"
                        }
                      }
                    ]
                    """
                )
            );

            Assert.Empty(
                probesSchema.Validate(
                    """
                    [
                      {
                        "Name": "custom-probe",
                        "Probe": "Contoso.CustomProbe",
                        "ProbeConfiguration": {
                          "Threshold": 5
                        }
                      }
                    ]
                    """
                )
            );

            Assert.Empty(
                serverSchema.Validate(
                    """
                    {
                      "Http": {
                        "Port": 8080,
                        "Endpoints": [
                          {
                            "Path": "/health",
                            "Actions": [
                              {
                                "Method": "Get",
                                "TransactionStubName": "stub-custom",
                                "Name": "health"
                              }
                            ]
                          }
                        ]
                      }
                    }
                    """
                )
            );

            Assert.Empty(
                serversSchema.Validate(
                    """
                    [
                      {
                        "Http": {
                          "Port": 8080,
                          "Endpoints": [
                            {
                              "Path": "/health",
                              "Actions": [
                                {
                                  "Method": "Get",
                                  "TransactionStubName": "stub-custom",
                                  "Name": "health"
                                }
                              ]
                            }
                          ]
                        }
                      },
                      {
                        "Grpc": {
                          "Port": 5001,
                          "Services": [
                            {
                              "ServiceName": "Greeter",
                              "ProtoNamespace": "Contoso.Grpc",
                              "AssemblyName": "Contoso.Grpc",
                              "Actions": [
                                {
                                  "RpcName": "SayHello",
                                  "TransactionStubName": "stub-custom",
                                  "Name": "say-hello"
                                }
                              ]
                            }
                          ]
                        }
                      }
                    ]
                    """
                )
            );
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public void PublishMirrorRelease_StripsSourceFilesFromReleaseArchives()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            var qaasPackageRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "qaas",
                "QaaS.Sample",
                "1.0.0"
            );
            var notQaasPackageRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "not-qaas",
                "Other.Sample",
                "1.0.0"
            );
            Directory.CreateDirectory(Path.Combine(qaasPackageRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(qaasPackageRoot, "src"));
            Directory.CreateDirectory(
                Path.Combine(notQaasPackageRoot, "contentFiles", "cs", "any")
            );
            Directory.CreateDirectory(Path.Combine(notQaasPackageRoot, "build"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "state"));

            File.WriteAllText(
                Path.Combine(qaasPackageRoot, "lib", "net10.0", "QaaS.Sample.dll"),
                "binary"
            );
            File.WriteAllText(
                Path.Combine(qaasPackageRoot, "src", "Sample.cs"),
                "public class Sample {}"
            );
            File.WriteAllText(Path.Combine(qaasPackageRoot, "README.md"), "qaas readme");
            File.WriteAllText(
                Path.Combine(notQaasPackageRoot, "contentFiles", "cs", "any", "Helper.cs"),
                "public class Helper {}"
            );
            File.WriteAllText(
                Path.Combine(notQaasPackageRoot, "build", "Other.Sample.targets"),
                "<Project />"
            );
            File.WriteAllText(Path.Combine(notQaasPackageRoot, "README.md"), "dependency readme");
            CreateFamilySchemaContractFiles(workspaceRoot);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );
            Assert.True(
                result.ExitCode == 0,
                $"Release command failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            );

            var qaasZipPath = ExtractOutputPath(result.StandardOutput, "QaaS zip:");
            var notQaasZipPath = ExtractOutputPath(result.StandardOutput, "Not-QaaS zip:");
            var newDepsZipPath = ExtractOutputPath(result.StandardOutput, "New deps zip:");

            Assert.True(File.Exists(qaasZipPath));
            Assert.True(File.Exists(notQaasZipPath));
            Assert.True(File.Exists(newDepsZipPath));

            using var qaasArchive = ZipFile.OpenRead(qaasZipPath);
            using var notQaasArchive = ZipFile.OpenRead(notQaasZipPath);
            using var newDepsArchive = ZipFile.OpenRead(newDepsZipPath);
            var qaasEntries = qaasArchive.Entries.Select(entry => entry.FullName).ToArray();
            var notQaasEntries = notQaasArchive.Entries.Select(entry => entry.FullName).ToArray();
            var newDepsEntries = newDepsArchive.Entries.Select(entry => entry.FullName).ToArray();

            Assert.Contains("qaas/QaaS.Sample/1.0.0/lib/net10.0/QaaS.Sample.dll", qaasEntries);
            Assert.DoesNotContain(
                qaasEntries,
                entry =>
                    entry.Contains("Sample.cs", StringComparison.OrdinalIgnoreCase)
                    || entry.Contains("README", StringComparison.OrdinalIgnoreCase)
            );
            Assert.Contains(
                "not-qaas/Other.Sample/1.0.0/build/Other.Sample.targets",
                notQaasEntries
            );
            Assert.Contains(
                "new-deps/Other.Sample/1.0.0/build/Other.Sample.targets",
                newDepsEntries
            );
            Assert.DoesNotContain(
                notQaasEntries,
                entry =>
                    entry.Contains("contentFiles", StringComparison.OrdinalIgnoreCase)
                    || entry.Contains("README", StringComparison.OrdinalIgnoreCase)
                    || entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            );
            Assert.DoesNotContain(
                newDepsEntries,
                entry =>
                    entry.Contains("contentFiles", StringComparison.OrdinalIgnoreCase)
                    || entry.Contains("README", StringComparison.OrdinalIgnoreCase)
                    || entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            );
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void PublishMirrorRelease_IncludesSanitizedSourceArchives()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);

            var sourceArchivesRoot = CreateCombinedSourceArchive(workspaceRoot);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --source-archives-root \"{sourceArchivesRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );
            Assert.True(
                result.ExitCode == 0,
                $"Release command failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            );

            Assert.Contains("Source archives included: 1", result.StandardOutput);
            Assert.Contains("Source archive:", result.StandardOutput);
            Assert.Contains("qaas-source-code.zip", result.StandardOutput);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_IncludesDocsOfflineBundleAssets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);
            var docsZimRoot = Path.Combine(workspaceRoot, "docs-zim");
            Directory.CreateDirectory(docsZimRoot);
            var docsZimPath = Path.Combine(docsZimRoot, "qaas-docs-2.1.2.zim");
            File.WriteAllText(docsZimPath, "zim");
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs-image.tgz"), "image");
            WriteDocsZimProvenance(repositoryRoot, docsZimRoot);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --docs-zim-root \"{docsZimRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );
            Assert.True(
                result.ExitCode == 0,
                $"Release command failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            );

            var docsZimAssetPaths = ExtractOutputPaths(result.StandardOutput, "Docs ZIM asset:");

            Assert.Contains("Docs ZIM assets included: 1", result.StandardOutput);
            Assert.Contains("Docs ZIM provenance assets included: 1", result.StandardOutput);
            Assert.Contains("Docs image assets included: 1", result.StandardOutput);
            Assert.Contains("Release assets included: 8", result.StandardOutput);
            Assert.Single(docsZimAssetPaths);
            Assert.Equal("qaas-docs.zim", Path.GetFileName(docsZimAssetPaths[0]));
            Assert.True(File.Exists(docsZimAssetPaths[0]));
            var provenanceAssetPath = ExtractOutputPath(
                result.StandardOutput,
                "Docs ZIM provenance asset:"
            );
            Assert.Equal("qaas-docs-zim-provenance.json", Path.GetFileName(provenanceAssetPath));
            Assert.True(File.Exists(provenanceAssetPath));
            var imageAssetPath = ExtractOutputPath(result.StandardOutput, "Docs image asset:");
            Assert.Equal("qaas-docs-image.tgz", Path.GetFileName(imageAssetPath));
            Assert.Equal("image", File.ReadAllText(imageAssetPath));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_RejectsAmbiguousDocsZimAssets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);
            var docsZimRoot = Path.Combine(workspaceRoot, "docs-zim");
            Directory.CreateDirectory(docsZimRoot);
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs-2.1.1.zim"), "old");
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs-2.1.2.zim"), "new");
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs-image.tgz"), "image");
            WriteDocsZimProvenance(repositoryRoot, docsZimRoot);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --docs-zim-root \"{docsZimRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("must contain exactly one .zim file", result.StandardError);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_RejectsMissingDocsImageArchive()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);
            var docsZimRoot = Path.Combine(workspaceRoot, "docs-zim");
            Directory.CreateDirectory(docsZimRoot);
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs.zim"), "zim");
            WriteDocsZimProvenance(repositoryRoot, docsZimRoot);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --docs-zim-root \"{docsZimRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("must contain exactly one .tgz image archive", result.StandardError);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_RejectsAmbiguousDocsImageArchives()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);
            var docsZimRoot = Path.Combine(workspaceRoot, "docs-zim");
            Directory.CreateDirectory(docsZimRoot);
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs.zim"), "zim");
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs-image-old.tgz"), "old");
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs-image.tgz"), "new");
            WriteDocsZimProvenance(repositoryRoot, docsZimRoot);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --docs-zim-root \"{docsZimRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("must contain exactly one .tgz image archive", result.StandardError);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_RejectsInvalidDocsZimProvenance()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);
            var docsZimRoot = Path.Combine(workspaceRoot, "docs-zim");
            Directory.CreateDirectory(docsZimRoot);
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs.zim"), "zim");
            File.WriteAllText(Path.Combine(docsZimRoot, "qaas-docs-image.tgz"), "image");
            WriteDocsZimProvenance(repositoryRoot, docsZimRoot);

            var provenancePath = Path.Combine(docsZimRoot, "qaas-docs-zim-provenance.json");
            var provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!.AsObject();
            provenance["zim"]!["title"] = "Wrong title";
            File.WriteAllText(provenancePath, provenance.ToJsonString());

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --docs-zim-root \"{docsZimRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("invalid 'zim.title'", result.StandardError);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_RequiresDocsZimRootWhenPublishing()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --github-token fake-token"
            );

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "--docs-zim-root is required when publishing a mirror release",
                result.StandardError
            );
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_IncludesOnlyFamilySchemaAssets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );
            Assert.True(
                result.ExitCode == 0,
                $"Release command failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            );

            var schemaAssetPaths = ExtractOutputPaths(result.StandardOutput, "Schema asset:");
            var schemaAssetNames = schemaAssetPaths
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var expectedSchemaAssetNames = new[]
            {
                "mocker-family-schema.json",
                "runner-family-schema.json",
            };

            Assert.Contains("Schema assets included: 2", result.StandardOutput);
            Assert.Equal(expectedSchemaAssetNames, schemaAssetNames);
            Assert.All(schemaAssetPaths, path => Assert.True(File.Exists(path)));
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_RejectsSourceArchiveMissingTrackedRepositorySource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);
            var sourceArchivesRoot = CreateCombinedSourceArchive(
                workspaceRoot,
                "qaas-runner-source"
            );

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --source-archives-root \"{sourceArchivesRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("missing source folders", result.StandardError);
            Assert.Contains("qaas-runner-source", result.StandardError);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_RejectsMultipleSourceArchives()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);

            var sourceArchivesRoot = Path.Combine(workspaceRoot, "source-code-zips");
            var sourceRoot = Path.Combine(workspaceRoot, "source", "QaaS.Framework");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(Path.Combine(sourceRoot, "Component.txt"), "component");
            CreateZipArchive(sourceRoot, Path.Combine(sourceArchivesRoot, "source-a.zip"));
            CreateZipArchive(sourceRoot, Path.Combine(sourceArchivesRoot, "source-b.zip"));

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --source-archives-root \"{sourceArchivesRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("must contain one combined source archive", result.StandardError);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_RejectsSourceArchivesWithCiPaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            CreateMinimalReleaseWorkspace(workspaceRoot);

            var sourceArchivesRoot = Path.Combine(workspaceRoot, "source-code-zips");
            var sourceRoot = Path.Combine(workspaceRoot, "source", "QaaS.Framework");
            Directory.CreateDirectory(Path.Combine(sourceRoot, ".github", "workflows"));
            File.WriteAllText(
                Path.Combine(sourceRoot, ".github", "workflows", "ci.yml"),
                "name: CI"
            );
            CreateZipArchive(
                sourceRoot,
                Path.Combine(sourceArchivesRoot, "qaas-framework-source.zip")
            );

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --source-archives-root \"{sourceArchivesRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("contains CI path", result.StandardError);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PackageMirrorCli_ExcludesConfigurationAndTemplatePackagesFromMirrorAndState()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();
        var artifactRoot = Path.Combine(workspaceRoot, "artifact");

        try
        {
            var runnerPackageRoot = Path.Combine(artifactRoot, "QaaS.Runner", "4.5.1");
            var configurationPackageRoot = Path.Combine(
                artifactRoot,
                "QaaS.Configuration",
                "1.0.1"
            );
            var runnerTemplatePackageRoot = Path.Combine(
                artifactRoot,
                "QaaS.Runner.Template",
                "1.4.0"
            );
            var mockerTemplatePackageRoot = Path.Combine(
                artifactRoot,
                "QaaS.Mocker.Template",
                "1.4.0"
            );
            var dependencyPackageRoot = Path.Combine(artifactRoot, "Other.Sample", "1.0.0");

            Directory.CreateDirectory(runnerPackageRoot);
            Directory.CreateDirectory(configurationPackageRoot);
            Directory.CreateDirectory(runnerTemplatePackageRoot);
            Directory.CreateDirectory(mockerTemplatePackageRoot);
            Directory.CreateDirectory(dependencyPackageRoot);

            File.WriteAllText(Path.Combine(runnerPackageRoot, "QaaS.Runner.4.5.1.nupkg"), "runner");
            File.WriteAllText(
                Path.Combine(configurationPackageRoot, "QaaS.Configuration.1.0.1.nupkg"),
                "configuration"
            );
            File.WriteAllText(
                Path.Combine(runnerTemplatePackageRoot, "QaaS.Runner.Template.1.4.0.nupkg"),
                "runner-template"
            );
            File.WriteAllText(
                Path.Combine(mockerTemplatePackageRoot, "QaaS.Mocker.Template.1.4.0.nupkg"),
                "mocker-template"
            );
            File.WriteAllText(
                Path.Combine(dependencyPackageRoot, "Other.Sample.1.0.0.nupkg"),
                "dependency"
            );

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorCliDllPath(repositoryRoot)}\" --artifact-root \"{artifactRoot}\" --mirror-root \"{workspaceRoot}\" --source-repo \"TheSmokeTeam/QaaS.Runner\" --source-tag \"4.5.1\" --origin \"https://example.test/run\" --source-run-id \"123\" --reset-packages",
                repositoryRoot
            );

            Assert.True(
                result.ExitCode == 0,
                $"Mirror CLI failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            );

            Assert.True(
                Directory.Exists(Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Runner"))
            );
            Assert.True(
                Directory.Exists(
                    Path.Combine(workspaceRoot, "packages", "not-qaas", "Other.Sample")
                )
            );
            Assert.False(
                Directory.Exists(
                    Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Configuration")
                )
            );
            Assert.False(
                Directory.Exists(
                    Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Runner.Template")
                )
            );
            Assert.False(
                Directory.Exists(
                    Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Mocker.Template")
                )
            );

            var state = File.ReadAllText(
                Path.Combine(workspaceRoot, "state", "TheSmokeTeam_QaaS.Runner.json")
            );
            Assert.DoesNotContain("QaaS.Configuration", state, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "QaaS.Runner.Template",
                state,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.DoesNotContain(
                "QaaS.Mocker.Template",
                state,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.Contains("QaaS.Runner", state, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Other.Sample", state, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_UsesFullQaasBootstrapFullDependenciesAndNewDepsDiff()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();
        var previousWorkspaceRoot = CreateTemporaryDirectory();

        try
        {
            var previousQaasRoot = Path.Combine(
                previousWorkspaceRoot,
                "packages",
                "qaas",
                "QaaS.Runner",
                "1.0.0"
            );
            var previousNotQaasRoot = Path.Combine(
                previousWorkspaceRoot,
                "packages",
                "not-qaas",
                "Other.Sample",
                "1.0.0"
            );
            Directory.CreateDirectory(Path.Combine(previousQaasRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(previousNotQaasRoot, "build"));
            File.WriteAllText(
                Path.Combine(previousQaasRoot, "lib", "net10.0", "QaaS.Runner.dll"),
                "old-binary"
            );
            File.WriteAllText(
                Path.Combine(previousNotQaasRoot, "build", "Other.Sample.targets"),
                "<Project />"
            );

            var currentQaasExistingRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "qaas",
                "QaaS.Runner",
                "1.0.0"
            );
            var currentQaasNewRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "qaas",
                "QaaS.Runner",
                "2.0.0"
            );
            var currentElasticBootstrapRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "qaas",
                "QaaS.ElasticBootstrap",
                "1.0.0"
            );
            var currentConfigurationRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "qaas",
                "QaaS.Configuration",
                "1.0.1"
            );
            var currentNotQaasExistingRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "not-qaas",
                "Other.Sample",
                "1.0.0"
            );
            var currentNotQaasNewRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "not-qaas",
                "Other.Sample",
                "1.1.0"
            );
            Directory.CreateDirectory(Path.Combine(currentQaasExistingRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(currentQaasNewRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(currentElasticBootstrapRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(currentConfigurationRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(currentNotQaasExistingRoot, "build"));
            Directory.CreateDirectory(Path.Combine(currentNotQaasNewRoot, "build"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "state"));

            File.WriteAllText(
                Path.Combine(currentQaasExistingRoot, "lib", "net10.0", "QaaS.Runner.dll"),
                "existing-binary"
            );
            File.WriteAllText(
                Path.Combine(currentQaasNewRoot, "lib", "net10.0", "QaaS.Runner.dll"),
                "new-binary"
            );
            File.WriteAllText(
                Path.Combine(
                    currentElasticBootstrapRoot,
                    "lib",
                    "net10.0",
                    "QaaS.ElasticBootstrap.dll"
                ),
                "elastic"
            );
            File.WriteAllText(
                Path.Combine(currentConfigurationRoot, "lib", "net10.0", "QaaS.Configuration.dll"),
                "configuration"
            );
            File.WriteAllText(
                Path.Combine(currentNotQaasExistingRoot, "build", "Other.Sample.targets"),
                "<Project />"
            );
            File.WriteAllText(
                Path.Combine(currentNotQaasNewRoot, "build", "Other.Sample.targets"),
                "<Project Version=\"1.1.0\" />"
            );
            CreateFamilySchemaContractFiles(workspaceRoot);
            File.WriteAllText(
                Path.Combine(workspaceRoot, "state", "TheSmokeTeam_QaaS.Runner.json"),
                """
                {
                  "repository": "TheSmokeTeam/QaaS.Runner",
                  "packages": [
                    {
                      "name": "QaaS.Runner",
                      "version": "2.0.0"
                    }
                  ]
                }
                """
            );

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --previous-packages-root \"{previousWorkspaceRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );
            Assert.True(
                result.ExitCode == 0,
                $"Release command failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            );

            var qaasZipPath = ExtractOutputPath(result.StandardOutput, "QaaS zip:");
            var notQaasZipPath = ExtractOutputPath(result.StandardOutput, "Not-QaaS zip:");
            var newDepsZipPath = ExtractOutputPath(result.StandardOutput, "New deps zip:");
            var notesPath = ExtractOutputPath(result.StandardOutput, "Notes file:");

            using var qaasArchive = ZipFile.OpenRead(qaasZipPath);
            using var notQaasArchive = ZipFile.OpenRead(notQaasZipPath);
            using var newDepsArchive = ZipFile.OpenRead(newDepsZipPath);
            var qaasEntries = qaasArchive.Entries.Select(entry => entry.FullName).ToArray();
            var notQaasEntries = notQaasArchive.Entries.Select(entry => entry.FullName).ToArray();
            var newDepsEntries = newDepsArchive.Entries.Select(entry => entry.FullName).ToArray();
            var notes = File.ReadAllText(notesPath);

            Assert.Contains("qaas/QaaS.Runner/2.0.0/lib/net10.0/QaaS.Runner.dll", qaasEntries);
            Assert.Contains("qaas/QaaS.Runner/1.0.0/lib/net10.0/QaaS.Runner.dll", qaasEntries);
            Assert.DoesNotContain(
                qaasEntries,
                entry => entry.Contains("QaaS.ElasticBootstrap", StringComparison.OrdinalIgnoreCase)
            );
            Assert.DoesNotContain(
                qaasEntries,
                entry => entry.Contains("QaaS.Configuration", StringComparison.OrdinalIgnoreCase)
            );
            Assert.Contains(
                "not-qaas/Other.Sample/1.1.0/build/Other.Sample.targets",
                notQaasEntries
            );
            Assert.Contains(
                "not-qaas/Other.Sample/1.0.0/build/Other.Sample.targets",
                notQaasEntries
            );
            Assert.Contains(
                "new-deps/Other.Sample/1.1.0/build/Other.Sample.targets",
                newDepsEntries
            );
            Assert.DoesNotContain(
                "new-deps/Other.Sample/1.0.0/build/Other.Sample.targets",
                newDepsEntries
            );
            Assert.Contains(
                "New Not-QaaS dependency package versions included: 1",
                result.StandardOutput
            );
            Assert.Contains("QaaS.Runner version 2.0.0", notes);
            Assert.DoesNotContain("QaaS.Runner version 1.0.0", notes);
            Assert.DoesNotContain(
                "QaaS.ElasticBootstrap",
                notes,
                StringComparison.OrdinalIgnoreCase
            );
            Assert.DoesNotContain("QaaS.Configuration", notes, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
            Directory.Delete(previousWorkspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void PublishMirrorRelease_ExcludesTemplatePackages()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            var templatePackageRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "qaas",
                "QaaS.Runner.Template",
                "1.3.1"
            );
            var notQaasPackageRoot = Path.Combine(
                workspaceRoot,
                "packages",
                "not-qaas",
                "Other.Sample",
                "1.0.0"
            );
            Directory.CreateDirectory(templatePackageRoot);
            Directory.CreateDirectory(
                Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Runner", "4.1.1")
            );
            Directory.CreateDirectory(notQaasPackageRoot);
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "state"));

            File.WriteAllText(
                Path.Combine(templatePackageRoot, "QaaS.Runner.Template.1.3.1.nupkg"),
                "template-package"
            );
            File.WriteAllText(
                Path.Combine(templatePackageRoot, "QaaS.Runner.Template.1.3.1.snupkg"),
                "template-symbol-package"
            );
            File.WriteAllText(
                Path.Combine(
                    Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Runner", "4.1.1"),
                    "QaaS.Runner.4.1.1.nupkg"
                ),
                "runner-package"
            );
            File.WriteAllText(
                Path.Combine(
                    Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Runner", "4.1.1"),
                    "QaaS.Runner.4.1.1.snupkg"
                ),
                "runner-symbol-package"
            );
            File.WriteAllText(
                Path.Combine(notQaasPackageRoot, "Other.Sample.1.0.0.nupkg"),
                "dependency-package"
            );
            CreateFamilySchemaContractFiles(workspaceRoot);
            File.WriteAllText(
                Path.Combine(workspaceRoot, "state", "TheSmokeTeam_QaaS.Runner.Template.json"),
                """
                {
                  "repository": "TheSmokeTeam/QaaS.Runner.Template",
                  "packages": [
                    {
                      "name": "QaaS.Runner.Template",
                      "version": "1.3.1"
                    }
                  ]
                }
                """
            );
            File.WriteAllText(
                Path.Combine(workspaceRoot, "state", "TheSmokeTeam_QaaS.Runner.json"),
                """
                {
                  "repository": "TheSmokeTeam/QaaS.Runner",
                  "packages": [
                    {
                      "name": "QaaS.Runner",
                      "version": "4.1.1"
                    }
                  ]
                }
                """
            );

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );
            Assert.True(
                result.ExitCode == 0,
                $"Release command failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            );

            var qaasZipPath = ExtractOutputPath(result.StandardOutput, "QaaS zip:");

            using var qaasArchive = ZipFile.OpenRead(qaasZipPath);
            var templateEntries = qaasArchive
                .Entries.Select(entry => entry.FullName)
                .Where(entry =>
                    entry.StartsWith("qaas/QaaS.Runner.Template/1.3.1/", StringComparison.Ordinal)
                )
                .Where(entry => !entry.EndsWith("/", StringComparison.Ordinal))
                .OrderBy(entry => entry, StringComparer.Ordinal)
                .ToArray();

            Assert.Empty(templateEntries);
            Assert.DoesNotContain(
                "QaaS.Runner.Template",
                result.StandardOutput,
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_UsesHeadAsBaselineWhenPackagesAreDirtyWithoutExplicitPreviousRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            InitializeGitReleaseWorkspace(workspaceRoot, commitNewDependencyVersion: false);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );
            Assert.True(
                result.ExitCode == 0,
                $"Release command failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            );

            var notQaasZipPath = ExtractOutputPath(result.StandardOutput, "Not-QaaS zip:");
            var newDepsZipPath = ExtractOutputPath(result.StandardOutput, "New deps zip:");
            using var notQaasArchive = ZipFile.OpenRead(notQaasZipPath);
            using var newDepsArchive = ZipFile.OpenRead(newDepsZipPath);
            var notQaasEntries = notQaasArchive.Entries.Select(entry => entry.FullName).ToArray();
            var newDepsEntries = newDepsArchive.Entries.Select(entry => entry.FullName).ToArray();

            Assert.Contains(
                "not-qaas/Other.Sample/1.1.0/build/Other.Sample.targets",
                notQaasEntries
            );
            Assert.Contains(
                "not-qaas/Other.Sample/1.0.0/build/Other.Sample.targets",
                notQaasEntries
            );
            Assert.Contains(
                "new-deps/Other.Sample/1.1.0/build/Other.Sample.targets",
                newDepsEntries
            );
            Assert.DoesNotContain(
                "new-deps/Other.Sample/1.0.0/build/Other.Sample.targets",
                newDepsEntries
            );
            Assert.Contains(
                "Not-QaaS dependency package versions included: 2",
                result.StandardOutput
            );
            Assert.Contains(
                "New Not-QaaS dependency package versions included: 1",
                result.StandardOutput
            );
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    [Fact]
    public void PublishMirrorRelease_UsesHeadParentAsBaselineWhenPackagesAreCleanWithoutExplicitPreviousRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();

        try
        {
            InitializeGitReleaseWorkspace(workspaceRoot, commitNewDependencyVersion: true);

            var result = RunProcess(
                "dotnet",
                $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" publish-mirror-release --workspace-root \"{workspaceRoot}\" --github-repository \"TheSmokeTeam/QaaS.PackageMirror\" --skip-publish"
            );
            Assert.True(
                result.ExitCode == 0,
                $"Release command failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
            );

            var notQaasZipPath = ExtractOutputPath(result.StandardOutput, "Not-QaaS zip:");
            var newDepsZipPath = ExtractOutputPath(result.StandardOutput, "New deps zip:");
            using var notQaasArchive = ZipFile.OpenRead(notQaasZipPath);
            using var newDepsArchive = ZipFile.OpenRead(newDepsZipPath);
            var notQaasEntries = notQaasArchive.Entries.Select(entry => entry.FullName).ToArray();
            var newDepsEntries = newDepsArchive.Entries.Select(entry => entry.FullName).ToArray();

            Assert.Contains(
                "not-qaas/Other.Sample/1.1.0/build/Other.Sample.targets",
                notQaasEntries
            );
            Assert.Contains(
                "not-qaas/Other.Sample/1.0.0/build/Other.Sample.targets",
                notQaasEntries
            );
            Assert.Contains(
                "new-deps/Other.Sample/1.1.0/build/Other.Sample.targets",
                newDepsEntries
            );
            Assert.DoesNotContain(
                "new-deps/Other.Sample/1.0.0/build/Other.Sample.targets",
                newDepsEntries
            );
            Assert.Contains(
                "Not-QaaS dependency package versions included: 2",
                result.StandardOutput
            );
            Assert.Contains(
                "New Not-QaaS dependency package versions included: 1",
                result.StandardOutput
            );
        }
        finally
        {
            DeleteTemporaryDirectory(workspaceRoot);
        }
    }

    private static JsonSchema ResolveArrayItemSchema(JsonSchema schema)
    {
        if (schema.Item is not null)
        {
            return schema.Item;
        }

        return schema.Items.First();
    }

    private static void AssertKnownValuesContainOnlySimpleNames(JsonSchema selectorSchema)
    {
        var knownValues = (
            (System.Collections.IEnumerable?)selectorSchema.ExtensionData?["x-qaas-known-values"]
        )
            ?.Cast<object?>()
            .Select(value => value?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        Assert.NotNull(knownValues);
        Assert.NotEmpty(knownValues!);
        Assert.DoesNotContain(
            knownValues!,
            value => value!.Contains('.', StringComparison.Ordinal)
        );
    }

    private static void AssertNoEnumSuggestionsContainNumericValues(JsonSchema schema)
    {
        var rootNode =
            JsonNode.Parse(schema.ToJson())
            ?? throw new InvalidOperationException("Could not parse schema JSON.");
        AssertNoEnumSuggestionsContainNumericValues(rootNode, "$");
    }

    private static void AssertNoEnumSuggestionsContainNumericValues(JsonNode node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (
                    obj.TryGetPropertyValue("x-enumNames", out var enumNamesNode)
                    && enumNamesNode is JsonArray enumNames
                    && enumNames.Count > 0
                    && obj.TryGetPropertyValue("enum", out var enumNode)
                    && enumNode is JsonArray enumValues
                )
                {
                    Assert.DoesNotContain(
                        enumValues,
                        value => value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out _)
                    );
                }

                foreach (var property in obj)
                {
                    if (property.Value is not null)
                    {
                        AssertNoEnumSuggestionsContainNumericValues(
                            property.Value,
                            $"{path}.{property.Key}"
                        );
                    }
                }

                break;
            }
            case JsonArray array:
            {
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is not null)
                    {
                        AssertNoEnumSuggestionsContainNumericValues(
                            array[index]!,
                            $"{path}[{index}]"
                        );
                    }
                }

                break;
            }
        }
    }

    private static async Task RunFamilySchemaGenerator(
        string repositoryRoot,
        string family,
        string outputRoot
    )
    {
        var packageArguments = GetFamilyPackageIds(family)
            .Select(packageId =>
                $"--package {packageId}={GetLatestFamilyPackageVersion(repositoryRoot, packageId)}"
            );

        var generatorProjectPath = Path.Combine(
            repositoryRoot,
            "QaaS.PackageMirror.FamilySchemas",
            "QaaS.PackageMirror.FamilySchemas.csproj"
        );
        var arguments =
            $"run --project \"{generatorProjectPath}\" -- --family {family} --packages-root \"{Path.Combine(repositoryRoot, "packages")}\" --output-root \"{outputRoot}\" --snapshot-id test-snapshot {string.Join(" ", packageArguments)}";

        var result = RunProcess("dotnet", arguments, repositoryRoot);
        Assert.True(
            result.ExitCode == 0,
            $"Schema generator failed for {family}:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
        );
    }

    private static void AssertRunnerDocsManifestContainsSectionFor(
        JsonNode docsManifest,
        JsonSchemaProperty schemaProperty,
        string topLevelPropertyName
    )
    {
        Assert.NotNull(schemaProperty);

        var sections = docsManifest["sections"]?.AsArray();
        Assert.NotNull(sections);
        Assert.Contains(
            sections!,
            section =>
                string.Equals(
                    section?["topLevelPropertyName"]?.GetValue<string>(),
                    topLevelPropertyName,
                    StringComparison.Ordinal
                )
        );
    }

    private static IReadOnlyList<string> GetFamilyPackageIds(string family)
    {
        return family switch
        {
            "runner-family" =>
            [
                "QaaS.Runner",
                "QaaS.Common.Generators",
                "QaaS.Common.Assertions",
                "QaaS.Common.Probes",
            ],
            "mocker-family" => ["QaaS.Mocker", "QaaS.Common.Generators", "QaaS.Common.Processors"],
            _ => throw new ArgumentOutOfRangeException(
                nameof(family),
                family,
                "Unsupported family."
            ),
        };
    }

    private static string GetLatestFamilyPackageVersion(string repositoryRoot, string packageId)
    {
        var packageDirectory = Path.Combine(
            repositoryRoot,
            "packages",
            "qaas",
            packageId.ToLowerInvariant()
        );
        Assert.True(
            Directory.Exists(packageDirectory),
            $"Could not find mirrored package directory for {packageId}."
        );

        var version = Directory
            .GetDirectories(packageDirectory)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        Assert.False(
            string.IsNullOrWhiteSpace(version),
            $"No versions found for mirrored package {packageId}."
        );
        return version!;
    }

    private static string ExtractOutputPath(string output, string prefix)
    {
        var line = output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(candidate =>
                candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            );

        Assert.True(
            line is not null,
            $"Could not find output line starting with '{prefix}'. Output:{Environment.NewLine}{output}"
        );
        return line![prefix.Length..].Trim();
    }

    private static string[] ExtractOutputPaths(string output, string prefix)
    {
        return output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Where(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(line => line[prefix.Length..].Trim())
            .ToArray();
    }

    private static void CreateFamilySchemaContractFiles(string workspaceRoot)
    {
        foreach (var familyId in FamilyIds)
        {
            var familyLatestRoot = Path.Combine(workspaceRoot, "schemas", familyId, "latest");
            Directory.CreateDirectory(familyLatestRoot);
            foreach (var fileName in FamilyJsonFileNames)
            {
                File.WriteAllText(Path.Combine(familyLatestRoot, fileName), "{}");
            }
        }
    }

    private static void WriteDocsZimProvenance(string repositoryRoot, string docsZimRoot)
    {
        var result = RunProcess(
            "dotnet",
            $"\"{GetMirrorToolsDllPath(repositoryRoot)}\" sync-docs-zim-provenance --docs-root \"{docsZimRoot}\" --docs-updated-date-utc 2026-07-13"
        );
        Assert.True(
            result.ExitCode == 0,
            $"Provenance command failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
        );
    }

    private static string CreateCombinedSourceArchive(
        string workspaceRoot,
        params string[] omittedFolderNames
    )
    {
        var sourceArchivesRoot = Path.Combine(workspaceRoot, "source-code-zips");
        var sourceRoot = Path.Combine(workspaceRoot, "source");
        var omitted = new HashSet<string>(omittedFolderNames, StringComparer.OrdinalIgnoreCase);

        foreach (var folderName in RequiredSourceArchiveFolderNames)
        {
            if (omitted.Contains(folderName))
            {
                continue;
            }

            var folderRoot = Path.Combine(sourceRoot, folderName);
            Directory.CreateDirectory(Path.Combine(folderRoot, "src"));
            File.WriteAllText(
                Path.Combine(folderRoot, "src", "Component.cs"),
                $"public class {folderName.Replace("-", string.Empty)} {{}}"
            );
        }

        CreateZipArchive(sourceRoot, Path.Combine(sourceArchivesRoot, "qaas-source-code.zip"));
        return sourceArchivesRoot;
    }

    private static ProcessResult RunProcess(
        string fileName,
        string arguments,
        string? workingDirectory = null
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            return new ProcessResult(
                -1,
                standardOutputTask.GetAwaiter().GetResult(),
                standardErrorTask.GetAwaiter().GetResult()
                    + $"{Environment.NewLine}Process timed out after 10 minutes: {fileName} {arguments}"
            );
        }

        var standardOutput = standardOutputTask.GetAwaiter().GetResult();
        var standardError = standardErrorTask.GetAwaiter().GetResult();

        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static void InitializeGitReleaseWorkspace(
        string workspaceRoot,
        bool commitNewDependencyVersion
    )
    {
        var qaasRoot = Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Runner", "4.1.1");
        var notQaasRoot = Path.Combine(workspaceRoot, "packages", "not-qaas", "Other.Sample");
        var version100Root = Path.Combine(notQaasRoot, "1.0.0", "build");
        var version110Root = Path.Combine(notQaasRoot, "1.1.0", "build");
        Directory.CreateDirectory(Path.Combine(qaasRoot, "lib", "net10.0"));
        Directory.CreateDirectory(version100Root);
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "state"));

        File.WriteAllText(Path.Combine(qaasRoot, "lib", "net10.0", "QaaS.Runner.dll"), "runner");
        File.WriteAllText(
            Path.Combine(version100Root, "Other.Sample.targets"),
            "<Project Version=\"1.0.0\" />"
        );
        CreateFamilySchemaContractFiles(workspaceRoot);

        RunGit(workspaceRoot, "init");
        RunGit(workspaceRoot, "config user.email codex@example.test");
        RunGit(workspaceRoot, "config user.name Codex");
        RunGit(workspaceRoot, "add .");
        RunGit(workspaceRoot, "commit -m initial");

        Directory.CreateDirectory(version110Root);
        File.WriteAllText(
            Path.Combine(version110Root, "Other.Sample.targets"),
            "<Project Version=\"1.1.0\" />"
        );

        if (commitNewDependencyVersion)
        {
            RunGit(workspaceRoot, "add .");
            RunGit(workspaceRoot, "commit -m add-new-dependency-version");
        }
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        var result = RunProcess("git", arguments, workingDirectory);
        Assert.True(
            result.ExitCode == 0,
            $"git {arguments} failed in '{workingDirectory}'.{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}"
        );
    }

    private static void CreateMinimalReleaseWorkspace(string workspaceRoot)
    {
        var qaasPackageRoot = Path.Combine(
            workspaceRoot,
            "packages",
            "qaas",
            "QaaS.Sample",
            "1.0.0"
        );
        var notQaasPackageRoot = Path.Combine(
            workspaceRoot,
            "packages",
            "not-qaas",
            "Other.Sample",
            "1.0.0"
        );
        Directory.CreateDirectory(qaasPackageRoot);
        Directory.CreateDirectory(notQaasPackageRoot);
        Directory.CreateDirectory(Path.Combine(workspaceRoot, "state"));

        File.WriteAllText(Path.Combine(qaasPackageRoot, "QaaS.Sample.1.0.0.nupkg"), "qaas");
        File.WriteAllText(
            Path.Combine(notQaasPackageRoot, "Other.Sample.1.0.0.nupkg"),
            "dependency"
        );
        CreateFamilySchemaContractFiles(workspaceRoot);
    }

    private static void CreateZipArchive(string sourceDirectory, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        ZipFile.CreateFromDirectory(sourceDirectory, destinationPath);
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

        foreach (
            var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
        )
        {
            File.SetAttributes(directory, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
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

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "qaas-package-mirror-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetMirrorToolsDllPath(string repositoryRoot)
    {
        var dllPath = Path.Combine(
            repositoryRoot,
            "QaaS.PackageMirror.Tools",
            "bin",
            "Release",
            "net10.0",
            "QaaS.PackageMirror.Tools.dll"
        );
        Assert.True(File.Exists(dllPath), $"Missing mirror tools CLI at '{dllPath}'.");
        return dllPath;
    }

    private static string GetMirrorCliDllPath(string repositoryRoot)
    {
        var dllPath = Path.Combine(
            repositoryRoot,
            "QaaS.PackageMirror",
            "bin",
            "Release",
            "net10.0",
            "QaaS.PackageMirror.dll"
        );
        Assert.True(File.Exists(dllPath), $"Missing mirror CLI at '{dllPath}'.");
        return dllPath;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
