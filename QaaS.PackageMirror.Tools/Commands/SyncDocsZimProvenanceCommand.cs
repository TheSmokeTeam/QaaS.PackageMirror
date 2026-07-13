using QaaS.PackageMirror.Tools.Infrastructure;

namespace QaaS.PackageMirror.Tools.Commands;

/// <summary>
/// Writes or validates the canonical qaas-docs ZIM provenance contract.
/// </summary>
internal sealed class SyncDocsZimProvenanceCommand : ICommandHandler
{
    /// <summary>
    /// Writes the contract for a docs generation run, or validates the committed contract when
    /// <c>--check</c> is supplied.
    /// </summary>
    public Task<int> ExecuteAsync(CommandArguments arguments)
    {
        var docsRoot = arguments.GetOptionalPath("--docs-root");
        if (string.IsNullOrWhiteSpace(docsRoot))
        {
            throw new InvalidOperationException("--docs-root is required.");
        }

        if (!Directory.Exists(docsRoot))
        {
            throw new DirectoryNotFoundException($"Docs root '{docsRoot}' does not exist.");
        }

        var provenancePath = DocsZimContract.GetProvenancePath(docsRoot);
        if (arguments.HasFlag("--check"))
        {
            var provenance = DocsZimContract.ReadAndValidate(provenancePath);
            Console.WriteLine($"Validated docs ZIM provenance: {provenancePath}");
            Console.WriteLine($"Docs updated date UTC: {provenance.DocsUpdatedDateUtc}");
            return Task.FromResult(0);
        }

        var docsUpdatedDateUtc = arguments.GetOptionalValue("--docs-updated-date-utc");
        if (string.IsNullOrWhiteSpace(docsUpdatedDateUtc))
        {
            throw new InvalidOperationException(
                "--docs-updated-date-utc is required unless --check is used."
            );
        }

        provenancePath = DocsZimContract.Write(docsRoot, docsUpdatedDateUtc);
        Console.WriteLine($"Docs ZIM provenance: {provenancePath}");
        Console.WriteLine($"Docs updated date UTC: {docsUpdatedDateUtc}");
        return Task.FromResult(0);
    }
}
