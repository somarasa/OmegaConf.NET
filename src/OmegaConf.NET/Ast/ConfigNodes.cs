using OmegaConf.NET.Common;

namespace OmegaConf.NET.Ast;

/// <summary>
/// Abstract base node in the OmegaConf Abstract Syntax Tree (AST).
/// Tracks origin file/line metadata (<see cref="Source"/>) and any pending mutation operator (<see cref="Operator"/>).
/// </summary>
public abstract class ConfigNode
{
    /// <summary>
    /// Gets or sets the file, line, and column where this configuration node was defined.
    /// </summary>
    public SourceInfo Source { get; set; } = SourceInfo.Unknown;

    /// <summary>
    /// Gets or sets the mutation operator associated with this node when merging onto a base layer.
    /// Defaults to <see cref="ConfigOperator.Assign"/> (<c>=</c>).
    /// </summary>
    public ConfigOperator Operator { get; set; } = ConfigOperator.Assign;

    /// <summary>
    /// Gets the structural kind of this AST node (Value, List, or Object).
    /// </summary>
    public abstract ConfigNodeType NodeType { get; }

    /// <summary>
    /// Creates a deep copy of this AST node and all of its children so merges never mutate original trees.
    /// </summary>
    public abstract ConfigNode Clone();
}

/// <summary>
/// Represents a scalar configuration value (string, integer, double, boolean, null, or mandatory marker <c>'???'</c>).
/// </summary>
public sealed class ConfigValueNode : ConfigNode
{
    /// <summary>
    /// Hydra/OmegaConf marker string (<c>"???"</c>) indicating a value that must be supplied by a child config or CLI override.
    /// </summary>
    public const string MissingMarker = "???";

    /// <summary>
    /// Gets or sets the underlying scalar CLR value (<see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, or <c>null</c>).
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// Preserves the raw unresolved interpolation template string (e.g. <c>"${db.host}:${db.port}"</c>)
    /// after <see cref="Value"/> is resolved, enabling diagnostics and future template write-back.
    /// </summary>
    public string? RawTemplate { get; set; }

    /// <summary>
    /// Gets a value indicating whether this node is currently set to the mandatory missing marker (<c>"???"</c>).
    /// </summary>
    public bool IsMissingMarker => Value is string s && s == MissingMarker;

    /// <inheritdoc />
    public override ConfigNodeType NodeType => ConfigNodeType.Value;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigValueNode"/> class.
    /// </summary>
    /// <param name="value">The scalar CLR value.</param>
    /// <param name="op">The mutation operator to apply during merging.</param>
    /// <param name="source">Optional source location metadata.</param>
    public ConfigValueNode(object? value, ConfigOperator op = ConfigOperator.Assign, SourceInfo? source = null)
    {
        Value = value;
        // Automatically capture the raw template if the initial string contains an interpolation token
        if (value is string s && s.Contains("${"))
        {
            RawTemplate = s;
        }
        Operator = op;
        Source = source ?? SourceInfo.Unknown;
    }

    /// <inheritdoc />
    public override ConfigNode Clone() => new ConfigValueNode(Value, Operator, Source)
    {
        RawTemplate = RawTemplate
    };

    /// <inheritdoc />
    public override string ToString() => IsMissingMarker ? MissingMarker : Value?.ToString() ?? "null";
}

/// <summary>
/// Represents an ordered list/sequence of configuration nodes.
/// Supports <c>+=</c> (append items) and <c>-=</c> (remove matching items) mutation operators.
/// </summary>
public sealed class ConfigListNode : ConfigNode
{
    /// <summary>
    /// Gets the mutable list of child AST nodes in this sequence.
    /// </summary>
    public List<ConfigNode> Items { get; }

    /// <inheritdoc />
    public override ConfigNodeType NodeType => ConfigNodeType.List;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigListNode"/> class.
    /// </summary>
    /// <param name="items">Optional initial elements.</param>
    /// <param name="op">The mutation operator to apply during merging.</param>
    /// <param name="source">Optional source location metadata.</param>
    public ConfigListNode(IEnumerable<ConfigNode>? items = null, ConfigOperator op = ConfigOperator.Assign, SourceInfo? source = null)
    {
        Items = items != null ? new List<ConfigNode>(items) : new List<ConfigNode>();
        Operator = op;
        Source = source ?? SourceInfo.Unknown;
    }

    /// <inheritdoc />
    public override ConfigNode Clone()
    {
        var cloned = new ConfigListNode(Items.Select(x => x.Clone()), Operator, Source);
        return cloned;
    }

    /// <inheritdoc />
    public override string ToString() => $"[{string.Join(", ", Items)}]";
}

/// <summary>
/// Represents a hierarchical key-value dictionary/mapping of configuration nodes.
/// Keys are matched using <see cref="StringComparer.OrdinalIgnoreCase"/>.
/// </summary>
public sealed class ConfigObjectNode : ConfigNode
{
    /// <summary>
    /// Gets the case-insensitive dictionary of child configuration nodes keyed by property name.
    /// </summary>
    public Dictionary<string, ConfigNode> Children { get; }

    /// <inheritdoc />
    public override ConfigNodeType NodeType => ConfigNodeType.Object;

    /// <summary>
    /// Initializes an empty <see cref="ConfigObjectNode"/>.
    /// </summary>
    public ConfigObjectNode(ConfigOperator op = ConfigOperator.Assign, SourceInfo? source = null)
    {
        Children = new Dictionary<string, ConfigNode>(StringComparer.OrdinalIgnoreCase);
        Operator = op;
        Source = source ?? SourceInfo.Unknown;
    }

    /// <summary>
    /// Initializes a <see cref="ConfigObjectNode"/> populated with the specified child entries.
    /// </summary>
    public ConfigObjectNode(IDictionary<string, ConfigNode> children, ConfigOperator op = ConfigOperator.Assign, SourceInfo? source = null)
    {
        Children = new Dictionary<string, ConfigNode>(children, StringComparer.OrdinalIgnoreCase);
        Operator = op;
        Source = source ?? SourceInfo.Unknown;
    }

    /// <summary>
    /// Determines whether this object contains a child node with the specified key (case-insensitive).
    /// </summary>
    public bool ContainsKey(string key) => Children.ContainsKey(key);

    /// <summary>
    /// Attempts to retrieve a child node by key (case-insensitive).
    /// </summary>
    public bool TryGetChild(string key, out ConfigNode? node) => Children.TryGetValue(key, out node);

    /// <summary>
    /// Gets a child node by key (case-insensitive), or <c>null</c> if the key does not exist.
    /// </summary>
    public ConfigNode? GetChild(string key) => Children.TryGetValue(key, out var node) ? node : null;

    /// <summary>
    /// Sets or replaces a child node for the specified key.
    /// </summary>
    public void SetChild(string key, ConfigNode node) => Children[key] = node;

    /// <summary>
    /// Removes the child node with the specified key.
    /// </summary>
    public bool RemoveChild(string key) => Children.Remove(key);

    /// <inheritdoc />
    public override ConfigNode Clone()
    {
        var cloned = new ConfigObjectNode(Operator, Source);
        foreach (var (k, v) in Children)
        {
            cloned.Children[k] = v.Clone();
        }
        return cloned;
    }

    /// <inheritdoc />
    public override string ToString() => $"{{{string.Join(", ", Children.Select(kv => $"{kv.Key}: {kv.Value}"))}}}";
}
