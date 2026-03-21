using System.Diagnostics;
using System.IO.Compression;
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
            var assertionSelector = ResolveArrayItemSchema(assertionsSchema).Properties["Assertion"];
            var probeSelector = ResolveArrayItemSchema(probesSchema).Properties["Probe"];

            Assert.False(serverSchema.Properties.ContainsKey("Type"));
            Assert.False(ResolveArrayItemSchema(serversSchema).Properties.ContainsKey("Type"));
            Assert.NotEmpty((System.Collections.IEnumerable?)generatorSelector.ExtensionData?["x-qaas-known-values"]);
            Assert.NotEmpty((System.Collections.IEnumerable?)assertionSelector.ExtensionData?["x-qaas-known-values"]);
            Assert.NotEmpty((System.Collections.IEnumerable?)probeSelector.ExtensionData?["x-qaas-known-values"]);

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
            Assert.Equal(
                0,
                result.ExitCode);

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

    private static JsonSchema ResolveArrayItemSchema(JsonSchema schema)
    {
        if (schema.Item is not null)
        {
            return schema.Item;
        }

        return schema.Items.First();
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
