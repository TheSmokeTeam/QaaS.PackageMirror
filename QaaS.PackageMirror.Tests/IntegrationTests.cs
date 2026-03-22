using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.Json;
using NJsonSchema;
using Xunit;

namespace QaaS.PackageMirror.Tests;

public class IntegrationTests
{
    [Fact]
    public async Task FamilySchemaGenerator_AllowsCustomHooksDuplicateGeneratorsAndNamedServerTypes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var outputRoot = CreateTemporaryDirectory();

        try
        {
            await RunFamilySchemaGenerator(repositoryRoot, "mocker-family", outputRoot);
            await RunFamilySchemaGenerator(repositoryRoot, "runner-family", outputRoot);

            var mockerSchema = await JsonSchema.FromFileAsync(Path.Combine(outputRoot, "mocker-family", "latest", "schema.json"));
            var runnerSchema = await JsonSchema.FromFileAsync(Path.Combine(outputRoot, "runner-family", "latest", "schema.json"));

            var dataSourcesSchema = mockerSchema.Properties["DataSources"];
            var stubsSchema = mockerSchema.Properties["Stubs"];
            var serverSchema = mockerSchema.Properties["Server"];
            var serversSchema = mockerSchema.Properties["Servers"];
            var assertionsSchema = runnerSchema.Properties["Assertions"];
            var probesSchema = ResolveArrayItemSchema(runnerSchema.Properties["Sessions"]).Properties["Probes"];
            var generatorSelector = ResolveArrayItemSchema(dataSourcesSchema).Properties["Generator"];
            var processorSelector = ResolveArrayItemSchema(stubsSchema).Properties["Processor"];
            var assertionSelector = ResolveArrayItemSchema(assertionsSchema).Properties["Assertion"];
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

            Assert.Equal(
                "Optional stage number that decides when the runner waits for this session to complete. If omitted, the session becomes visible only after its own stage completes. If set, the runner defers waiting until the configured future stage is reached.",
                sessionSchema.Properties["RunUntilStage"].Description);
            Assert.Equal(
                "Optional per-stage configuration for the session's internal action stages. Use this to override timing around a specific stage number without changing the action order.",
                sessionSchema.Properties["Stages"].Description);
            Assert.Equal(
                "The internal session stage number this configuration applies to.",
                ResolveArrayItemSchema(sessionSchema.Properties["Stages"]).Properties["StageNumber"].Description);
            Assert.DoesNotContain("ProcessorSpecificConfiguration", ResolveArrayItemSchema(stubsSchema).Properties.Keys);
            Assert.Contains("ProcessorConfiguration", ResolveArrayItemSchema(stubsSchema).Properties.Keys);
            Assert.DoesNotContain(
                "ProcessorSpecificConfiguration",
                ResolveArrayItemSchema(stubsSchema)
                    .AnyOf
                    .SelectMany(branch => branch.Properties.Keys));
            Assert.Contains(
                ResolveArrayItemSchema(stubsSchema).AnyOf,
                branch => branch.Properties.ContainsKey("ProcessorConfiguration"));
            Assert.Contains("JsonStorageFormat", storageSchema.Properties.Keys);

            Assert.Empty(
                dataSourcesSchema.Validate("""
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
                    """));

            Assert.Empty(
                dataSourcesSchema.Validate("""
                    [
                      {
                        "Name": "source-legacy",
                        "Generator": "QaaS.Common.Generators.JsonGenerators.Json",
                        "GeneratorConfiguration": {
                          "JsonDataSourceName": "payload-legacy"
                        }
                      }
                    ]
                    """));

            Assert.Empty(
                stubsSchema.Validate("""
                    [
                      {
                        "Name": "stub-custom",
                        "Processor": "Contoso.CustomProcessor",
                        "ProcessorConfiguration": {
                          "Retries": 2
                        }
                      }
                    ]
                    """));

            Assert.Empty(
                assertionsSchema.Validate("""
                    [
                      {
                        "Name": "custom-assertion",
                        "Assertion": "Contoso.CustomAssertion",
                        "AssertionConfiguration": {
                          "ExpectedStatus": "ok"
                        }
                      }
                    ]
                    """));

            Assert.Empty(
                probesSchema.Validate("""
                    [
                      {
                        "Name": "custom-probe",
                        "Probe": "Contoso.CustomProbe",
                        "ProbeConfiguration": {
                          "Threshold": 5
                        }
                      }
                    ]
                    """));

            Assert.Empty(
                serverSchema.Validate("""
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
                    """));

            Assert.Empty(
                serversSchema.Validate("""
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
                    """));
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
            var qaasPackageRoot = Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Sample", "1.0.0");
            var notQaasPackageRoot = Path.Combine(workspaceRoot, "packages", "not-qaas", "Other.Sample", "1.0.0");
            Directory.CreateDirectory(Path.Combine(qaasPackageRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(qaasPackageRoot, "src"));
            Directory.CreateDirectory(Path.Combine(notQaasPackageRoot, "contentFiles", "cs", "any"));
            Directory.CreateDirectory(Path.Combine(notQaasPackageRoot, "build"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "schemas", "runner-family", "latest"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "schemas", "mocker-family", "latest"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "state"));

            File.WriteAllText(Path.Combine(qaasPackageRoot, "lib", "net10.0", "QaaS.Sample.dll"), "binary");
            File.WriteAllText(Path.Combine(qaasPackageRoot, "src", "Sample.cs"), "public class Sample {}");
            File.WriteAllText(Path.Combine(notQaasPackageRoot, "contentFiles", "cs", "any", "Helper.cs"), "public class Helper {}");
            File.WriteAllText(Path.Combine(notQaasPackageRoot, "build", "Other.Sample.targets"), "<Project />");
            File.WriteAllText(Path.Combine(workspaceRoot, "schemas", "runner-family", "latest", "schema.json"), "{}");
            File.WriteAllText(Path.Combine(workspaceRoot, "schemas", "mocker-family", "latest", "schema.json"), "{}");

            var releaseScriptPath = Path.Combine(repositoryRoot, "scripts", "Publish-MirrorRelease.ps1");
            var result = RunProcess(
                "powershell",
                $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{releaseScriptPath}\" -WorkspaceRoot \"{workspaceRoot}\" -GitHubRepository \"TheSmokeTeam/QaaS.PackageMirror\" -SkipPublish");
            Assert.True(
                result.ExitCode == 0,
                $"Release script failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

            var qaasZipPath = ExtractOutputPath(result.StandardOutput, "QaaS zip:");
            var notQaasZipPath = ExtractOutputPath(result.StandardOutput, "Not-QaaS zip:");

            Assert.True(File.Exists(qaasZipPath));
            Assert.True(File.Exists(notQaasZipPath));

            using var qaasArchive = ZipFile.OpenRead(qaasZipPath);
            using var notQaasArchive = ZipFile.OpenRead(notQaasZipPath);
            var qaasEntries = qaasArchive.Entries.Select(entry => entry.FullName).ToArray();
            var notQaasEntries = notQaasArchive.Entries.Select(entry => entry.FullName).ToArray();

            Assert.Contains("qaas/QaaS.Sample/1.0.0/lib/net10.0/QaaS.Sample.dll", qaasEntries);
            Assert.DoesNotContain(qaasEntries, entry => entry.Contains("Sample.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("not-qaas/Other.Sample/1.0.0/build/Other.Sample.targets", notQaasEntries);
            Assert.DoesNotContain(
                notQaasEntries,
                entry => entry.Contains("contentFiles", StringComparison.OrdinalIgnoreCase) ||
                         entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void PublishMirrorRelease_IncludesOnlyPackageVersionsMissingFromPreviousPackagesRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var workspaceRoot = CreateTemporaryDirectory();
        var previousWorkspaceRoot = CreateTemporaryDirectory();

        try
        {
            var previousQaasRoot = Path.Combine(previousWorkspaceRoot, "packages", "qaas", "QaaS.Runner", "1.0.0");
            var previousNotQaasRoot = Path.Combine(previousWorkspaceRoot, "packages", "not-qaas", "Other.Sample", "1.0.0");
            Directory.CreateDirectory(Path.Combine(previousQaasRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(previousNotQaasRoot, "build"));
            File.WriteAllText(Path.Combine(previousQaasRoot, "lib", "net10.0", "QaaS.Runner.dll"), "old-binary");
            File.WriteAllText(Path.Combine(previousNotQaasRoot, "build", "Other.Sample.targets"), "<Project />");

            var currentQaasExistingRoot = Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Runner", "1.0.0");
            var currentQaasNewRoot = Path.Combine(workspaceRoot, "packages", "qaas", "QaaS.Runner", "2.0.0");
            var currentNotQaasExistingRoot = Path.Combine(workspaceRoot, "packages", "not-qaas", "Other.Sample", "1.0.0");
            var currentNotQaasNewRoot = Path.Combine(workspaceRoot, "packages", "not-qaas", "Other.Sample", "1.1.0");
            Directory.CreateDirectory(Path.Combine(currentQaasExistingRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(currentQaasNewRoot, "lib", "net10.0"));
            Directory.CreateDirectory(Path.Combine(currentNotQaasExistingRoot, "build"));
            Directory.CreateDirectory(Path.Combine(currentNotQaasNewRoot, "build"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "schemas", "runner-family", "latest"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "schemas", "mocker-family", "latest"));
            Directory.CreateDirectory(Path.Combine(workspaceRoot, "state"));

            File.WriteAllText(Path.Combine(currentQaasExistingRoot, "lib", "net10.0", "QaaS.Runner.dll"), "existing-binary");
            File.WriteAllText(Path.Combine(currentQaasNewRoot, "lib", "net10.0", "QaaS.Runner.dll"), "new-binary");
            File.WriteAllText(Path.Combine(currentNotQaasExistingRoot, "build", "Other.Sample.targets"), "<Project />");
            File.WriteAllText(Path.Combine(currentNotQaasNewRoot, "build", "Other.Sample.targets"), "<Project Version=\"1.1.0\" />");
            File.WriteAllText(Path.Combine(workspaceRoot, "schemas", "runner-family", "latest", "schema.json"), "{}");
            File.WriteAllText(Path.Combine(workspaceRoot, "schemas", "mocker-family", "latest", "schema.json"), "{}");
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
                """);

            var releaseScriptPath = Path.Combine(repositoryRoot, "scripts", "Publish-MirrorRelease.ps1");
            var result = RunProcess(
                "powershell",
                $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File \"{releaseScriptPath}\" -WorkspaceRoot \"{workspaceRoot}\" -PreviousPackagesRoot \"{Path.Combine(previousWorkspaceRoot, "packages")}\" -GitHubRepository \"TheSmokeTeam/QaaS.PackageMirror\" -SkipPublish");
            Assert.True(
                result.ExitCode == 0,
                $"Release script failed:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");

            var qaasZipPath = ExtractOutputPath(result.StandardOutput, "QaaS zip:");
            var notQaasZipPath = ExtractOutputPath(result.StandardOutput, "Not-QaaS zip:");
            var notesPath = ExtractOutputPath(result.StandardOutput, "Notes file:");

            using var qaasArchive = ZipFile.OpenRead(qaasZipPath);
            using var notQaasArchive = ZipFile.OpenRead(notQaasZipPath);
            var qaasEntries = qaasArchive.Entries.Select(entry => entry.FullName).ToArray();
            var notQaasEntries = notQaasArchive.Entries.Select(entry => entry.FullName).ToArray();
            var notes = File.ReadAllText(notesPath);

            Assert.Contains("qaas/QaaS.Runner/2.0.0/lib/net10.0/QaaS.Runner.dll", qaasEntries);
            Assert.DoesNotContain("qaas/QaaS.Runner/1.0.0/lib/net10.0/QaaS.Runner.dll", qaasEntries);
            Assert.Contains("not-qaas/Other.Sample/1.1.0/build/Other.Sample.targets", notQaasEntries);
            Assert.DoesNotContain("not-qaas/Other.Sample/1.0.0/build/Other.Sample.targets", notQaasEntries);
            Assert.Contains("QaaS.Runner version 2.0.0", notes);
            Assert.DoesNotContain("QaaS.Runner version 1.0.0", notes);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
            Directory.Delete(previousWorkspaceRoot, recursive: true);
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
        var knownValues = ((System.Collections.IEnumerable?)selectorSchema.ExtensionData?["x-qaas-known-values"])
            ?.Cast<object?>()
            .Select(value => value?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        Assert.NotNull(knownValues);
        Assert.NotEmpty(knownValues!);
        Assert.DoesNotContain(knownValues!, value => value!.Contains('.', StringComparison.Ordinal));
    }

    private static void AssertNoEnumSuggestionsContainNumericValues(JsonSchema schema)
    {
        var rootNode = JsonNode.Parse(schema.ToJson())
                       ?? throw new InvalidOperationException("Could not parse schema JSON.");
        AssertNoEnumSuggestionsContainNumericValues(rootNode, "$");
    }

    private static void AssertNoEnumSuggestionsContainNumericValues(JsonNode node, string path)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                if (obj.TryGetPropertyValue("x-enumNames", out var enumNamesNode) &&
                    enumNamesNode is JsonArray enumNames &&
                    enumNames.Count > 0 &&
                    obj.TryGetPropertyValue("enum", out var enumNode) &&
                    enumNode is JsonArray enumValues)
                {
                    Assert.DoesNotContain(
                        enumValues,
                        value => value is JsonValue jsonValue &&
                                 jsonValue.TryGetValue<int>(out _));
                }

                foreach (var property in obj)
                {
                    if (property.Value is not null)
                    {
                        AssertNoEnumSuggestionsContainNumericValues(property.Value, $"{path}.{property.Key}");
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
                        AssertNoEnumSuggestionsContainNumericValues(array[index]!, $"{path}[{index}]");
                    }
                }

                break;
            }
        }
    }

    private static async Task RunFamilySchemaGenerator(string repositoryRoot, string family, string outputRoot)
    {
        var metadataPath = Path.Combine(repositoryRoot, "schemas", family, "latest", "metadata.json");
        using var metadataDocument = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        var packageArguments = metadataDocument.RootElement
            .GetProperty("packages")
            .EnumerateArray()
            .Select(package => $"--package {package.GetProperty("packageId").GetString()}={package.GetProperty("version").GetString()}");

        var generatorProjectPath = Path.Combine(repositoryRoot, "QaaS.PackageMirror.FamilySchemas", "QaaS.PackageMirror.FamilySchemas.csproj");
        var arguments =
            $"run --project \"{generatorProjectPath}\" -- --family {family} --packages-root \"{Path.Combine(repositoryRoot, "packages")}\" --output-root \"{outputRoot}\" --snapshot-id test-snapshot {string.Join(" ", packageArguments)}";

        var result = RunProcess("dotnet", arguments, repositoryRoot);
        Assert.True(
            result.ExitCode == 0,
            $"Schema generator failed for {family}:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}{result.StandardError}");
    }

    private static string ExtractOutputPath(string output, string prefix)
    {
        var line = output
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        Assert.True(line is not null, $"Could not find output line starting with '{prefix}'. Output:{Environment.NewLine}{output}");
        return line![prefix.Length..].Trim();
    }

    private static ProcessResult RunProcess(string fileName, string arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, standardOutput, standardError);
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
        var path = Path.Combine(Path.GetTempPath(), "qaas-package-mirror-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
