using System.Collections;
using System.Reflection;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Attributes;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Composition;

/// <summary>
/// Categorizes an entry path in a <see cref="ConfigStore"/>.
/// </summary>
public enum ConfigStoreEntryType
{
    /// <summary>Path does not exist in the store.</summary>
    NotFound,

    /// <summary>Path refers to a configuration group folder containing subgroups or configs.</summary>
    Group,

    /// <summary>Path refers to a stored configuration node.</summary>
    Config
}

/// <summary>
/// Represents a configuration node and metadata stored inside a <see cref="ConfigStore"/>.
/// Modeled on Python Hydra's <c>hydra.core.config_store.ConfigNode</c>.
/// </summary>
public sealed class StoredConfigNode
{
    /// <summary>Gets the normalized configuration name (ending with <c>.yaml</c>).</summary>
    public string Name { get; }

    /// <summary>Gets the normalized configuration group path (e.g. <c>"db"</c> or <c>"app/db"</c>), or <c>null</c> for root configs.</summary>
    public string? Group { get; }

    /// <summary>Gets the stored <see cref="ConfigObjectNode"/> AST.</summary>
    public ConfigObjectNode Node { get; }

    /// <summary>Gets the optional target package override (e.g. <c>"primary_db"</c> or <c>"_global_"</c>).</summary>
    public string? Package { get; }

    /// <summary>Gets the optional provider identifier indicating what module registered this entry.</summary>
    public string? Provider { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="StoredConfigNode"/> class.
    /// </summary>
    public StoredConfigNode(string name, string? group, ConfigObjectNode node, string? package = null, string? provider = null)
    {
        Name = name;
        Group = group;
        Node = node;
        Package = package;
        Provider = provider;
    }

    /// <summary>
    /// Creates an isolated deep clone of this <see cref="StoredConfigNode"/> so mutations during composition never alter the store.
    /// </summary>
    public StoredConfigNode Clone() =>
        new(Name, Group, (ConfigObjectNode)Node.Clone(), Package, Provider);
}

/// <summary>
/// In-memory repository for code-first structured configurations, C# POCOs/records, and config groups.
/// Modeled directly on Python Hydra's <c>hydra.core.config_store.ConfigStore</c>.
/// Allows composing configurations purely from memory or mixing in-memory schemas with YAML/JSON/TOML files on disk.
/// </summary>
public sealed class ConfigStore
{
    /// <summary>
    /// Gets the global singleton <see cref="ConfigStore"/> instance (equivalent to <c>ConfigStore.instance()</c> in Hydra).
    /// Isolated instances can also be created via <c>new ConfigStore()</c> and passed to <see cref="OmegaConfBuilder.WithConfigStore(ConfigStore)"/>.
    /// </summary>
    public static ConfigStore Instance { get; } = new();

    private sealed class GroupFolder
    {
        public Dictionary<string, GroupFolder> Subgroups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, StoredConfigNode> Configs { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly GroupFolder _root = new();
    private readonly List<string> _warnings = [];

    /// <summary>
    /// Gets the list of collision warning messages recorded when <see cref="Store"/> overwrites an existing entry with <c>replace: null</c>.
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Clears all stored configurations, groups, and warnings from this <see cref="ConfigStore"/>.
    /// </summary>
    public void Clear()
    {
        _root.Subgroups.Clear();
        _root.Configs.Clear();
        _warnings.Clear();
    }

    /// <summary>
    /// Returns a scoped <see cref="ConfigStoreWithProvider"/> wrapper that automatically stamps all <c>Store(...)</c> calls with <paramref name="provider"/>.
    /// Equivalent to Python's <c>with ConfigStore.instance().with_provider("my_provider") as cs:</c>.
    /// </summary>
    /// <param name="provider">The provider name to associate with stored entries.</param>
    public ConfigStoreWithProvider WithProvider(string provider) => new(this, provider);

    /// <summary>
    /// Stores a configuration node (<see cref="ConfigObjectNode"/>, <see cref="IDictionary"/>, C# POCO/record instance, or <see cref="Type"/>)
    /// in the repository.
    /// </summary>
    /// <param name="name">The configuration name (with or without <c>.yaml</c> extension).</param>
    /// <param name="node">The configuration node, dictionary, POCO/record instance, or C# schema <see cref="Type"/>.</param>
    /// <param name="group">Optional group path (e.g. <c>"db"</c> or <c>"cloud/aws"</c>); <c>null</c> or <c>""</c> stores at the root level.</param>
    /// <param name="package">Optional target package path (e.g. <c>"primary_db"</c>, <c>"_global_"</c>, or <c>"_group_.replica"</c>).</param>
    /// <param name="provider">Optional provider identifier for collision diagnostics.</param>
    /// <param name="replace">
    /// Controls collision handling when an entry with the same group and name already exists:
    /// <list type="bullet">
    /// <item><description><c>true</c>: Silently replaces the existing entry and updates its provider.</description></item>
    /// <item><description><c>false</c>: Throws <see cref="ArgumentException"/> without modifying the existing entry.</description></item>
    /// <item><description><c>null</c> (default): Replaces the existing entry and appends a collision warning to <see cref="Warnings"/>.</description></item>
    /// </list>
    /// </param>
    public void Store(
        string name,
        object node,
        string? group = null,
        string? package = null,
        string? provider = null,
        bool? replace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(node);

        string? normalizedGroup = NormalizeGroup(group);
        string normalizedName = NormalizeConfigName(name);
        string fullKey = normalizedGroup != null ? $"{normalizedGroup}/{normalizedName}" : normalizedName;

        GroupFolder targetFolder = GetOrCreateGroupFolder(normalizedGroup);

        if (targetFolder.Configs.TryGetValue(normalizedName, out var existing))
        {
            if (replace == false)
            {
                throw new ArgumentException(
                    $"Config '{fullKey}' is already registered in ConfigStore " +
                    $"(existing provider: {existing.Provider ?? "null"}, new provider: {provider ?? "null"}).",
                    nameof(name));
            }

            if (replace == null)
            {
                _warnings.Add(
                    $"Overwriting '{fullKey}' in ConfigStore " +
                    $"(existing provider: {existing.Provider ?? "null"}, new provider: {provider ?? "null"}).");
            }
        }

        string? expandedPkg = DefaultsEntry.GroupChoice(normalizedGroup ?? "", normalizedName, package: package).Package;
        ConfigObjectNode astNode = ConvertToObjectNode(node, new SourceInfo($"store://{fullKey}", 0, 0));
        targetFolder.Configs[normalizedName] = new StoredConfigNode(normalizedName, normalizedGroup, astNode, expandedPkg, provider);
    }

    /// <summary>
    /// Stores a default instance of the strongly typed C# schema <typeparamref name="TSchema"/> in the repository.
    /// </summary>
    public void Store<TSchema>(
        string name,
        string? group = null,
        string? package = null,
        string? provider = null,
        bool? replace = null)
        where TSchema : class
    {
        Store(name, typeof(TSchema), group, package, provider, replace);
    }

    /// <summary>
    /// Loads a cloned <see cref="StoredConfigNode"/> by path (e.g. <c>"config"</c>, <c>"config.yaml"</c>, or <c>"db/postgres"</c>).
    /// Throws <see cref="KeyNotFoundException"/> if the entry does not exist.
    /// </summary>
    public StoredConfigNode Load(string configPath)
    {
        if (TryLoad(configPath, out var stored) && stored != null)
        {
            return stored;
        }

        throw new KeyNotFoundException($"Structured config '{configPath}' not found in ConfigStore.");
    }

    /// <summary>
    /// Attempts to load a cloned <see cref="StoredConfigNode"/> by path (or by <paramref name="group"/> and <paramref name="name"/>).
    /// </summary>
    public bool TryLoad(string? group, string name, out StoredConfigNode? stored)
    {
        string combined = string.IsNullOrWhiteSpace(group)
            ? name
            : $"{group.Trim().Trim('/')}/{name.Trim().TrimStart('/')}";
        return TryLoad(combined, out stored);
    }

    /// <summary>
    /// Attempts to load a cloned <see cref="StoredConfigNode"/> by path (e.g. <c>"config"</c>, <c>"db/postgres"</c>, or <c>"db/postgres.yaml"</c>).
    /// </summary>
    public bool TryLoad(string configPath, out StoredConfigNode? stored)
    {
        stored = null;
        if (string.IsNullOrWhiteSpace(configPath)) return false;

        string normalized = configPath.Trim().Trim('/').Replace('\\', '/');
        int lastSlash = normalized.LastIndexOf('/');
        string? group = lastSlash > 0 ? normalized[..lastSlash] : null;
        string rawName = lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
        string normalizedName = NormalizeConfigName(rawName);

        var folder = FindGroupFolder(group);
        if (folder != null && folder.Configs.TryGetValue(normalizedName, out var found))
        {
            stored = found.Clone();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a configuration group folder exists in this store.
    /// </summary>
    public bool HasGroup(string groupPath) => GetEntryType(groupPath) == ConfigStoreEntryType.Group;

    /// <summary>
    /// Inspects whether <paramref name="path"/> refers to a <see cref="ConfigStoreEntryType.Group"/>,
    /// <see cref="ConfigStoreEntryType.Config"/>, or <see cref="ConfigStoreEntryType.NotFound"/>.
    /// Equivalent to Python's <c>ConfigStore.get_type(path)</c>.
    /// </summary>
    public ConfigStoreEntryType GetEntryType(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ConfigStoreEntryType.Group;
        }

        string normalized = path.Trim().Trim('/').Replace('\\', '/');
        if (FindGroupFolder(normalized) != null)
        {
            return ConfigStoreEntryType.Group;
        }

        if (TryLoad(normalized, out _))
        {
            return ConfigStoreEntryType.Config;
        }

        return ConfigStoreEntryType.NotFound;
    }

    /// <summary>
    /// Lists all subgroup names and stored configuration filenames (<c>*.yaml</c>) directly under <paramref name="groupPath"/> in sorted order.
    /// Equivalent to Python's <c>ConfigStore.list(path)</c>.
    /// </summary>
    public IReadOnlyList<string> List(string? groupPath = null)
    {
        string? normalized = NormalizeGroup(groupPath);
        var folder = FindGroupFolder(normalized);
        if (folder == null)
        {
            if (normalized != null && TryLoad(normalized, out _))
            {
                throw new IOException($"Path '{groupPath}' points to a config node, not a group directory.");
            }
            throw new IOException($"Group path '{groupPath}' not found in ConfigStore.");
        }

        var entries = new List<string>();
        entries.AddRange(folder.Subgroups.Keys);
        entries.AddRange(folder.Configs.Keys);
        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    private GroupFolder GetOrCreateGroupFolder(string? normalizedGroup)
    {
        if (string.IsNullOrEmpty(normalizedGroup)) return _root;

        string[] segments = normalizedGroup.Split('/', StringSplitOptions.RemoveEmptyEntries);
        GroupFolder current = _root;
        foreach (var seg in segments)
        {
            if (!current.Subgroups.TryGetValue(seg, out var next))
            {
                next = new GroupFolder();
                current.Subgroups[seg] = next;
            }
            current = next;
        }
        return current;
    }

    private GroupFolder? FindGroupFolder(string? normalizedGroup)
    {
        if (string.IsNullOrEmpty(normalizedGroup)) return _root;

        string[] segments = normalizedGroup.Split('/', StringSplitOptions.RemoveEmptyEntries);
        GroupFolder current = _root;
        foreach (var seg in segments)
        {
            if (!current.Subgroups.TryGetValue(seg, out var next))
            {
                return null;
            }
            current = next;
        }
        return current;
    }

    private static string? NormalizeGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return null;
        string trimmed = group.Trim().Replace('\\', '/').Trim('/');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string NormalizeConfigName(string name)
    {
        string trimmed = name.Trim().Replace('\\', '/').TrimStart('/');
        if (trimmed.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^5] + ".yaml";
        }
        if (trimmed.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed[..^4] + ".yaml";
        }
        if (trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            int dotIdx = trimmed.LastIndexOf('.');
            return trimmed[..dotIdx] + ".yaml";
        }
        return trimmed + ".yaml";
    }

    /// <summary>
    /// Converts a <see cref="ConfigObjectNode"/>, <see cref="IDictionary"/>, C# POCO/record instance, or C# <see cref="Type"/>
    /// into a <see cref="ConfigObjectNode"/> AST.
    /// </summary>
    public static ConfigObjectNode ConvertToObjectNode(object node, SourceInfo? source = null)
    {
        var src = source ?? SourceInfo.Unknown;

        if (node is ConfigObjectNode existingObj)
        {
            var cloned = (ConfigObjectNode)existingObj.Clone();
            if (cloned.Source == SourceInfo.Unknown && src != SourceInfo.Unknown)
            {
                cloned.Source = src;
            }
            return cloned;
        }

        if (node is IDictionary dict)
        {
            var dictNode = new ConfigObjectNode(source: src);
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key != null)
                {
                    dictNode.SetChild(entry.Key.ToString()!, ConvertValueToNode(entry.Value, src), allowNewKeyInStruct: true);
                }
            }
            return dictNode;
        }

        if (node is Type schemaType)
        {
            object? defaultInstance = null;
            try
            {
                defaultInstance = Activator.CreateInstance(schemaType);
            }
            catch
            {
                // Positional record without parameterless ctor: inspect constructor parameter defaults
            }

            if (defaultInstance != null)
            {
                var instNode = ConvertPocoToObjectNode(defaultInstance, schemaType, src);
                instNode.SetStruct(true);
                return instNode;
            }

            var ctorNode = ConvertSchemaTypeToObjectNode(schemaType, src);
            ctorNode.SetStruct(true);
            return ctorNode;
        }

        var pocoNode = ConvertPocoToObjectNode(node, node.GetType(), src);
        return pocoNode;
    }

    private static ConfigObjectNode ConvertPocoToObjectNode(object instance, Type type, SourceInfo src)
    {
        var objNode = new ConfigObjectNode(source: src);
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.CanRead && p.GetCustomAttribute<ConfigIgnoreAttribute>() == null);

        foreach (var prop in props)
        {
            string key = prop.GetCustomAttribute<ConfigKeyAttribute>()?.Key
                         ?? ConfigBinder.ToSnakeCase(prop.Name);
            bool isRequired = prop.GetCustomAttribute<ConfigRequiredAttribute>() != null;
            object? val = prop.GetValue(instance);

            if (val == null && isRequired)
            {
                objNode.SetChild(key, new ConfigValueNode(ConfigValueNode.MissingMarker, source: src), allowNewKeyInStruct: true);
            }
            else
            {
                objNode.SetChild(key, ConvertValueToNode(val, src), allowNewKeyInStruct: true);
            }
        }

        return objNode;
    }

    private static ConfigObjectNode ConvertSchemaTypeToObjectNode(Type schemaType, SourceInfo src)
    {
        var objNode = new ConfigObjectNode(source: src);
        var ctor = schemaType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                             .OrderByDescending(c => c.GetParameters().Length)
                             .FirstOrDefault();

        if (ctor != null)
        {
            foreach (var param in ctor.GetParameters())
            {
                string paramName = param.Name ?? "arg";
                string key = param.GetCustomAttribute<ConfigKeyAttribute>()?.Key
                             ?? ConfigBinder.ToSnakeCase(paramName);
                if (param.HasDefaultValue)
                {
                    objNode.SetChild(key, ConvertValueToNode(param.DefaultValue, src), allowNewKeyInStruct: true);
                }
                else
                {
                    objNode.SetChild(key, new ConfigValueNode(ConfigValueNode.MissingMarker, source: src), allowNewKeyInStruct: true);
                }
            }
        }

        return objNode;
    }

    private static ConfigNode ConvertValueToNode(object? value, SourceInfo src)
    {
        if (value is ConfigNode cn) return cn.Clone();
        if (value == null) return new ConfigValueNode(null, source: src);
        if (value is int i) return new ConfigValueNode((long)i, source: src);
        if (value is float f) return new ConfigValueNode((double)f, source: src);
        if (value is Enum e) return new ConfigValueNode(e.ToString(), source: src);
        if (value is string or bool or long or double or decimal or Guid or Uri or TimeSpan or DateTime or DateTimeOffset or DateOnly or TimeOnly)
        {
            return new ConfigValueNode(value, source: src);
        }

        if (value is IDictionary dict)
        {
            return ConvertToObjectNode(dict, src);
        }

        if (value is IEnumerable enumerable)
        {
            var items = new List<ConfigNode>();
            foreach (var item in enumerable)
            {
                items.Add(ConvertValueToNode(item, src));
            }
            return new ConfigListNode(items, source: src);
        }

        return ConvertPocoToObjectNode(value, value.GetType(), src);
    }
}

/// <summary>
/// Scoped helper returned by <see cref="ConfigStore.WithProvider(string)"/> that automatically passes a default <c>provider</c>
/// to all <see cref="Store"/> calls.
/// </summary>
public sealed class ConfigStoreWithProvider : IDisposable
{
    private readonly ConfigStore _store;
    private readonly string _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigStoreWithProvider"/> class.
    /// </summary>
    public ConfigStoreWithProvider(ConfigStore store, string provider)
    {
        _store = store;
        _provider = provider;
    }

    /// <summary>
    /// Stores a configuration node in the underlying <see cref="ConfigStore"/> using the scoped provider name.
    /// </summary>
    public void Store(
        string name,
        object node,
        string? group = null,
        string? package = null,
        string? provider = null,
        bool? replace = null)
    {
        _store.Store(name, node, group, package, provider ?? _provider, replace);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
