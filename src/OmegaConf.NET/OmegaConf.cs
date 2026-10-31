using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Interpolation;
using OmegaConf.NET.Operators;
using OmegaConf.NET.Parsers;

namespace OmegaConf.NET;

/// <summary>
/// Static utility facade inspired by Python's <c>OmegaConf</c> and <c>hydra.utils</c> APIs,
/// providing programmatic AST merging, safe path selection, immutability/struct flag control,
/// dynamic instantiation, and inline format parsing.
/// </summary>
public static class OmegaConfig
{
    private static readonly YamlFormatParser YamlParser = new();
    private static readonly JsonFormatParser JsonParser = new();
    private static readonly TomlFormatParser TomlParser = new();

    /// <summary>
    /// Merges multiple <see cref="ConfigObjectNode"/> trees sequentially from left to right using <see cref="OperatorEngine"/>
    /// and resolves any variable interpolations across the merged tree.
    /// Equivalent to Python's <c>OmegaConf.merge(*configs)</c>.
    /// </summary>
    /// <param name="configs">The configuration trees to merge in precedence order.</param>
    /// <param name="resolveInterpolations">Whether to evaluate <c>${...}</c> interpolations after merging (defaults to <c>true</c>).</param>
    public static ConfigObjectNode Merge(IEnumerable<ConfigObjectNode> configs, bool resolveInterpolations = true)
    {
        ConfigObjectNode merged = new();
        foreach (var cfg in configs)
        {
            if (cfg == null) continue;
            merged = (ConfigObjectNode)OperatorEngine.Apply(merged, cfg.Clone());
        }

        if (resolveInterpolations)
        {
            var engine = new InterpolationEngine(merged);
            engine.ResolveAll();
        }

        return merged;
    }

    /// <summary>
    /// Merges multiple <see cref="ConfigObjectNode"/> trees sequentially from left to right using <see cref="OperatorEngine"/>
    /// and resolves any variable interpolations across the merged tree.
    /// Equivalent to Python's <c>OmegaConf.merge(*configs)</c>.
    /// </summary>
    public static ConfigObjectNode Merge(params ConfigObjectNode[] configs) => Merge((IEnumerable<ConfigObjectNode>)configs);

    /// <summary>
    /// Safely selects a raw value or node at <paramref name="path"/> from <paramref name="root"/>,
    /// returning <paramref name="defaultValue"/> if the path does not exist or is marked <c>???</c>.
    /// Equivalent to Python's <c>OmegaConf.select(cfg, path, default=...)</c>.
    /// </summary>
    public static object? Select(ConfigObjectNode root, string path, object? defaultValue = null) =>
        root.Select(path, defaultValue);

    /// <summary>
    /// Safely selects and binds the configuration node at <paramref name="path"/> to <typeparamref name="T"/>,
    /// returning <paramref name="defaultValue"/> if the path does not exist or is marked <c>???</c>.
    /// </summary>
    public static T? Select<T>(ConfigObjectNode root, string path, T? defaultValue = default) =>
        root.Select(path, defaultValue);

    /// <summary>
    /// Recursively sets the read-only flag on <paramref name="node"/> and all of its descendants.
    /// Equivalent to Python's <c>OmegaConf.set_readonly(cfg, readOnly)</c>.
    /// </summary>
    public static void SetReadOnly(ConfigNode node, bool readOnly = true) =>
        node.SetReadOnly(readOnly);

    /// <summary>
    /// Recursively enables or disables Struct Mode on <paramref name="node"/> and all of its descendant <see cref="ConfigObjectNode"/> instances.
    /// Equivalent to Python's <c>OmegaConf.set_struct(cfg, isStruct)</c>.
    /// </summary>
    public static void SetStruct(ConfigObjectNode node, bool isStruct = true) =>
        node.SetStruct(isStruct);

    /// <summary>
    /// Dynamically instantiates a strongly typed object (or <see cref="Func{TResult}"/> factory when <c>_partial_: true</c>)
    /// from a configuration node containing <c>_target_</c> and optional <c>_args_</c>.
    /// Equivalent to Python's <c>hydra.utils.instantiate(cfg)</c>.
    /// </summary>
    public static T Instantiate<T>(ConfigNode node) =>
        ConfigBinder.Instantiate<T>(node);

    /// <summary>
    /// Dynamically instantiates an object from a configuration node containing <c>_target_</c>,
    /// enforcing an <see cref="ExecutionWhitelist"/> of allowed target type names or <c>Namespace.*</c> wildcard patterns.
    /// </summary>
    public static T Instantiate<T>(ConfigNode node, IEnumerable<string> allowedTargets) =>
        ConfigBinder.Instantiate<T>(node, null, allowedTargets);

    /// <summary>
    /// Dynamically instantiates an object from a configuration node containing <c>_target_</c>,
    /// enforcing an <see cref="ExecutionWhitelist"/>.
    /// </summary>
    public static T Instantiate<T>(ConfigNode node, ExecutionWhitelist whitelist) =>
        ConfigBinder.Instantiate<T>(node, null, whitelist);

    /// <summary>
    /// Dynamically instantiates an object from a configuration node containing <c>_target_</c>,
    /// applying call-site parameter overrides from a dictionary.
    /// Equivalent to Python's <c>hydra.utils.instantiate(cfg, **overrides)</c>.
    /// </summary>
    public static T Instantiate<T>(
        ConfigNode node,
        IDictionary<string, object?>? overrides,
        IEnumerable<string>? allowedTargets = null) =>
        ConfigBinder.Instantiate<T>(node, overrides, allowedTargets);

    /// <summary>
    /// Dynamically instantiates an object from a configuration node containing <c>_target_</c>,
    /// applying call-site parameter overrides from an anonymous object (e.g. <c>new { Port = 9090 }</c>).
    /// </summary>
    public static T Instantiate<T>(
        ConfigNode node,
        object anonymousOverrides,
        IEnumerable<string>? allowedTargets = null) =>
        ConfigBinder.Instantiate<T>(node, anonymousOverrides, allowedTargets);

    /// <summary>
    /// Parses an inline YAML string into a <see cref="ConfigObjectNode"/>.
    /// </summary>
    public static ConfigObjectNode ParseYaml(string yamlContent) =>
        YamlParser.Parse(yamlContent);

    /// <summary>
    /// Parses an inline JSON or JSONC string into a <see cref="ConfigObjectNode"/>.
    /// </summary>
    public static ConfigObjectNode ParseJson(string jsonContent) =>
        JsonParser.Parse(jsonContent);

    /// <summary>
    /// Parses an inline TOML string into a <see cref="ConfigObjectNode"/>.
    /// </summary>
    public static ConfigObjectNode ParseToml(string tomlContent) =>
        TomlParser.Parse(tomlContent);
}
