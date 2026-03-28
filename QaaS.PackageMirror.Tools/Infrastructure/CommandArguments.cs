namespace QaaS.PackageMirror.Tools.Infrastructure;

/// <summary>
/// Lightweight command-line parser that matches the existing PowerShell-style named parameter surface.
/// </summary>
internal sealed class CommandArguments
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses PowerShell-style named arguments into value and flag collections.
    /// </summary>
    public static CommandArguments Parse(IEnumerable<string> args)
    {
        var parsed = new CommandArguments();
        var tokens = args.ToArray();

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 < tokens.Length && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (!parsed._values.TryGetValue(token, out var list))
                {
                    list = [];
                    parsed._values[token] = list;
                }

                list.Add(tokens[index + 1]);
                index++;
                continue;
            }

            parsed._flags.Add(token);
        }

        return parsed;
    }

    /// <summary>
    /// Returns <see langword="true"/> when a switch-style option is present.
    /// </summary>
    public bool HasFlag(string key) => _flags.Contains(key);

    /// <summary>
    /// Attempts to read the last supplied value for an option.
    /// </summary>
    public bool TryGetSingleValue(string key, out string value)
    {
        if (_values.TryGetValue(key, out var list) && list.Count > 0)
        {
            value = list[^1];
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads an optional string value.
    /// </summary>
    public string? GetOptionalValue(string key)
    {
        return TryGetSingleValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Reads and normalizes an optional path value.
    /// </summary>
    public string? GetOptionalPath(string key)
    {
        var path = GetOptionalValue(key);
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    /// <summary>
    /// Returns every supplied value for a repeated option.
    /// </summary>
    public IReadOnlyList<string> GetValues(string key)
    {
        return _values.TryGetValue(key, out var list) ? list : [];
    }
}
