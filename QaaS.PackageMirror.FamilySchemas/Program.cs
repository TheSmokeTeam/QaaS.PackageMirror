using System.Text.Json;

var arguments = CliArguments.Parse(args);
if (!arguments.IsValid(out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage: --family <runner-family|mocker-family> --packages-root <path> --output-root <path> --snapshot-id <id> [--package <PackageId=Version>] [--trigger-repo <owner/repo>] [--trigger-tag <tag>] [--trigger-run-id <id>] [--trigger-origin <url>]");
    return 1;
}

var manifest = FamilyManifests.Resolve(arguments.Family!);
var generator = new FamilySchemaGenerator();
var result = generator.Generate(manifest, arguments);

var latestDirectory = Path.Combine(arguments.OutputRoot!, manifest.Id, "latest");
Directory.CreateDirectory(latestDirectory);

var schemaJson = result.Schema.ToJson();
File.WriteAllText(Path.Combine(latestDirectory, "schema.json"), schemaJson);

var metadataJson = JsonSerializer.Serialize(result.Metadata, JsonDefaults.Indented);
File.WriteAllText(Path.Combine(latestDirectory, "metadata.json"), metadataJson + Environment.NewLine);

Console.WriteLine($"Generated schema for {manifest.Id} at {latestDirectory}");
return 0;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
