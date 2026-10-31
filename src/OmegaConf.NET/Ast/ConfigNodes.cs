using OmegaConf.NET.Common;

namespace OmegaConf.NET.Ast;

/// <summary>
/// Abstract base node in the OmegaConf AST.
/// </summary>
public abstract class ConfigNode
{
    public SourceInfo Source { get; set; } = SourceInfo.Unknown;

    public ConfigOperator Operator { get; set; } = ConfigOperator.Assign;

    public abstract ConfigNodeType NodeType { get; }

    public abstract ConfigNode Clone();
}

/// <summary>
/// Represents a scalar value (string, integer, double, boolean, null, or '???').
/// </summary>
public sealed class ConfigValueNode : ConfigNode
{
    public const string MissingMarker = "???";

    public object? Value { get; set; }

    /// <summary>
    /// Preserves the raw unresolved template string (e.g. "${db.host}:${db.port}") for future write-back.
    /// </summary>
    public string? RawTemplate { get; set; }

    public bool IsMissingMarker => Value is string s && s == MissingMarker;

    public override ConfigNodeType NodeType => ConfigNodeType.Value;

    public ConfigValueNode(object? value, ConfigOperator op = ConfigOperator.Assign, SourceInfo? source = null)
    {
        Value = value;
        if (value is string s && s.Contains("${"))
        {
            RawTemplate = s;
        }
        Operator = op;
        Source = source ?? SourceInfo.Unknown;
    }

    public override ConfigNode Clone() => new ConfigValueNode(Value, Operator, Source)
    {
        RawTemplate = RawTemplate
    };

    public override string ToString() => IsMissingMarker ? MissingMarker : Value?.ToString() ?? "null";
}

/// <summary>
/// Represents a list/sequence of configuration nodes.
/// </summary>
public sealed class ConfigListNode : ConfigNode
{
    public List<ConfigNode> Items { get; }

    public override ConfigNodeType NodeType => ConfigNodeType.List;

    public ConfigListNode(IEnumerable<ConfigNode>? items = null, ConfigOperator op = ConfigOperator.Assign, SourceInfo? source = null)
    {
        Items = items != null ? new List<ConfigNode>(items) : new List<ConfigNode>();
        Operator = op;
        Source = source ?? SourceInfo.Unknown;
    }

    public override ConfigNode Clone()
    {
        var cloned = new ConfigListNode(Items.Select(x => x.Clone()), Operator, Source);
        return cloned;
    }

    public override string ToString() => $"[{string.Join(", ", Items)}]";
}

/// <summary>
/// Represents a key-value dictionary/mapping of configuration nodes.
/// </summary>
public sealed class ConfigObjectNode : ConfigNode
{
    public Dictionary<string, ConfigNode> Children { get; }

    public override ConfigNodeType NodeType => ConfigNodeType.Object;

    public ConfigObjectNode(ConfigOperator op = ConfigOperator.Assign, SourceInfo? source = null)
    {
        Children = new Dictionary<string, ConfigNode>(StringComparer.OrdinalIgnoreCase);
        Operator = op;
        Source = source ?? SourceInfo.Unknown;
    }

    public ConfigObjectNode(IDictionary<string, ConfigNode> children, ConfigOperator op = ConfigOperator.Assign, SourceInfo? source = null)
    {
        Children = new Dictionary<string, ConfigNode>(children, StringComparer.OrdinalIgnoreCase);
        Operator = op;
        Source = source ?? SourceInfo.Unknown;
    }

    public bool ContainsKey(string key) => Children.ContainsKey(key);

    public bool TryGetChild(string key, out ConfigNode? node) => Children.TryGetValue(key, out node);

    public ConfigNode? GetChild(string key) => Children.TryGetValue(key, out var node) ? node : null;

    public void SetChild(string key, ConfigNode node) => Children[key] = node;

    public bool RemoveChild(string key) => Children.Remove(key);

    public override ConfigNode Clone()
    {
        var cloned = new ConfigObjectNode(Operator, Source);
        foreach (var (k, v) in Children)
        {
            cloned.Children[k] = v.Clone();
        }
        return cloned;
    }

    public override string ToString() => $"{{{string.Join(", ", Children.Select(kv => $"{kv.Key}: {kv.Value}"))}}}";
}
