internal sealed class CliArguments
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? Family => GetSingle("--family");
    public string? ResolverAppPath => GetSingle("--resolver-app");
    public string? OutputRoot => GetSingle("--output-root");
    public string? SnapshotId => GetSingle("--snapshot-id");
    public string? TriggerRepository => GetSingle("--trigger-repo");
    public string? TriggerTag => GetSingle("--trigger-tag");
    public string? TriggerRunId => GetSingle("--trigger-run-id");
    public string? TriggerOrigin => GetSingle("--trigger-origin");
    public IReadOnlyList<string> PackageVersions => GetMany("--package");

    public static CliArguments Parse(string[] args)
    {
        var parsed = new CliArguments();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Add(argument, string.Empty);
                continue;
            }

            parsed.Add(argument, args[index + 1]);
            index++;
        }

        return parsed;
    }

    public bool IsValid(out string error)
    {
        var missing = new[]
            {
                "--family",
                "--resolver-app",
                "--output-root",
                "--snapshot-id"
            }
            .Where(key => string.IsNullOrWhiteSpace(GetSingle(key)))
            .ToArray();

        if (missing.Length > 0)
        {
            error = $"Missing required arguments: {string.Join(", ", missing)}";
            return false;
        }

        if (!File.Exists(Path.GetFullPath(ResolverAppPath!)))
        {
            error = $"Resolver app '{ResolverAppPath}' does not exist.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void Add(string key, string value)
    {
        if (!_values.TryGetValue(key, out var list))
        {
            list = [];
            _values[key] = list;
        }

        list.Add(value);
    }

    private string? GetSingle(string key) => _values.GetValueOrDefault(key)?.LastOrDefault();
    private IReadOnlyList<string> GetMany(string key) => _values.GetValueOrDefault(key) ?? [];
}
