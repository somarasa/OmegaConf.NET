using System.Text.Json;
using OmegaConf.NET.Ast;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace OmegaConf.NET.Serialization;

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

    public static string ToYaml(ConfigNode node)
    {
        object? clrObject = ToClrObject(node);
        return YamlSerializer.Serialize(clrObject);
    }

    public static string ToJson(ConfigNode node, bool indented = true)
    {
        object? clrObject = ToClrObject(node);
        return JsonSerializer.Serialize(clrObject, indented ? IndentedJsonOptions : CompactJsonOptions);
    }

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

public static class ConfigNodeSerializationExtensions
{
    public static string ToYaml(this ConfigNode node) => ConfigSerializer.ToYaml(node);

    public static string ToJson(this ConfigNode node, bool indented = true) => ConfigSerializer.ToJson(node, indented);

    public static object? ToClrObject(this ConfigNode node) => ConfigSerializer.ToClrObject(node);
}
