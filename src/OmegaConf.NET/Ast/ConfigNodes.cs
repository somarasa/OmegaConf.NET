using OmegaConf.NET.Binding;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Ast;

/// <summary>
/// Abstract base node in the OmegaConf Abstract Syntax Tree (AST).
/// Tracks origin file/line metadata (<see cref="Source"/>), any pending mutation operator (<see cref="Operator"/>),
/// and immutability state (<see cref="IsReadOnly"/>).
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
    /// Gets a value indicating whether this node is locked as read-only.
    /// When <c>true</c>, any attempt to modify the node throws <see cref="ConfigReadOnlyException"/>.
    /// </summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>
    /// Gets the structural kind of this AST node (Value, List, or Object).
    /// </summary>
    public abstract ConfigNodeType NodeType { get; }

    /// <summary>
    /// Recursively sets the read-only flag on this node and all of its descendant nodes.
    /// Equivalent to <c>OmegaConf.set_readonly(cfg, readOnly)</c>.
    /// </summary>
    /// <param name="readOnly">Whether to lock (<c>true</c>) or unlock (<c>false</c>) the tree.</param>
    public virtual void SetReadOnly(bool readOnly = true)
    {
        IsReadOnly = readOnly;
    }

    /// <summary>
    /// Locks this node and all of its descendants as read-only.
    /// </summary>
    public void Freeze() => SetReadOnly(true);

    /// <summary>
    /// Throws <see cref="ConfigReadOnlyException"/> if <see cref="IsReadOnly"/> is <c>true</c>.
    /// </summary>
    protected void EnsureWritable()
    {
        if (IsReadOnly)
        {
            throw new ConfigReadOnlyException("Cannot modify a read-only configuration node.", Source);
        }
    }

    /// <summary>
    /// Creates a deep copy of this AST node and all of its children so merges never mutate original trees.
    /// Note: Cloned nodes are created in a writable state so they can participate in new compositions.
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

    private object? _value;

    /// <summary>
    /// Gets or sets the underlying scalar CLR value (<see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, or <c>null</c>).
    /// </summary>
    public object? Value
    {
        get => _value;
        set
        {
            EnsureWritable();
            _value = value;
        }
    }

    /// <summary>
    /// Preserves the raw unresolved interpolation template string (e.g. <c>"${db.host}:${db.port}"</c>)
    /// after <see cref="Value"/> is resolved, enabling diagnostics and future template write-back.
    /// </summary>
    public string? RawTemplate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a literal <c>"???"</c> string was explicitly escaped (<c>\???</c>)
    /// so that it is treated as a regular string rather than a mandatory missing value marker.
    /// </summary>
    public bool IsEscapedLiteral { get; set; }

    /// <summary>
    /// Gets a value indicating whether this node is currently set to the mandatory missing marker (<c>"???"</c>) and was not escaped (<c>\???</c>).
    /// </summary>
    public bool IsMissingMarker => !IsEscapedLiteral && _value is string s && s == MissingMarker;

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
        if (value is string rawStr)
        {
            // Handle Hydra escaped missing literal: \??? -> literal "???" (IsMissingMarker = false)
            if (rawStr == @"\???")
            {
                _value = MissingMarker;
                IsEscapedLiteral = true;
            }
            else if (rawStr == @"\\???")
            {
                _value = @"\???";
                IsEscapedLiteral = true;
            }
            else
            {
                _value = value;
                if (rawStr.Contains("${"))
                {
                    RawTemplate = rawStr;
                }
            }
        }
        else
        {
            _value = value;
        }
        Operator = op;
        Source = source ?? SourceInfo.Unknown;
    }

    /// <inheritdoc />
    public override ConfigNode Clone() => new ConfigValueNode(_value, Operator, Source)
    {
        RawTemplate = RawTemplate,
        IsEscapedLiteral = IsEscapedLiteral
    };

    /// <inheritdoc />
    public override string ToString() => IsMissingMarker ? MissingMarker : _value?.ToString() ?? "null";
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

    /// <summary>
    /// Appends an item to the list after verifying the node is not read-only.
    /// </summary>
    public void AddItem(ConfigNode item)
    {
        EnsureWritable();
        Items.Add(item);
    }

    /// <inheritdoc />
    public override void SetReadOnly(bool readOnly = true)
    {
        base.SetReadOnly(readOnly);
        foreach (var item in Items)
        {
            item.SetReadOnly(readOnly);
        }
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

    /// <summary>
    /// Gets a value indicating whether this object is in Struct Mode (<c>OmegaConf.set_struct</c>).
    /// When <c>true</c>, setting or deleting keys that do not already exist throws <see cref="ConfigStructException"/>
    /// unless explicitly forced via <c>+key</c> or <c>++key</c>.
    /// </summary>
    public bool IsStruct { get; private set; }

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

    /// <inheritdoc />
    public override void SetReadOnly(bool readOnly = true)
    {
        base.SetReadOnly(readOnly);
        foreach (var child in Children.Values)
        {
            child.SetReadOnly(readOnly);
        }
    }

    /// <summary>
    /// Recursively enables or disables Struct Mode on this object node and all descendant <see cref="ConfigObjectNode"/> instances.
    /// Equivalent to <c>OmegaConf.set_struct(cfg, isStruct)</c>.
    /// </summary>
    /// <param name="isStruct">Whether to lock (<c>true</c>) or unlock (<c>false</c>) the key structure.</param>
    public void SetStruct(bool isStruct = true)
    {
        IsStruct = isStruct;
        foreach (var child in Children.Values)
        {
            if (child is ConfigObjectNode childObj)
            {
                childObj.SetStruct(isStruct);
            }
        }
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
    /// Sets or replaces a child node for the specified key, enforcing <see cref="ConfigNode.IsReadOnly"/>
    /// and <see cref="IsStruct"/> constraints.
    /// </summary>
    /// <param name="key">The child property key.</param>
    /// <param name="node">The AST node to set.</param>
    /// <param name="allowNewKeyInStruct">If <c>true</c>, permits adding a new key even when <see cref="IsStruct"/> is enabled.</param>
    public void SetChild(string key, ConfigNode node, bool allowNewKeyInStruct = false)
    {
        EnsureWritable();
        if (IsStruct && !allowNewKeyInStruct && !Children.ContainsKey(key))
        {
            throw new ConfigStructException(
                key,
                $"Cannot add new key '{key}' to a struct-locked configuration node. Use '+{key}=...' or '++{key}=...' to explicitly add keys.",
                node.Source != SourceInfo.Unknown ? node.Source : Source);
        }
        Children[key] = node;
    }

    /// <summary>
    /// Removes the child node with the specified key, enforcing <see cref="ConfigNode.IsReadOnly"/>.
    /// </summary>
    public bool RemoveChild(string key)
    {
        EnsureWritable();
        return Children.Remove(key);
    }

    /// <summary>
    /// Navigates a dot-separated path (including bracket indexing <c>servers[0].host</c> and dot indexing <c>servers.0.host</c>)
    /// and returns the matching <see cref="ConfigNode"/>, or <c>null</c> if not found.
    /// </summary>
    public ConfigNode? SelectNode(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return this;

        string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        ConfigNode current = this;

        foreach (var part in parts)
        {
            int bracketIdx = part.IndexOf('[');
            if (bracketIdx >= 0 && part.EndsWith(']'))
            {
                string propName = part[..bracketIdx];
                if (!string.IsNullOrEmpty(propName))
                {
                    if (current is ConfigObjectNode obj && obj.TryGetChild(propName, out var listCandidate))
                    {
                        current = listCandidate!;
                    }
                    else
                    {
                        return null;
                    }
                }

                string indicesPart = part[bracketIdx..];
                while (indicesPart.StartsWith('['))
                {
                    int closeIdx = indicesPart.IndexOf(']');
                    if (closeIdx < 0) return null;

                    string idxStr = indicesPart[1..closeIdx].Trim();
                    if (int.TryParse(idxStr, out int index) && current is ConfigListNode listNode)
                    {
                        int actualIdx = index < 0 ? listNode.Items.Count + index : index;
                        if (actualIdx >= 0 && actualIdx < listNode.Items.Count)
                        {
                            current = listNode.Items[actualIdx];
                        }
                        else
                        {
                            return null;
                        }
                    }
                    else if (current is ConfigObjectNode bracketObj && bracketObj.TryGetChild(idxStr, out var dictChild))
                    {
                        current = dictChild!;
                    }
                    else
                    {
                        return null;
                    }

                    indicesPart = indicesPart[(closeIdx + 1)..];
                }
            }
            else if (current is ConfigListNode directList && int.TryParse(part, out int directIdx))
            {
                int actualIdx = directIdx < 0 ? directList.Items.Count + directIdx : directIdx;
                if (actualIdx >= 0 && actualIdx < directList.Items.Count)
                {
                    current = directList.Items[actualIdx];
                }
                else
                {
                    return null;
                }
            }
            else if (current is ConfigObjectNode obj && obj.TryGetChild(part, out var next))
            {
                current = next!;
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// Safely selects a raw value or node at <paramref name="path"/>, returning <paramref name="defaultValue"/> if the path does not exist or is <c>???</c>.
    /// Equivalent to Python's <c>OmegaConf.select(cfg, path, default=...)</c>.
    /// </summary>
    public object? Select(string path, object? defaultValue = null)
    {
        var node = SelectNode(path);
        if (node == null) return defaultValue;
        if (node is ConfigValueNode vn)
        {
            if (vn.IsMissingMarker || vn.Value == null) return defaultValue;
            return vn.Value;
        }
        return node;
    }

    /// <summary>
    /// Safely selects and binds the configuration node at <paramref name="path"/> to <typeparamref name="T"/>,
    /// returning <paramref name="defaultValue"/> if the path does not exist or is marked <c>???</c>.
    /// </summary>
    public T? Select<T>(string path, T? defaultValue = default)
    {
        var node = SelectNode(path);
        if (node == null) return defaultValue;
        if (node is ConfigValueNode vn && (vn.IsMissingMarker || vn.Value == null))
        {
            return defaultValue;
        }
        return ConfigBinder.Bind<T>(node);
    }

    /// <inheritdoc />
    public override ConfigNode Clone()
    {
        var cloned = new ConfigObjectNode(Operator, Source)
        {
            IsStruct = IsStruct
        };
        foreach (var (k, v) in Children)
        {
            cloned.Children[k] = v.Clone();
        }
        return cloned;
    }

    /// <inheritdoc />
    public override string ToString() => $"{{{string.Join(", ", Children.Select(kv => $"'{kv.Key}': {FormatPythonValue(kv.Value)}"))}}}";

    private static string FormatPythonValue(ConfigNode node) => node switch
    {
        ConfigValueNode { IsMissingMarker: true } => "'???'",
        ConfigValueNode { Value: string s } => $"'{s}'",
        ConfigValueNode { Value: bool b } => b ? "True" : "False",
        ConfigValueNode { Value: null } => "None",
        ConfigValueNode vn => Convert.ToString(vn.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "None",
        ConfigListNode ln => $"[{string.Join(", ", ln.Items.Select(FormatPythonValue))}]",
        ConfigObjectNode on => on.ToString(),
        _ => "None"
    };
}
