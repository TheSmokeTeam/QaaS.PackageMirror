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

            arrayProperty.Items.Add(GenerateInlineSchema(elementType));
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
        var currentSchemaType = schema.Type;
        if (currentSchemaType != JsonObjectType.String &&
            currentSchemaType != (JsonObjectType.String | JsonObjectType.Null) &&
            currentSchemaType != JsonObjectType.None)
        {
            schema.Type = currentSchemaType | JsonObjectType.String;
            schema.Pattern = @"\$\{.*\}";
        }

        foreach (var property in schema.Properties.Values)
        {
            AllowPlaceholderStrings(property);
        }

        foreach (var item in schema.Items)
        {
            AllowPlaceholderStrings(item);
        }

        foreach (var item in schema.OneOf)
        {
            AllowPlaceholderStrings(item);
        }

        foreach (var item in schema.AnyOf)
        {
            AllowPlaceholderStrings(item);
        }

        foreach (var item in schema.AllOf)
        {
            AllowPlaceholderStrings(item);
        }
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
            target.Items.Add(CloneSchema(source.Item));
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
}
