using System.ComponentModel;
using System.Reflection;
using NJsonSchema;
using NJsonSchema.Generation;

internal sealed class SchemaTransforms(JsonSchemaGenerator generator)
{
    public JsonSchema GenerateInlineSchema(Type type)
    {
        var generatedSchema = generator.Generate(type);
        var inlineSchema = CloneSchema(generatedSchema);
        inlineSchema.Title = generatedSchema.Title;
        inlineSchema.Description = generatedSchema.Description;
        return inlineSchema;
    }

    public JsonSchemaProperty GenerateConfigurationProperty(Type configurationType, string title, string? description)
    {
        if (TryGetEnumerableElementType(configurationType, out var elementType))
        {
            var arrayProperty = new JsonSchemaProperty
            {
                Title = title,
                Description = description,
                Type = JsonObjectType.Array | JsonObjectType.Null
            };

            arrayProperty.Item = GenerateInlineSchema(elementType);
            return arrayProperty;
        }

        var configurationSchema = GenerateInlineSchema(configurationType);
        var configurationProperty = CloneProperty(configurationSchema);
        configurationProperty.Title = title;
        if (!string.IsNullOrWhiteSpace(description))
        {
            configurationProperty.Description = description;
        }

        return configurationProperty;
    }

    public void AllowPlaceholderStrings(JsonSchema schema)
    {
        AllowPlaceholderStringsCore(schema, new HashSet<JsonSchema>(ReferenceEqualityComparer.Instance));
    }

    public void AllowEnumNames(JsonSchema schema)
    {
        AllowEnumNamesCore(schema, new HashSet<JsonSchema>(ReferenceEqualityComparer.Instance));
    }

    public void MakeSelectorExtensible(JsonSchemaProperty selectorProperty, IReadOnlyCollection<string> knownValues)
    {
        selectorProperty.Type = JsonObjectType.String;
        selectorProperty.Enumeration.Clear();
        selectorProperty.AnyOf.Clear();
        selectorProperty.OneOf.Clear();
        selectorProperty.AllOf.Clear();

        var distinctKnownValues = knownValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (distinctKnownValues.Length == 0)
        {
            return;
        }

        var knownValuesSchema = new JsonSchema
        {
            Type = JsonObjectType.String
        };

        foreach (var knownValue in distinctKnownValues)
        {
            knownValuesSchema.Enumeration.Add(knownValue);
        }

        selectorProperty.AnyOf.Add(knownValuesSchema);
        selectorProperty.AnyOf.Add(new JsonSchema
        {
            Type = JsonObjectType.String
        });

        selectorProperty.ExtensionData ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        selectorProperty.ExtensionData["x-qaas-known-values"] = distinctKnownValues;
    }

    public void ApplyMockerServerDiscriminators(JsonSchema rootSchema)
    {
        if (rootSchema.Properties.TryGetValue("Server", out var serverProperty))
        {
            ApplyServerDiscriminator(serverProperty);
        }

        if (rootSchema.Properties.TryGetValue("Servers", out var serversProperty))
        {
            ApplyServerDiscriminator(ResolveArrayItemSchema(serversProperty));
        }
    }

    public void ApplyRunnerSessionDocumentation(JsonSchema rootSchema)
    {
        if (!rootSchema.Properties.TryGetValue("Sessions", out var sessionsProperty))
        {
            return;
        }

        var sessionSchema = ResolveArrayItemSchema(sessionsProperty);
        SetPropertyDescription(
            sessionSchema,
            "RunUntilStage",
            "Optional stage number that decides when the runner waits for this session to complete. " +
            "If omitted, the session becomes visible only after its own stage completes. " +
            "If set, the runner defers waiting until the configured future stage is reached.");

        if (!sessionSchema.Properties.TryGetValue("Stages", out var stagesProperty))
        {
            return;
        }

        stagesProperty.Description =
            "Optional per-stage configuration for the session's internal action stages. " +
            "Use this to override timing around a specific stage number without changing the action order.";

        var stageSchema = ResolveArrayItemSchema(stagesProperty);
        SetPropertyDescription(stageSchema, "StageNumber", "The internal session stage number this configuration applies to.");
        SetPropertyDescription(stageSchema, "TimeoutBefore", "Optional time in milliseconds to wait before starting this internal session stage.");
        SetPropertyDescription(stageSchema, "TimeoutAfter", "Optional time in milliseconds to wait after this internal session stage completes.");
    }

    private JsonSchema CloneSchema(JsonSchema source)
    {
        var actualSchema = source.ActualSchema.ActualTypeSchema;
        var clone = CreateSchemaShell(actualSchema, source.Title, source.Description ?? actualSchema.Description);
        CopySchemaMembers(actualSchema, clone);
        return clone;
    }

    private JsonSchemaProperty CloneProperty(JsonSchema source)
    {
        var actualSchema = source.ActualSchema.ActualTypeSchema;
        var clone = CreatePropertyShell(actualSchema, source.Title, source.Description ?? actualSchema.Description);
        CopySchemaMembers(actualSchema, clone);
        return clone;
    }

    private static JsonSchema CreateSchemaShell(JsonSchema source, string? title, string? description)
    {
        var clone = new JsonSchema
        {
            Title = title,
            Description = description,
            Type = source.Type,
            Format = source.Format,
            Pattern = source.Pattern,
            Maximum = source.Maximum,
            Minimum = source.Minimum,
            Default = source.Default,
            MinItems = source.MinItems,
            MaxItems = source.MaxItems
        };

        CopyEnumeration(source, clone);
        return clone;
    }

    private static JsonSchemaProperty CreatePropertyShell(JsonSchema source, string? title, string? description)
    {
        var clone = new JsonSchemaProperty
        {
            Title = title,
            Description = description,
            Type = source.Type,
            Format = source.Format,
            Pattern = source.Pattern,
            Maximum = source.Maximum,
            Minimum = source.Minimum,
            Default = source.Default,
            MinItems = source.MinItems,
            MaxItems = source.MaxItems
        };

        CopyEnumeration(source, clone);
        return clone;
    }

    private void CopySchemaMembers(JsonSchema source, JsonSchema target)
    {
        foreach (var requiredProperty in source.RequiredProperties)
        {
            target.RequiredProperties.Add(requiredProperty);
        }

        foreach (var property in source.ActualProperties)
        {
            target.Properties[property.Key] = CloneProperty(property.Value);
        }

        foreach (var anyOfSchema in source.AnyOf)
        {
            target.AnyOf.Add(CloneSchema(anyOfSchema));
        }

        foreach (var oneOfSchema in source.OneOf)
        {
            target.OneOf.Add(CloneSchema(oneOfSchema));
        }

        foreach (var allOfSchema in source.AllOf)
        {
            target.AllOf.Add(CloneSchema(allOfSchema));
        }

        if (source.Item is not null)
        {
            target.Item = CloneSchema(source.Item);
        }
        else
        {
            foreach (var item in source.Items)
            {
                target.Items.Add(CloneSchema(item));
            }
        }
    }

    private static void CopyEnumeration(JsonSchema source, JsonSchema target)
    {
        foreach (var enumerationValue in source.Enumeration)
        {
            target.Enumeration.Add(enumerationValue);
        }

        foreach (var enumerationName in source.EnumerationNames)
        {
            target.EnumerationNames.Add(enumerationName);
        }
    }

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type != typeof(string))
        {
            var genericEnumerable = type
                .GetInterfaces()
                .Concat([type])
                .FirstOrDefault(candidate =>
                    candidate.IsGenericType &&
                    candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

            if (genericEnumerable is not null)
            {
                elementType = genericEnumerable.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = type;
        return false;
    }

    private void ApplyServerDiscriminator(JsonSchema serverSchema)
    {
        serverSchema.Properties.Remove("Type");
        serverSchema.RequiredProperties.Remove("Type");
        serverSchema.AnyOf.Clear();
        serverSchema.OneOf.Clear();
        serverSchema.AllOf.Clear();

        var branches = new[]
        {
            new ServerBranch("Http"),
            new ServerBranch("Grpc"),
            new ServerBranch("Socket")
        };

        foreach (var branch in branches)
        {
            if (!serverSchema.Properties.TryGetValue(branch.ConfigurationPropertyName, out var configurationProperty))
            {
                continue;
            }

            var branchSchema = new JsonSchema
            {
                Type = JsonObjectType.Object,
                Title = branch.ConfigurationPropertyName,
                Description = serverSchema.Description
            };

            branchSchema.Properties[branch.ConfigurationPropertyName] = CloneProperty(configurationProperty);
            branchSchema.RequiredProperties.Add(branch.ConfigurationPropertyName);

            serverSchema.AnyOf.Add(branchSchema);
        }
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

    private static void AllowPlaceholderStringsCore(JsonSchema schema, HashSet<JsonSchema> visited)
    {
        if (!visited.Add(schema))
        {
            return;
        }

        var currentSchemaType = schema.Type;
        if ((currentSchemaType & JsonObjectType.String) == 0 &&
            currentSchemaType != JsonObjectType.None)
        {
            schema.Type = currentSchemaType | JsonObjectType.String;
            schema.Pattern = @"\$\{.*\}";
        }

        foreach (var property in schema.Properties.Values)
        {
            AllowPlaceholderStringsCore(property, visited);
        }

        foreach (var item in schema.Items)
        {
            AllowPlaceholderStringsCore(item, visited);
        }

        if (schema.Item is not null)
        {
            AllowPlaceholderStringsCore(schema.Item, visited);
        }

        foreach (var item in schema.OneOf)
        {
            AllowPlaceholderStringsCore(item, visited);
        }

        foreach (var item in schema.AnyOf)
        {
            AllowPlaceholderStringsCore(item, visited);
        }

        foreach (var item in schema.AllOf)
        {
            AllowPlaceholderStringsCore(item, visited);
        }
    }

    private static void AllowEnumNamesCore(JsonSchema schema, HashSet<JsonSchema> visited)
    {
        if (!visited.Add(schema))
        {
            return;
        }

        if (schema.EnumerationNames.Count > 0)
        {
            var enumerationNames = schema.EnumerationNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var originalEnumerationValues = schema.Enumeration.ToArray();
            var originalDefaultValue = schema.Default;

            schema.Type = NormalizeEnumType(schema.Type);
            schema.Format = null;
            schema.Enumeration.Clear();

            foreach (var enumerationName in enumerationNames)
            {
                schema.Enumeration.Add(enumerationName);
            }

            schema.Default = ConvertEnumDefaultToName(originalDefaultValue, originalEnumerationValues, enumerationNames);
        }

        foreach (var property in schema.Properties.Values)
        {
            AllowEnumNamesCore(property, visited);
        }

        foreach (var item in schema.Items)
        {
            AllowEnumNamesCore(item, visited);
        }

        if (schema.Item is not null)
        {
            AllowEnumNamesCore(schema.Item, visited);
        }

        foreach (var item in schema.OneOf)
        {
            AllowEnumNamesCore(item, visited);
        }

        foreach (var item in schema.AnyOf)
        {
            AllowEnumNamesCore(item, visited);
        }

        foreach (var item in schema.AllOf)
        {
            AllowEnumNamesCore(item, visited);
        }
    }

    private static JsonObjectType NormalizeEnumType(JsonObjectType type)
    {
        var normalizedType = type & ~JsonObjectType.Integer & ~JsonObjectType.Number;
        normalizedType |= JsonObjectType.String;
        return normalizedType == JsonObjectType.None ? JsonObjectType.String : normalizedType;
    }

    private static object? ConvertEnumDefaultToName(
        object? originalDefaultValue,
        IReadOnlyList<object?> originalEnumerationValues,
        IReadOnlyList<string> enumerationNames)
    {
        if (originalDefaultValue is null || enumerationNames.Count == 0)
        {
            return originalDefaultValue;
        }

        if (originalDefaultValue is string stringDefault &&
            enumerationNames.Contains(stringDefault, StringComparer.Ordinal))
        {
            return stringDefault;
        }

        for (var index = 0; index < originalEnumerationValues.Count && index < enumerationNames.Count; index++)
        {
            var enumerationValue = originalEnumerationValues[index];
            if (Equals(enumerationValue, originalDefaultValue) ||
                string.Equals(enumerationValue?.ToString(), originalDefaultValue.ToString(), StringComparison.Ordinal))
            {
                return enumerationNames[index];
            }
        }

        return originalDefaultValue;
    }

    private static void SetPropertyDescription(JsonSchema schema, string propertyName, string description)
    {
        if (schema.Properties.TryGetValue(propertyName, out var property))
        {
            property.Description = description;
        }
    }

    private sealed record ServerBranch(string ConfigurationPropertyName);
}
