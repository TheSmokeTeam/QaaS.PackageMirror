using NJsonSchema;
using NJsonSchema.Generation;
using Xunit;

namespace QaaS.PackageMirror.Tests;

public class SchemaTransformsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"${Variables:ColumnTypes}\"")]
    [InlineData("{\"price\":\"double precision\",\"created_at\":\"timestamp\"}")]
    public void StringMap_AcceptsSupportedConfiguration(string json)
    {
        var schema = GenerateColumnTypes();

        Assert.Empty(schema.Validate(json));
    }

    [Theory]
    [InlineData("{\"price\":{\"nested\":\"double precision\"}}")]
    [InlineData("{\"price\":[\"double precision\"]}")]
    [InlineData("{\"price\":123}")]
    [InlineData("{\"price\":true}")]
    public async Task StringMap_RejectsNonStringValuesAfterInliningAndSerialization(string json)
    {
        var schema = GenerateColumnTypes();
        var serialized = await JsonSchema.FromJsonAsync(schema.ToJson());

        Assert.NotEmpty(serialized.Validate(json));
    }

    [Theory]
    [InlineData("{\"count\":42}")]
    [InlineData("{\"count\":\"${Variables:Count}\"}")]
    public void NumericMap_PreservesValuesAndPlaceholderSupport(string json)
    {
        var transforms = CreateTransforms();
        var schema = transforms.GenerateInlineSchema(typeof(NumericMapConfiguration));
        transforms.AllowPlaceholderStrings(schema);

        Assert.Empty(schema.Properties["Values"].Validate(json));
    }

    [Fact]
    public void NumericMap_RejectsNonPlaceholderText()
    {
        var transforms = CreateTransforms();
        var schema = transforms.GenerateInlineSchema(typeof(NumericMapConfiguration));
        transforms.AllowPlaceholderStrings(schema);

        Assert.NotEmpty(schema.Properties["Values"].Validate("{\"count\":\"invalid\"}"));
    }

    [Theory]
    [InlineData("MsSqlTable")]
    [InlineData("OracleSqlTable")]
    [InlineData("PostgreSqlTable")]
    [InlineData("TrinoSqlTable")]
    public void SqlColumnTypes_AreAdvertisedOnlyForPublishers(string protocol)
    {
        var transforms = CreateTransforms();
        var root = CreateRunnerSchema(protocol);

        transforms.ApplyRunnerSqlColumnTypes(root);

        var actions = root.Properties["Sessions"].Item!;
        Assert.DoesNotContain(
            "ColumnTypes",
            actions.Properties["Consumers"].Item!.Properties[protocol].Properties.Keys
        );
        var columnTypes = actions.Properties["Publishers"].Item!.Properties[protocol].Properties[
            "ColumnTypes"
        ];
        Assert.Contains("nonempty", columnTypes.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("empty", columnTypes.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bulk", columnTypes.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlColumnTypes_OlderPackagesAndOtherProtocolsAreUnchanged()
    {
        var transforms = CreateTransforms();
        var root = CreateRunnerSchema("PostgreSqlTable");
        var publishers = root.Properties["Sessions"].Item!.Properties["Publishers"].Item!;
        publishers.Properties["PostgreSqlTable"].Properties.Remove("ColumnTypes");
        publishers.Properties["Http"] = new JsonSchemaProperty { Type = JsonObjectType.Object };
        publishers.Properties["Http"].Properties["Method"] = new JsonSchemaProperty
        {
            Type = JsonObjectType.String,
        };

        transforms.ApplyRunnerSqlColumnTypes(root);

        Assert.DoesNotContain(
            "ColumnTypes",
            publishers.Properties["PostgreSqlTable"].Properties.Keys
        );
        Assert.Single(publishers.Properties["Http"].Properties);
        transforms.ApplyRunnerSqlColumnTypes(new JsonSchema { Type = JsonObjectType.Object });
    }

    private static JsonSchema GenerateColumnTypes()
    {
        var transforms = CreateTransforms();
        var schema = transforms.GenerateConfigurationProperty(
            typeof(StringMapConfiguration),
            "SQL",
            null
        );
        transforms.AllowPlaceholderStrings(schema);
        return schema.Properties["ColumnTypes"];
    }

    private static SchemaTransforms CreateTransforms() =>
        new(
            new JsonSchemaGenerator(
                new SystemTextJsonSchemaGeneratorSettings
                {
                    AllowReferencesWithProperties = false,
                    GenerateCustomNullableProperties = true,
                    FlattenInheritanceHierarchy = true,
                }
            )
        );

    private static JsonSchema CreateRunnerSchema(string protocol)
    {
        var root = new JsonSchema { Type = JsonObjectType.Object };
        root.Properties["Sessions"] = new JsonSchemaProperty
        {
            Type = JsonObjectType.Array,
            Item = new JsonSchema { Type = JsonObjectType.Object },
        };
        foreach (var action in new[] { "Consumers", "Publishers" })
        {
            var actions = new JsonSchemaProperty
            {
                Type = JsonObjectType.Array,
                Item = new JsonSchema { Type = JsonObjectType.Object },
            };
            var sql = new JsonSchemaProperty { Type = JsonObjectType.Object };
            sql.Properties["ColumnTypes"] = new JsonSchemaProperty
            {
                Type = JsonObjectType.Object,
                Description = "When configured, chunk sends use row inserts.",
            };
            actions.Item.Properties[protocol] = sql;
            root.Properties["Sessions"].Item!.Properties[action] = actions;
        }
        return root;
    }

    public sealed class StringMapConfiguration
    {
        public Dictionary<string, string>? ColumnTypes { get; set; }
    }

    public sealed class NumericMapConfiguration
    {
        public Dictionary<string, int>? Values { get; set; }
    }
}
