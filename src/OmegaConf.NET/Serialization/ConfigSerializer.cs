using System.Text.Json;
using OmegaConf.NET.Ast;
using Tomlyn;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OmegaConf.NET.Serialization;

/// <summary>
/// Exports resolved <see cref="ConfigNode"/> AST trees back into YAML, JSON, TOML, or plain CLR dictionaries/lists.
/// </summary>
public static class ConfigSerializer
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .Build();

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Serializes a <see cref="ConfigNode"/> tree into a YAML document string.
    /// </summary>
    public static string ToYaml(ConfigNode node)
    {
        object? clrObject = ToClrObject(node);
        return YamlSerializer.Serialize(clrObject);
    }

    /// <summary>
    /// Serializes a <see cref="ConfigNode"/> tree into a JSON document string.
    /// </summary>
    /// <param name="node">The AST node to serialize.</param>
    /// <param name="indented">Whether to pretty-print with indentation (defaults to <c>true</c>).</param>
    public static string ToJson(ConfigNode node, bool indented = true)
    {
        object? clrObject = ToClrObject(node);
        return JsonSerializer.Serialize(clrObject, indented ? IndentedJsonOptions : CompactJsonOptions);
    }

    /// <summary>
    /// Serializes a <see cref="ConfigNode"/> tree into a TOML document string.
    /// </summary>
    public static string ToToml(ConfigNode node)
    {
        object? clrObject = ToClrObject(node);
        return clrObject != null ? Toml.FromModel(clrObject) : "";
    }

    /// <summary>
    /// Recursively converts a <see cref="ConfigNode"/> AST into standard CLR primitives,
    /// <see cref="List{T}"/>, and <see cref="Dictionary{TKey,TValue}"/> instances for serializer consumption.
    /// </summary>
    public static object? ToClrObject(ConfigNode? node)
    {
        if (node == null) return null;

        return node switch
        {
            ConfigValueNode val => val.IsMissingMarker ? "???" : val.Value,
            ConfigListNode list => list.Items.Select(ToClrObject).ToList(),
            ConfigObjectNode obj => obj.Children.ToDictionary(kv => kv.Key, kv => ToClrObject(kv.Value)),
            _ => null
        };
    }
}

/// <summary>
/// Fluent extension methods on <see cref="ConfigNode"/> for exporting to YAML, JSON, TOML, or CLR objects.
/// </summary>
public static class ConfigNodeSerializationExtensions
{
    /// <inheritdoc cref="ConfigSerializer.ToYaml(ConfigNode)"/>
    public static string ToYaml(this ConfigNode node) => ConfigSerializer.ToYaml(node);

    /// <inheritdoc cref="ConfigSerializer.ToJson(ConfigNode, bool)"/>
    public static string ToJson(this ConfigNode node, bool indented = true) => ConfigSerializer.ToJson(node, indented);

    /// <inheritdoc cref="ConfigSerializer.ToToml(ConfigNode)"/>
    public static string ToToml(this ConfigNode node) => ConfigSerializer.ToToml(node);

    /// <inheritdoc cref="ConfigSerializer.ToClrObject(ConfigNode?)"/>
    public static object? ToClrObject(this ConfigNode node) => ConfigSerializer.ToClrObject(node);
}
