internal sealed record FamilyManifest(
    string Id,
    string DisplayName,
    string RootAssemblyName,
    string RootTypeFullName,
    IReadOnlyList<string> PackageIds,
    IReadOnlyList<string> AssembliesToLoad,
    IReadOnlyList<HookSlot> HookSlots);

internal sealed record HookSlot(
    string CollectionPropertyName,
    string? NestedCollectionPropertyName,
    string SelectorPropertyName,
    string ConfigurationPropertyName,
    string HookInterfaceFullName,
    string GenericBaseTypeFullName);

internal static class FamilyManifests
{
    private static readonly FamilyManifest Runner = new(
        Id: "runner-family",
        DisplayName: "QaaS Runner Family",
        RootAssemblyName: "QaaS.Runner",
        RootTypeFullName: "QaaS.Runner.ExecutionBuilder",
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
        HookSlots:
        [
            new HookSlot(
                "DataSources",
                null,
                "Generator",
                "GeneratorConfiguration",
                "QaaS.Framework.SDK.Hooks.Generator.IGenerator",
                "QaaS.Framework.SDK.Hooks.Generator.BaseGenerator`1"),
            new HookSlot(
                "Assertions",
                null,
                "Assertion",
                "AssertionConfiguration",
                "QaaS.Framework.SDK.Hooks.Assertion.IAssertion",
                "QaaS.Framework.SDK.Hooks.Assertion.BaseAssertion`1"),
            new HookSlot(
                "Sessions",
                "Probes",
                "Probe",
                "ProbeConfiguration",
                "QaaS.Framework.SDK.Hooks.Probe.IProbe",
                "QaaS.Framework.SDK.Hooks.Probe.BaseProbe`1")
        ]);

    private static readonly FamilyManifest Mocker = new(
        Id: "mocker-family",
        DisplayName: "QaaS Mocker Family",
        RootAssemblyName: "QaaS.Mocker",
        RootTypeFullName: "QaaS.Mocker.ExecutionBuilder",
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
        HookSlots:
        [
            new HookSlot(
                "DataSources",
                null,
                "Generator",
                "GeneratorConfiguration",
                "QaaS.Framework.SDK.Hooks.Generator.IGenerator",
                "QaaS.Framework.SDK.Hooks.Generator.BaseGenerator`1"),
            new HookSlot(
                "Stubs",
                null,
                "Processor",
                "ProcessorSpecificConfiguration",
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
