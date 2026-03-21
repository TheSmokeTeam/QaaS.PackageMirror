using System.ComponentModel;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using NJsonSchema;
using NJsonSchema.Generation;
using NuGet.Versioning;

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
        var packagesRoot = Path.GetFullPath(arguments.PackagesRoot!);
        var loadContext = new PackageLoadContext(packagesRoot, arguments.PackageVersions);
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

            transforms.AllowEnumNames(schema);
            if (string.Equals(manifest.Id, "mocker-family", StringComparison.Ordinal))
            {
                transforms.ApplyMockerServerDiscriminators(schema);
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
            for (var attempt = 0; attempt < 5; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                if (loadContext.TryCleanup())
                {
                    break;
                }

                Thread.Sleep(200);
            }
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
        transforms.MakeSelectorExtensible(
            selectorProperty,
            hookDefinitions.SelectMany(definition => definition.AcceptedNamesOrEmpty).ToArray());

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

        itemSchema.AnyOf.Clear();
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

            itemSchema.AnyOf.Add(branch);
        }

        itemSchema.AnyOf.Add(CreateCustomHookFallbackBranch(slot, selectorProperty.Description, configurationProperty.Description));
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

    private static JsonSchema CreateCustomHookFallbackBranch(
        HookSlot slot,
        string? selectorDescription,
        string? configurationDescription)
    {
        var branch = new JsonSchema
        {
            Type = JsonObjectType.Object,
            Description = "Supports custom hook implementations that are not part of the mirrored common package set."
        };

        branch.Properties[slot.SelectorPropertyName] = new JsonSchemaProperty
        {
            Type = JsonObjectType.String,
            Description = selectorDescription
        };
        branch.RequiredProperties.Add(slot.SelectorPropertyName);
        branch.Properties[slot.ConfigurationPropertyName] = new JsonSchemaProperty
        {
            Type = JsonObjectType.Object | JsonObjectType.Null,
            Description = configurationDescription
        };

        return branch;
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

    private sealed class PackageLoadContext : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, string> _assemblyPaths;
        private readonly string _extractionRoot;

        public PackageLoadContext(string packagesRoot, IReadOnlyList<string> packageVersions)
            : base(isCollectible: true)
        {
            _extractionRoot = Path.Combine(
                Path.GetTempPath(),
                "qaas-family-schema-load",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_extractionRoot);
            _assemblyPaths = BuildAssemblyMap(packagesRoot, packageVersions, _extractionRoot);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            return _assemblyPaths.TryGetValue(assemblyName.Name, out var path)
                ? LoadFromAssemblyPath(path)
                : null;
        }

        public bool TryCleanup()
        {
            try
            {
                if (Directory.Exists(_extractionRoot))
                {
                    Directory.Delete(_extractionRoot, recursive: true);
                }

                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static IReadOnlyDictionary<string, string> BuildAssemblyMap(
            string packagesRoot,
            IReadOnlyList<string> packageVersions,
            string extractionRoot)
        {
            var selectedPackages = packageVersions
                .Select(ParsePackageVersion)
                .ToDictionary(
                    package => package.PackageId,
                    package => package.Version,
                    StringComparer.OrdinalIgnoreCase);

            var candidates = new Dictionary<string, AssemblyCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var packageArchive in EnumeratePackageArchives(packagesRoot))
            {
                foreach (var dllEntry in ExtractManagedAssemblyPaths(packageArchive, extractionRoot))
                {
                    AssemblyName assemblyName;
                    try
                    {
                        assemblyName = AssemblyName.GetAssemblyName(dllEntry.Path);
                    }
                    catch (BadImageFormatException)
                    {
                        continue;
                    }
                    catch (FileLoadException)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(assemblyName.Name))
                    {
                        continue;
                    }

                    var candidate = new AssemblyCandidate(
                        packageArchive.PackageId,
                        packageArchive.Version,
                        dllEntry.Path,
                        selectedPackages.TryGetValue(packageArchive.PackageId, out var selectedVersion) &&
                        string.Equals(selectedVersion, packageArchive.Version, StringComparison.OrdinalIgnoreCase),
                        packageArchive.Bucket.Equals("qaas", StringComparison.OrdinalIgnoreCase),
                        GetPathRank(dllEntry.RelativePath));

                    if (!candidates.TryGetValue(assemblyName.Name, out var current) || Compare(candidate, current) < 0)
                    {
                        candidates[assemblyName.Name] = candidate;
                    }
                }
            }

            return candidates.ToDictionary(pair => pair.Key, pair => pair.Value.Path, StringComparer.OrdinalIgnoreCase);
        }

        private static int Compare(AssemblyCandidate left, AssemblyCandidate right)
        {
            var selectedComparison = CompareBool(right.IsSelectedFamilyPackage, left.IsSelectedFamilyPackage);
            if (selectedComparison != 0)
            {
                return selectedComparison;
            }

            var qaasComparison = CompareBool(right.IsQaasPackage, left.IsQaasPackage);
            if (qaasComparison != 0)
            {
                return qaasComparison;
            }

            var versionComparison = NuGetVersion.Parse(right.Version).CompareTo(NuGetVersion.Parse(left.Version));
            if (versionComparison != 0)
            {
                return versionComparison;
            }

            var pathComparison = right.PathRank.CompareTo(left.PathRank);
            if (pathComparison != 0)
            {
                return pathComparison;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
        }

        private static int CompareBool(bool left, bool right) => left == right ? 0 : left ? 1 : -1;

        private static (string PackageId, string Version) ParsePackageVersion(string raw)
        {
            var separatorIndex = raw.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex == raw.Length - 1)
            {
                throw new InvalidOperationException($"Invalid package specification '{raw}'. Expected PackageId=Version.");
            }

            return (raw[..separatorIndex], raw[(separatorIndex + 1)..]);
        }

        private static IEnumerable<PackageArchive> EnumeratePackageArchives(string packagesRoot)
        {
            foreach (var bucketDirectory in Directory.EnumerateDirectories(packagesRoot))
            {
                var bucketName = Path.GetFileName(bucketDirectory);
                foreach (var packageDirectory in Directory.EnumerateDirectories(bucketDirectory))
                {
                    var packageId = Path.GetFileName(packageDirectory);
                    foreach (var versionDirectory in Directory.EnumerateDirectories(packageDirectory))
                    {
                        var packageArchivePath = Directory.EnumerateFiles(versionDirectory, "*.nupkg", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault();
                        if (packageArchivePath is null)
                        {
                            continue;
                        }

                        yield return new PackageArchive(
                            bucketName,
                            packageId,
                            Path.GetFileName(versionDirectory),
                            packageArchivePath);
                    }
                }
            }
        }

        private static IEnumerable<ExtractedAssemblyPath> ExtractManagedAssemblyPaths(
            PackageArchive packageArchive,
            string extractionRoot)
        {
            using var archive = ZipFile.OpenRead(packageArchive.PackageArchivePath);
            foreach (var entry in archive.Entries.Where(entry =>
                         entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
                         entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var destinationPath = Path.Combine(
                    extractionRoot,
                    packageArchive.PackageId,
                    packageArchive.Version,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, overwrite: true);
                yield return new ExtractedAssemblyPath(destinationPath, entry.FullName);
            }
        }

        private static int GetPathRank(string relativePath)
        {
            relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            if (relativePath.StartsWith($"lib{Path.DirectorySeparatorChar}net10", StringComparison.OrdinalIgnoreCase))
            {
                return 100;
            }

            if (relativePath.StartsWith($"lib{Path.DirectorySeparatorChar}net9", StringComparison.OrdinalIgnoreCase))
            {
                return 90;
            }

            if (relativePath.StartsWith($"lib{Path.DirectorySeparatorChar}net8", StringComparison.OrdinalIgnoreCase))
            {
                return 80;
            }

            if (relativePath.StartsWith($"lib{Path.DirectorySeparatorChar}net7", StringComparison.OrdinalIgnoreCase))
            {
                return 70;
            }

            if (relativePath.StartsWith($"lib{Path.DirectorySeparatorChar}net6", StringComparison.OrdinalIgnoreCase))
            {
                return 60;
            }

            if (relativePath.StartsWith($"lib{Path.DirectorySeparatorChar}netstandard2.1", StringComparison.OrdinalIgnoreCase))
            {
                return 50;
            }

            if (relativePath.StartsWith($"lib{Path.DirectorySeparatorChar}netstandard2.0", StringComparison.OrdinalIgnoreCase))
            {
                return 40;
            }

            if (relativePath.StartsWith($"lib{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return 30;
            }

            return 0;
        }
    }
}

internal sealed record PackageArchive(string Bucket, string PackageId, string Version, string PackageArchivePath);
internal sealed record ExtractedAssemblyPath(string Path, string RelativePath);
internal sealed record AssemblyCandidate(
    string PackageId,
    string Version,
    string Path,
    bool IsSelectedFamilyPackage,
    bool IsQaasPackage,
    int PathRank);

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
