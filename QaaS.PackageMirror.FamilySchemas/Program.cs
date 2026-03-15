using System.Text.Json;

var arguments = CliArguments.Parse(args);
if (!arguments.IsValid(out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage: --family <runner-family|mocker-family> --resolver-app <path> --output-root <path> --snapshot-id <id> [--package <PackageId=Version>] [--trigger-repo <owner/repo>] [--trigger-tag <tag>] [--trigger-run-id <id>] [--trigger-origin <url>]");
    return 1;
}

var manifest = FamilyManifests.Resolve(arguments.Family!);
var generator = new FamilySchemaGenerator();
var result = generator.Generate(manifest, arguments);

var latestDirectory = Path.Combine(arguments.OutputRoot!, manifest.Id, "latest");
var snapshotDirectory = Path.Combine(arguments.OutputRoot!, manifest.Id, "snapshots", arguments.SnapshotId!);
Directory.CreateDirectory(latestDirectory);
Directory.CreateDirectory(snapshotDirectory);

var schemaJson = result.Schema.ToJson();
File.WriteAllText(Path.Combine(latestDirectory, "schema.json"), schemaJson);
File.WriteAllText(Path.Combine(snapshotDirectory, "schema.json"), schemaJson);

var metadataJson = JsonSerializer.Serialize(result.Metadata, JsonDefaults.Indented);
File.WriteAllText(Path.Combine(latestDirectory, "metadata.json"), metadataJson + Environment.NewLine);
File.WriteAllText(Path.Combine(snapshotDirectory, "metadata.json"), metadataJson + Environment.NewLine);

Console.WriteLine($"Generated schema for {manifest.Id} at {latestDirectory}");
Console.WriteLine($"Snapshot written to {snapshotDirectory}");
return 0;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
