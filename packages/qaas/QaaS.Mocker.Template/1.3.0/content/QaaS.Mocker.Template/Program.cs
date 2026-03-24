Directory.SetCurrentDirectory(AppContext.BaseDirectory);
QaaS.Mocker.Bootstrap.New(NormalizeExampleArgs(args)).Run();

static string[] NormalizeExampleArgs(IEnumerable<string> args)
{
    var normalizedArguments = args.ToList();
    if (normalizedArguments.All(argument =>
            !string.Equals(argument, "--no-env", StringComparison.OrdinalIgnoreCase)))
    {
        normalizedArguments.Add("--no-env");
    }

    return [.. normalizedArguments];
}
