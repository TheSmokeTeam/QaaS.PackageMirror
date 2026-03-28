using QaaS.PackageMirror.Tools.Infrastructure;

namespace QaaS.PackageMirror.Tools.Commands;

/// <summary>
/// Represents a single mirror-maintenance command exposed by <c>QaaS.PackageMirror.Tools</c>.
/// </summary>
internal interface ICommandHandler
{
    /// <summary>
    /// Executes the command using the parsed command-line arguments.
    /// </summary>
    Task<int> ExecuteAsync(CommandArguments arguments);
}
