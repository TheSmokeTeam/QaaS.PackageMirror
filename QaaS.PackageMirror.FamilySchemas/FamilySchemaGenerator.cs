using System.ComponentModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using NJsonSchema;
using NJsonSchema.Generation;

internal sealed class FamilySchemaGenerator
{
    private readonly JsonSchemaGenerator _generator = new(new SystemTextJsonSchemaGeneratorSettings
    {
        AllowReferencesWithProperties = false,
        GenerateAbstractProperties = true,
        GenerateKnownTypes = true,
        GenerateEnumMappingDescription = true,
        GenerateCustomNullableProperties = true,
        FlattenInheritanceHierarchy = true
    });

    public FamilySchemaResult Generate(FamilyManifest manifest, CliArguments arguments)
    {
        var resolverAppPath = Path.GetFullPath(arguments.ResolverAppPath!);
        var loadContext = new ResolverLoadContext(resolverAppPath);
        try
        {
            var loadedAssemblies = manifest.AssembliesToLoad
                .Select(name => loadContext.LoadFromAssemblyName(new AssemblyName(name)))
                .Distinct()
                .ToList();

            var rootAssembly = loadedAssemblies.Single(assembly =>
                string.Equals(assembly.GetName().Name, manifest.RootAssemblyName, StringComparison.Ordinal));
            var rootType = rootAssembly.GetType(manifest.RootTypeFullName, throwOnError: true)!;

            var transforms = new SchemaTransforms(_generator);
            var schema = transforms.GenerateInlineSchema(rootType);
            schema.Title = manifest.DisplayName;
            schema.Description = $"{manifest.DisplayName} JSON schema";

            foreach (var slot in manifest.HookSlots)
            {
                var interfaceType = ResolveType(slot.HookInterfaceFullName, loadContext.Assemblies);
                var hookDefinitions = DiscoverHooks(slot, interfaceType, loadContext.Assemblies)
                    .OrderBy(definition => definition.Title, StringComparer.Ordinal)
                    .ToArray();
                ApplySlot(schema, slot, hookDefinitions, transforms);
            }

            transforms.AllowPlaceholderStrings(schema);
            schema.ExtensionData ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            schema.ExtensionData["x-qaas-family"] = manifest.Id;
            schema.ExtensionData["x-qaas-generated-at-utc"] = DateTimeOffset.UtcNow.ToString("O");

            var metadata = CreateMetadata(manifest, arguments, schema);
            return new FamilySchemaResult(schema, metadata);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static Type ResolveType(string fullName, IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var type = GetLoadableTypes(assembly).FirstOrDefault(candidate =>
                string.Equals(candidate.FullName, fullName, StringComparison.Ordinal));
            if (type is not null)
            {
                return type;
            }
        }

        throw new InvalidOperationException($"Could not resolve type '{fullName}'.");
    }

    private static IEnumerable<HookDefinition> DiscoverHooks(
        HookSlot slot,
        Type interfaceType,
        IEnumerable<Assembly> assemblies)
    {
        var discovered = assemblies
            .Where(assembly => assembly.GetName().Name?.StartsWith("QaaS.", StringComparison.Ordinal) == true)
            .SelectMany(GetLoadableTypes)
            .Where(type => type is { IsClass: true, IsAbstract: false } && interfaceType.IsAssignableFrom(type))
            .Select(type => new
            {
                HookType = type,
                ConfigurationType = ResolveConfigurationType(type, slot.GenericBaseTypeFullName)
            })
            .Where(candidate => candidate.ConfigurationType is not null)
            .Select(candidate => new HookDefinition(candidate.HookType, candidate.ConfigurationType!))
            .ToList();

        var simpleNameCounts = discovered
            .GroupBy(definition => definition.HookType.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var definition in discovered)
        {
            var acceptedNames = new List<string>();
            if (simpleNameCounts.GetValueOrDefault(definition.HookType.Name) == 1)
            {
                acceptedNames.Add(definition.HookType.Name);
            }

            acceptedNames.Add(definition.HookType.FullName!);
            yield return definition with
            {
                AcceptedNames = acceptedNames,
                Title = definition.HookType.Name,
                Description = definition.ConfigurationType.GetCustomAttribute<DescriptionAttribute>()?.Description
            };
        }
    }

    private static Type? ResolveConfigurationType(Type hookType, string genericBaseTypeFullName)
    {
        for (var current = hookType; current is not null && current != typeof(object); current = current.BaseType!)
        {
            if (!current.IsGenericType)
            {
                continue;
            }

            var openGenericType = current.GetGenericTypeDefinition();
            if (!string.Equals(openGenericType.FullName, genericBaseTypeFullName, StringComparison.Ordinal))
            {
                continue;
            }

            return current.GetGenericArguments()[0];
        }

        return null;
    }

    private static void ApplySlot(
        JsonSchema rootSchema,
        HookSlot slot,
        IReadOnlyList<HookDefinition> hookDefinitions,
        SchemaTransforms transforms)
    {
        if (hookDefinitions.Count == 0)
        {
            return;
        }

        var itemSchema = ResolveItemSchema(rootSchema, slot);

        if (!itemSchema.Properties.TryGetValue(slot.SelectorPropertyName, out var selectorProperty))
        {
            throw new InvalidOperationException(
                $"Could not find selector property '{slot.SelectorPropertyName}' on '{slot.CollectionPropertyName}'.");
        }

        selectorProperty.Type = JsonObjectType.String;
        selectorProperty.Enumeration.Clear();
        foreach (var acceptedName in hookDefinitions.SelectMany(definition => definition.AcceptedNamesOrEmpty).Distinct())
        {
            selectorProperty.Enumeration.Add(acceptedName);
        }

        if (!itemSchema.Properties.TryGetValue(slot.ConfigurationPropertyName, out var configurationProperty))
        {
            throw new InvalidOperationException(
                $"Could not find configuration property '{slot.ConfigurationPropertyName}' on '{slot.CollectionPropertyName}'.");
        }

        configurationProperty.Type = JsonObjectType.Object | JsonObjectType.Null;
        configurationProperty.Properties.Clear();
        configurationProperty.AnyOf.Clear();
        configurationProperty.OneOf.Clear();
        configurationProperty.AllOf.Clear();
        configurationProperty.Items.Clear();

        itemSchema.OneOf.Clear();
        foreach (var hookDefinition in hookDefinitions)
        {
            var branch = new JsonSchema
            {
                Type = JsonObjectType.Object,
                Title = hookDefinition.Title,
                Description = hookDefinition.Description
            };

            var branchSelector = new JsonSchemaProperty
            {
                Type = JsonObjectType.String,
                Description = selectorProperty.Description
            };

            foreach (var acceptedName in hookDefinition.AcceptedNamesOrEmpty)
            {
                branchSelector.Enumeration.Add(acceptedName);
            }

            branch.Properties[slot.SelectorPropertyName] = branchSelector;
            branch.RequiredProperties.Add(slot.SelectorPropertyName);
            branch.Properties[slot.ConfigurationPropertyName] =
                transforms.GenerateConfigurationProperty(
                    hookDefinition.ConfigurationType,
                    hookDefinition.Title ?? hookDefinition.HookType.Name,
                    hookDefinition.Description);

            itemSchema.OneOf.Add(branch);
        }
    }

    private static JsonSchema ResolveItemSchema(JsonSchema rootSchema, HookSlot slot)
    {
        var collectionProperty = rootSchema.Properties[slot.CollectionPropertyName];
        var collectionItemSchema = ResolveArrayItemSchema(collectionProperty);
        if (slot.NestedCollectionPropertyName is null)
        {
            return collectionItemSchema;
        }

        if (!collectionItemSchema.Properties.TryGetValue(slot.NestedCollectionPropertyName, out var nestedCollectionProperty))
        {
            throw new InvalidOperationException(
                $"Could not find nested collection '{slot.NestedCollectionPropertyName}' under '{slot.CollectionPropertyName}'.");
        }

        return ResolveArrayItemSchema(nestedCollectionProperty);
    }

    private static JsonSchema ResolveArrayItemSchema(JsonSchema schema)
    {
        if (schema.Item is not null)
        {
            return schema.Item;
        }

        if (schema.Items.Count > 0)
        {
            return schema.Items.First();
        }

        throw new InvalidOperationException("Expected array schema to have an item definition.");
    }

    private static FamilySchemaMetadata CreateMetadata(
        FamilyManifest manifest,
        CliArguments arguments,
        JsonSchema schema)
    {
        var packageVersions = arguments.PackageVersions
            .Select(raw =>
            {
                var separatorIndex = raw.IndexOf('=', StringComparison.Ordinal);
                if (separatorIndex <= 0 || separatorIndex == raw.Length - 1)
                {
                    throw new InvalidOperationException($"Invalid package specification '{raw}'. Expected PackageId=Version.");
                }

                return new FamilySchemaPackageVersion(raw[..separatorIndex], raw[(separatorIndex + 1)..]);
            })
            .OrderBy(package => package.PackageId, StringComparer.Ordinal)
            .ToArray();

        var signature = string.Join("|", packageVersions.Select(package => $"{package.PackageId}={package.Version}"));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant()[..12];

        return new FamilySchemaMetadata(
            manifest.Id,
            manifest.DisplayName,
            DateTimeOffset.UtcNow,
            arguments.SnapshotId!,
            hash,
            arguments.TriggerRepository,
            arguments.TriggerTag,
            arguments.TriggerRunId,
            arguments.TriggerOrigin,
            packageVersions,
            schema.Properties.Count);
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null)!;
        }
    }

    private sealed class ResolverLoadContext(string resolverAppPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(resolverAppPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

internal sealed record HookDefinition(
    Type HookType,
    Type ConfigurationType,
    IReadOnlyList<string>? AcceptedNames = null,
    string? Title = null,
    string? Description = null)
{
    public IReadOnlyList<string> AcceptedNamesOrEmpty => AcceptedNames ?? Array.Empty<string>();
}

internal sealed record FamilySchemaResult(JsonSchema Schema, FamilySchemaMetadata Metadata);

internal sealed record FamilySchemaMetadata(
    string FamilyId,
    string DisplayName,
    DateTimeOffset GeneratedAtUtc,
    string SnapshotId,
    string PackageSignatureHash,
    string? TriggerRepository,
    string? TriggerTag,
    string? TriggerRunId,
    string? TriggerOrigin,
    IReadOnlyList<FamilySchemaPackageVersion> Packages,
    int RootPropertyCount);

internal sealed record FamilySchemaPackageVersion(string PackageId, string Version);
