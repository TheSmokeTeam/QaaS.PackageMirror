internal sealed record FamilyManifest(
    string Id,
    string DisplayName,
    string RootAssemblyName,
    string RootTypeFullName,
    string DocsBasePath,
    IReadOnlyList<string> PackageIds,
    IReadOnlyList<string> AssembliesToLoad,
    IReadOnlyList<DocsSectionDefinition> RootSections,
    IReadOnlyList<HookSlot> HookSlots);

internal sealed record DocsSectionDefinition(
    string SourcePropertyName,
    string DocsSlug,
    string? OverviewSummaryOverride = null,
    IReadOnlyList<string>? LegacyAliases = null,
    IReadOnlyList<string>? Notes = null)
{
    public IReadOnlyList<string> LegacyAliasesOrEmpty => LegacyAliases ?? Array.Empty<string>();
    public IReadOnlyList<string> NotesOrEmpty => Notes ?? Array.Empty<string>();
}

internal sealed record HookSlot(
    string CollectionPropertyName,
    string? NestedCollectionPropertyName,
    string SelectorPropertyName,
    string ConfigurationPropertyName,
    string HookKind,
    string DocsBasePath,
    string HookInterfaceFullName,
    string GenericBaseTypeFullName);

internal static class FamilyManifests
{
    private static readonly FamilyManifest Runner = new(
        Id: "runner-family",
        DisplayName: "QaaS Runner Family",
        RootAssemblyName: "QaaS.Runner",
        RootTypeFullName: "QaaS.Runner.ExecutionBuilder",
        DocsBasePath: "qaas/userInterfaces/runner/configurationSections",
        PackageIds:
        [
            "QaaS.Runner",
            "QaaS.Common.Generators",
            "QaaS.Common.Assertions",
            "QaaS.Common.Probes"
        ],
        AssembliesToLoad:
        [
            "QaaS.Runner",
            "QaaS.Common.Generators",
            "QaaS.Common.Assertions",
            "QaaS.Common.Probes",
            "QaaS.Framework.SDK"
        ],
        RootSections:
        [
            new DocsSectionDefinition("MetaData", "metaData"),
            new DocsSectionDefinition("Links", "links"),
            new DocsSectionDefinition("Storages", "storages"),
            new DocsSectionDefinition("DataSources", "dataSources"),
            new DocsSectionDefinition(
                "Sessions",
                "sessions",
                Notes:
                [
                    "Session documentation includes RunUntilStage and per-stage Stages[] overrides."
                ]),
            new DocsSectionDefinition("Assertions", "assertions"),
            new DocsSectionDefinition("Reporters", "reporters")
        ],
        HookSlots:
        [
            new HookSlot(
                "DataSources",
                null,
                "Generator",
                "GeneratorConfiguration",
                "generator",
                "generators/availableGenerators",
                "QaaS.Framework.SDK.Hooks.Generator.IGenerator",
                "QaaS.Framework.SDK.Hooks.Generator.BaseGenerator`1"),
            new HookSlot(
                "Assertions",
                null,
                "Assertion",
                "AssertionConfiguration",
                "assertion",
                "assertions/availableAssertions",
                "QaaS.Framework.SDK.Hooks.Assertion.IAssertion",
                "QaaS.Framework.SDK.Hooks.Assertion.BaseAssertion`1"),
            new HookSlot(
                "Sessions",
                "Probes",
                "Probe",
                "ProbeConfiguration",
                "probe",
                "probes/availableProbes",
                "QaaS.Framework.SDK.Hooks.Probe.IProbe",
                "QaaS.Framework.SDK.Hooks.Probe.BaseProbe`1")
        ]);

    private static readonly FamilyManifest Mocker = new(
        Id: "mocker-family",
        DisplayName: "QaaS Mocker Family",
        RootAssemblyName: "QaaS.Mocker",
        RootTypeFullName: "QaaS.Mocker.ExecutionBuilder",
        DocsBasePath: "mocker/userInterfaces/mocker/configurationSections",
        PackageIds:
        [
            "QaaS.Mocker",
            "QaaS.Common.Generators",
            "QaaS.Common.Processors"
        ],
        AssembliesToLoad:
        [
            "QaaS.Mocker",
            "QaaS.Common.Generators",
            "QaaS.Common.Processors",
            "QaaS.Framework.SDK"
        ],
        RootSections:
        [
            new DocsSectionDefinition("DataSources", "dataSources"),
            new DocsSectionDefinition("Stubs", "stubs"),
            new DocsSectionDefinition("Controller", "controller"),
            new DocsSectionDefinition(
                "Servers",
                "server",
                OverviewSummaryOverride:
                "Servers defines the listeners that QaaS.Mocker starts for a mock execution. Each item hosts one concrete protocol configuration such as `Http`, `Grpc`, or `Socket`, so a single mocker run can expose multiple endpoints concurrently while keeping protocol-specific settings isolated per server entry.",
                LegacyAliases:
                [
                    "Server"
                ],
                Notes:
                [
                    "Servers is the preferred configuration model for new docs and runtime usage.",
                    "The legacy Server property remains supported as a single-server shorthand."
                ])
        ],
        HookSlots:
        [
            new HookSlot(
                "DataSources",
                null,
                "Generator",
                "GeneratorConfiguration",
                "generator",
                "generators/availableGenerators",
                "QaaS.Framework.SDK.Hooks.Generator.IGenerator",
                "QaaS.Framework.SDK.Hooks.Generator.BaseGenerator`1"),
            new HookSlot(
                "Stubs",
                null,
                "Processor",
                "ProcessorConfiguration",
                "processor",
                "processors/availableProcessors",
                "QaaS.Framework.SDK.Hooks.Processor.ITransactionProcessor",
                "QaaS.Framework.SDK.Hooks.Processor.BaseTransactionProcessor`1")
        ]);

    public static FamilyManifest Resolve(string family)
    {
        return family switch
        {
            "runner-family" => Runner,
            "mocker-family" => Mocker,
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unsupported family.")
        };
    }
}
