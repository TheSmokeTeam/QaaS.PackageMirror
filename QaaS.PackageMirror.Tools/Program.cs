using QaaS.PackageMirror.Tools.Commands;
using QaaS.PackageMirror.Tools.Infrastructure;

namespace QaaS.PackageMirror.Tools;

/// <summary>
/// Hosts the documented CLI surface that replaced the repository-owned mirror PowerShell scripts.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Dispatches the requested mirror maintenance command.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var commandName = args[0];
        var commandArguments = CommandArguments.Parse(args.Skip(1));

        try
        {
            return commandName switch
            {
                "generate-family-schemas" => await new GenerateFamilySchemasCommand().ExecuteAsync(
                    commandArguments
                ),
                "publish-mirror-release" => await new PublishMirrorReleaseCommand().ExecuteAsync(
                    commandArguments
                ),
                "sync-docs-zim-provenance" => await new SyncDocsZimProvenanceCommand().ExecuteAsync(
                    commandArguments
                ),
                "sync-restored-packages" => await new SyncRestoredPackagesCommand().ExecuteAsync(
                    commandArguments
                ),
                "--help" or "-h" or "help" => PrintHelp(commandArguments),
                _ => PrintUnknownCommand(commandName),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 3;
        }
    }

    private static int PrintUnknownCommand(string commandName)
    {
        Console.Error.WriteLine($"Unknown command '{commandName}'.");
        PrintUsage();
        return 1;
    }

    private static int PrintHelp(CommandArguments arguments)
    {
        if (arguments.TryGetSingleValue("--command", out var commandName))
        {
            PrintCommandUsage(commandName);
            return 0;
        }

        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "Usage: dotnet run --project QaaS.PackageMirror.Tools/QaaS.PackageMirror.Tools.csproj -- <command> [options]"
        );
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine(
            "  generate-family-schemas  Generate the stable runner-family and mocker-family schema outputs."
        );
        Console.WriteLine(
            "  publish-mirror-release   Build the release asset bundle and optionally publish the GitHub release."
        );
        Console.WriteLine(
            "  sync-docs-zim-provenance Write or validate the qaas-docs ZIM metadata contract."
        );
        Console.WriteLine(
            "  sync-restored-packages   Download tracked restore artifacts, rebuild the mirror, and refresh schemas."
        );
        Console.WriteLine();
        Console.WriteLine("Use 'help --command <name>' to print per-command options.");
    }

    private static void PrintCommandUsage(string commandName)
    {
        switch (commandName)
        {
            case "generate-family-schemas":
                Console.WriteLine("generate-family-schemas");
                Console.WriteLine("  --mirror-root <path>");
                Console.WriteLine("  --output-root <path>");
                Console.WriteLine("  --snapshot-id <id>");
                Console.WriteLine("  --trigger-repo <owner/repo>");
                Console.WriteLine("  --trigger-tag <tag>");
                Console.WriteLine("  --trigger-run-id <id>");
                Console.WriteLine("  --trigger-origin <url>");
                return;
            case "publish-mirror-release":
                Console.WriteLine("publish-mirror-release");
                Console.WriteLine("  --workspace-root <path>");
                Console.WriteLine("  --github-repository <owner/repo>");
                Console.WriteLine("  --branch-name <name>");
                Console.WriteLine("  --release-tag <tag>");
                Console.WriteLine("  --release-tag-prefix <prefix>");
                Console.WriteLine("  --github-token <token>");
                Console.WriteLine("  --source-archives-root <path>");
                Console.WriteLine("  --docs-zim-root <path>");
                Console.WriteLine("  --skip-publish");
                return;
            case "sync-docs-zim-provenance":
                Console.WriteLine("sync-docs-zim-provenance");
                Console.WriteLine("  --docs-root <path>");
                Console.WriteLine("  --docs-updated-date-utc <yyyy-MM-dd>");
                Console.WriteLine("  --check");
                return;
            case "sync-restored-packages":
                Console.WriteLine("sync-restored-packages");
                Console.WriteLine("  --source-repository <owner/repo>");
                Console.WriteLine("  --github-token <token>");
                return;
            default:
                Console.Error.WriteLine($"Unknown command '{commandName}'.");
                return;
        }
    }
}
