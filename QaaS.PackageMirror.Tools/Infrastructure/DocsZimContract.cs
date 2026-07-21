using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QaaS.PackageMirror.Tools.Infrastructure;

/// <summary>
/// Defines and validates the machine-readable handoff between PackageMirror docs generation and
/// qaas-docs ZIM packaging.
/// </summary>
internal static class DocsZimContract
{
    public const int CurrentSchemaVersion = 1;
    public const string ProvenanceFileName = "qaas-docs-zim-provenance.json";
    public const string ZimAssetFileName = "qaas-docs.zim";
    public const string ImageArchiveFileName = "qaas-docs-image.tgz";
    public const string ZimName = "QaaS Documantation";
    public const string ZimTitle = "Complete QaaS Documantation";

    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web
    )
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <summary>
    /// Creates the canonical provenance contract for the supplied UTC calendar date.
    /// </summary>
    public static DocsZimProvenance Create(string docsUpdatedDateUtc)
    {
        EnsureValidDate(docsUpdatedDateUtc);
        return new DocsZimProvenance
        {
            SchemaVersion = CurrentSchemaVersion,
            DocsUpdatedDateUtc = docsUpdatedDateUtc,
            Zim = new DocsZimMetadata
            {
                Name = ZimName,
                Title = ZimTitle,
                Description = docsUpdatedDateUtc,
                FileName = ZimAssetFileName,
            },
        };
    }

    /// <summary>
    /// Writes the canonical provenance file under a qaas-docs repository root.
    /// </summary>
    public static string Write(string docsRoot, string docsUpdatedDateUtc)
    {
        var provenance = Create(docsUpdatedDateUtc);
        var provenancePath = GetProvenancePath(docsRoot);
        var json = JsonSerializer
            .Serialize(provenance, SerializerOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        File.WriteAllText(provenancePath, $"{json}\n", new UTF8Encoding(false));
        return provenancePath;
    }

    /// <summary>
    /// Reads a provenance file and rejects any metadata that does not match the canonical contract.
    /// </summary>
    public static DocsZimProvenance ReadAndValidate(string provenancePath)
    {
        if (!File.Exists(provenancePath))
        {
            throw new FileNotFoundException(
                $"Missing docs ZIM provenance at '{provenancePath}'.",
                provenancePath
            );
        }

        DocsZimProvenance provenance;
        try
        {
            provenance =
                JsonSerializer.Deserialize<DocsZimProvenance>(
                    File.ReadAllText(provenancePath),
                    SerializerOptions
                ) ?? throw new InvalidOperationException("The provenance document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Docs ZIM provenance '{provenancePath}' is not valid contract JSON: {exception.Message}",
                exception
            );
        }

        Validate(provenance, provenancePath);
        return provenance;
    }

    /// <summary>
    /// Returns the canonical provenance path under a qaas-docs repository root.
    /// </summary>
    public static string GetProvenancePath(string docsRoot) =>
        Path.Combine(docsRoot, ProvenanceFileName);

    private static void Validate(DocsZimProvenance provenance, string provenancePath)
    {
        RequireEqual(
            provenance.SchemaVersion,
            CurrentSchemaVersion,
            "schemaVersion",
            provenancePath
        );
        EnsureValidDate(provenance.DocsUpdatedDateUtc);

        if (provenance.Zim is null)
        {
            throw new InvalidOperationException(
                $"Docs ZIM provenance '{provenancePath}' is missing 'zim'."
            );
        }

        RequireEqual(provenance.Zim.Name, ZimName, "zim.name", provenancePath);
        RequireEqual(provenance.Zim.Title, ZimTitle, "zim.title", provenancePath);
        RequireEqual(
            provenance.Zim.Description,
            provenance.DocsUpdatedDateUtc,
            "zim.description",
            provenancePath
        );
        RequireEqual(provenance.Zim.FileName, ZimAssetFileName, "zim.fileName", provenancePath);
    }

    private static void EnsureValidDate(string? value)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || !DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _
            )
        )
        {
            throw new InvalidOperationException(
                "Docs ZIM provenance 'docsUpdatedDateUtc' must be an exact UTC calendar date in yyyy-MM-dd format."
            );
        }
    }

    private static void RequireEqual<T>(
        T actual,
        T expected,
        string propertyName,
        string provenancePath
    )
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new InvalidOperationException(
                $"Docs ZIM provenance '{provenancePath}' has invalid '{propertyName}'. Expected '{expected}', found '{actual}'."
            );
        }
    }
}

internal sealed class DocsZimProvenance
{
    public int SchemaVersion { get; set; }
    public string DocsUpdatedDateUtc { get; set; } = string.Empty;
    public DocsZimMetadata? Zim { get; set; }
}

internal sealed class DocsZimMetadata
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}
