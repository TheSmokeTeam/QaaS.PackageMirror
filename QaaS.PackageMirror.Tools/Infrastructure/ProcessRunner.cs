using System.Diagnostics;

namespace QaaS.PackageMirror.Tools.Infrastructure;

/// <summary>
/// Runs external processes while preserving the script-compatible stdout/stderr contract used by workflows and tests.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Runs an external process, optionally echoing its output and applying temporary environment overrides.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        bool throwOnFailure = true,
        bool echoOutput = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach (var pair in environmentVariables)
            {
                if (pair.Value is null)
                {
                    startInfo.Environment.Remove(pair.Key);
                    continue;
                }

                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var result = new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);

        if (echoOutput && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            Console.Write(result.StandardOutput);
        }

        if (echoOutput && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            Console.Error.Write(result.StandardError);
        }

        if (throwOnFailure && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command '{fileName} {string.Join(' ', arguments)}' failed with exit code {result.ExitCode}.");
        }

        return result;
    }
}

/// <summary>
/// Captures the exit code and full output streams of a completed process.
/// </summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
