using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Cli;
using OmegaConf.NET.Common;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Interpolation;
using OmegaConf.NET.Parsers;
using OmegaConf.NET.Serialization;

namespace OmegaConf.NET;

/// <summary>
/// Fluent builder for composing, overriding, interpolating, and binding hierarchical configurations.
/// Executes a four-stage resolution pipeline:
/// 1. CLI Group Selection Extraction (<c>db=staging/postgres</c>, <c>db@primary=postgres</c>, <c>~db</c>)
/// 2. Tree Composition, Package Directives, Auto-Cascading &amp; Explicit Inheritance (<see cref="TreeResolver"/>)
/// 3. Deep CLI Property &amp; Operator Overrides, Struct Mode &amp; Key Deletions (<see cref="CliOverrideParser"/>)
/// 4. Variable &amp; Expression Interpolation (<see cref="InterpolationEngine"/>)
/// </summary>
public class OmegaConfBuilder
{
    private string _rootDir = Directory.GetCurrentDirectory();
    private string _entryFile = "config.yaml";
    private readonly List<string> _searchPaths = [];
    private readonly Dictionary<string, string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _droppedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _cliArgs = [];
    private readonly List<IResolver> _customResolvers = [];
    private readonly ConfigFormatRegistry _registry = ConfigFormatRegistry.Default;
    private bool _structMode;
    private bool _readOnly;
    private string? _outputDirectory;

    /// <summary>
    /// Creates a new untyped <see cref="OmegaConfBuilder"/> instance.
    /// </summary>
    public static OmegaConfBuilder Create() => new();

    /// <summary>
    /// Creates a new strongly typed <see cref="OmegaConfBuilder{T}"/> instance for target type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The POCO or positional record type to bind the configuration into.</typeparam>
    public static OmegaConfBuilder<T> Create<T>() => new();

    /// <inheritdoc cref="OmegaConfig.Merge(ConfigObjectNode[])"/>
    public static ConfigObjectNode Merge(params ConfigObjectNode[] configs) => OmegaConfig.Merge(configs);

    /// <inheritdoc cref="OmegaConfig.Select(ConfigObjectNode, string, object?)"/>
    public static object? Select(ConfigObjectNode root, string path, object? defaultValue = null) => OmegaConfig.Select(root, path, defaultValue);

    /// <summary>
    /// Safely selects and binds the configuration node at <paramref name="path"/> to <typeparamref name="T"/>,
    /// returning <paramref name="defaultValue"/> if the path does not exist or is marked <c>???</c>.
    /// </summary>
    public static T? Select<T>(ConfigObjectNode root, string path, T? defaultValue = default) => OmegaConfig.Select(root, path, defaultValue);

    /// <inheritdoc cref="OmegaConfig.SetReadOnly(ConfigNode, bool)"/>
    public static void SetReadOnly(ConfigNode node, bool readOnly = true) => OmegaConfig.SetReadOnly(node, readOnly);

    /// <inheritdoc cref="OmegaConfig.SetStruct(ConfigObjectNode, bool)"/>
    public static void SetStruct(ConfigObjectNode node, bool isStruct = true) => OmegaConfig.SetStruct(node, isStruct);

    /// <inheritdoc cref="OmegaConfig.Instantiate{T}(ConfigNode)"/>
    public static T Instantiate<T>(ConfigNode node) => OmegaConfig.Instantiate<T>(node);

    /// <summary>
    /// Sets the root directory containing the entrypoint configuration file and config group subfolders.
    /// </summary>
    /// <param name="rootDir">Absolute or relative directory path.</param>
    public OmegaConfBuilder SetRootDirectory(string rootDir)
    {
        _rootDir = rootDir;
        return this;
    }

    /// <summary>
    /// Shorthand alias for <see cref="SetRootDirectory(string)"/>.
    /// </summary>
    /// <param name="rootDir">Absolute or relative directory path.</param>
    public OmegaConfBuilder SetRoot(string rootDir) => SetRootDirectory(rootDir);

    /// <summary>
    /// Adds one or more secondary configuration search directories (similar to Hydra's <c>hydra.searchpath</c>).
    /// When a config group or file is not found in the primary root directory, search paths are probed in registration order.
    /// </summary>
    /// <param name="paths">Additional configuration directory paths.</param>
    public OmegaConfBuilder AddSearchPath(params string[] paths)
    {
        foreach (var p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                _searchPaths.Add(p);
            }
        }
        return this;
    }

    /// <summary>
    /// Configures secondary configuration search directories (similar to Hydra's <c>hydra.searchpath</c>).
    /// </summary>
    /// <param name="paths">Collection of additional configuration directory paths.</param>
    public OmegaConfBuilder WithSearchPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!string.IsNullOrWhiteSpace(p))
            {
                _searchPaths.Add(p);
            }
        }
        return this;
    }

    /// <summary>
    /// Sets the entrypoint configuration file name (defaults to <c>"config.yaml"</c>).
    /// Supports <c>.yaml</c>, <c>.yml</c>, <c>.json</c>, <c>.jsonc</c>, and <c>.toml</c>.
    /// </summary>
    /// <param name="entryFile">File name relative to the root directory.</param>
    public OmegaConfBuilder SetEntryFile(string entryFile)
    {
        _entryFile = entryFile;
        return this;
    }

    /// <summary>
    /// Explicitly selects a sub-configuration path for a given config group (e.g. group <c>"db"</c> -> path <c>"production/postgres"</c>).
    /// Overrides the default selection specified in the entrypoint's <c>defaults:</c> list.
    /// </summary>
    /// <param name="group">The config group name (or <c>group@package</c> key).</param>
    /// <param name="path">The relative path within the config group directory.</param>
    public OmegaConfBuilder SelectPath(string group, string path)
    {
        _selectedPaths[group] = path;
        return this;
    }

    /// <summary>
    /// Excludes a config group from being loaded during composition (equivalent to passing <c>~group</c> on the CLI).
    /// </summary>
    /// <param name="group">The config group name to exclude.</param>
    public OmegaConfBuilder DropGroup(string group)
    {
        _droppedGroups.Add(group);
        return this;
    }

    /// <summary>
    /// Adds command-line override arguments (e.g. <c>db=production/postgres</c>, <c>~cache</c>, <c>workers+=4</c>).
    /// </summary>
    /// <param name="args">Collection of CLI argument strings.</param>
    public OmegaConfBuilder AddCliArgs(IEnumerable<string> args)
    {
        _cliArgs.AddRange(args);
        return this;
    }

    /// <summary>
    /// Adds command-line override arguments (e.g. <c>db=production/postgres</c>, <c>~cache</c>, <c>workers+=4</c>).
    /// </summary>
    /// <param name="args">Array of CLI argument strings.</param>
    public OmegaConfBuilder AddCliArgs(params string[] args) => AddCliArgs((IEnumerable<string>)args);

    /// <summary>
    /// Registers a custom interpolation resolver function invoked via <c>${name:argument}</c>.
    /// </summary>
    /// <param name="name">Resolver prefix name (case-insensitive).</param>
    /// <param name="handler">Function that transforms the resolved argument string.</param>
    public OmegaConfBuilder RegisterResolver(string name, Func<string, string> handler)
    {
        _customResolvers.Add(new CustomResolver(name, handler));
        return this;
    }

    /// <summary>
    /// Registers a custom <see cref="IResolver"/> implementation for variable interpolation.
    /// </summary>
    /// <param name="resolver">The custom resolver instance.</param>
    public OmegaConfBuilder RegisterResolver(IResolver resolver)
    {
        _customResolvers.Add(resolver);
        return this;
    }

    /// <summary>
    /// Enables or disables strict Struct Mode (<c>OmegaConf.set_struct</c>).
    /// When enabled, CLI overrides that attempt to modify or delete a key that does not exist in the composed schema
    /// will throw <see cref="ConfigStructException"/> unless prefixed with <c>+key=val</c> (append) or <c>++key=val</c> (upsert).
    /// </summary>
    /// <param name="enabled">Whether Struct Mode is enabled (defaults to <c>true</c>).</param>
    public OmegaConfBuilder WithStructMode(bool enabled = true)
    {
        _structMode = enabled;
        return this;
    }

    /// <summary>
    /// Locks the resulting configuration AST as read-only (<see cref="ConfigNode.IsReadOnly"/>) after resolution completes.
    /// </summary>
    /// <param name="enabled">Whether to freeze the resolved node as read-only (defaults to <c>true</c>).</param>
    public OmegaConfBuilder WithReadOnly(bool enabled = true)
    {
        _readOnly = enabled;
        return this;
    }

    /// <summary>
    /// Configures an output directory where Hydra-style reproducibility artifacts (<c>.omegaconf/config.yaml</c>
    /// and <c>.omegaconf/overrides.yaml</c>) are automatically written upon building.
    /// Supports interpolation expressions in the path (e.g. <c>"outputs/${app_name}/${now:yyyy-MM-dd}"</c>).
    /// </summary>
    /// <param name="outputDir">Target output directory path.</param>
    public OmegaConfBuilder WithOutputDirectory(string outputDir)
    {
        _outputDirectory = outputDir;
        return this;
    }

    /// <summary>
    /// Executes the full composition, CLI override, and interpolation pipeline and returns the resolved root AST node.
    /// </summary>
    /// <returns>The composed and interpolated <see cref="ConfigObjectNode"/>.</returns>
    public ConfigObjectNode BuildNode()
    {
        // Stage 1: Separate CLI config-group selections (e.g. db=postgres, db@primary=postgres, ~db) from deep value overrides
        var remainingCliOverrides = new List<string>();
        CliOverrideParser.ExtractGroupSelections(_cliArgs, _selectedPaths, _droppedGroups, remainingCliOverrides, _rootDir, _searchPaths);

        // Stage 2: Resolve directory tree composition, defaults list, @package directives, auto-cascading, and _inherits_
        var resolver = new TreeResolver(_registry);
        var composedRoot = resolver.Resolve(_rootDir, _entryFile, _selectedPaths, _droppedGroups, _searchPaths);

        if (_structMode)
        {
            composedRoot.SetStruct(true);
        }

        // Stage 3: Apply deep property overrides, deletions (~key.path), struct modifiers (+key, ++key), and mutation operators
        CliOverrideParser.ApplyValueOverrides(composedRoot, remainingCliOverrides, _structMode, _droppedGroups);

        // Stage 4: Resolve variable interpolations (${db.host}, ${.sibling}, ${eval:...}, ${env:...}, ${oc.select:...}, ${oc.decode:...})
        var interpolationEngine = new InterpolationEngine(composedRoot, _customResolvers);
        interpolationEngine.ResolveAll();

        // Optional Stage 5: Write reproducible .omegaconf/ snapshot if an output directory was configured
        if (!string.IsNullOrWhiteSpace(_outputDirectory))
        {
            string resolvedOutDir = _outputDirectory.Contains("${")
                ? interpolationEngine.ResolveStringValue(_outputDirectory, "_output_dir_", SourceInfo.Unknown)?.ToString() ?? _outputDirectory
                : _outputDirectory;
            SaveRunSnapshot(composedRoot, resolvedOutDir, _cliArgs);
        }

        if (_readOnly)
        {
            composedRoot.SetReadOnly(true);
        }

        return composedRoot;
    }

    /// <summary>
    /// Writes a Hydra-style reproducibility snapshot inside <c>&lt;outputDir&gt;/.omegaconf/</c>, containing:
    /// - <c>config.yaml</c>: the full resolved configuration tree
    /// - <c>overrides.yaml</c>: the command-line overrides applied to this run
    /// </summary>
    /// <param name="resolvedNode">The resolved root configuration node.</param>
    /// <param name="outputDir">The run output directory.</param>
    /// <param name="cliArgs">Optional command-line overrides applied to the run.</param>
    public static void SaveRunSnapshot(ConfigObjectNode resolvedNode, string outputDir, IEnumerable<string>? cliArgs = null)
    {
        string metaDir = Path.Combine(outputDir, ".omegaconf");
        Directory.CreateDirectory(metaDir);

        File.WriteAllText(Path.Combine(metaDir, "config.yaml"), resolvedNode.ToYaml());

        var overridesNode = new ConfigObjectNode();
        var overridesList = new ConfigListNode(
            (cliArgs ?? []).Where(a => a != "-m" && a != "--multirun").Select(a => (ConfigNode)new ConfigValueNode(a)));
        overridesNode.SetChild("overrides", overridesList, allowNewKeyInStruct: true);
        File.WriteAllText(Path.Combine(metaDir, "overrides.yaml"), overridesNode.ToYaml());
    }

    /// <summary>
    /// Executes a multi-run parameter sweep (Hydra <c>-m</c> / <c>--multirun</c> style),
    /// generating a resolved <see cref="ConfigObjectNode"/> for every combination in the Cartesian product
    /// (including <c>range()</c>, <c>linspace()</c>, <c>interval()</c>, <c>choice()</c>, and <c>glob()</c> sweep generators).
    /// </summary>
    /// <returns>Read-only list of resolved AST nodes, one per parameter combination.</returns>
    public IReadOnlyList<ConfigObjectNode> BuildSweepNodes()
    {
        // Extract sweep matrix and static non-sweep arguments (passing _rootDir for glob() discovery)
        var (nonSweepArgs, sweepMatrix) = MultiRun.SweepEngine.ParseSweepArgs(_cliArgs, _rootDir);
        var combinations = MultiRun.SweepEngine.GenerateCartesianProduct(sweepMatrix);

        var results = new List<ConfigObjectNode>();
        for (int jobIndex = 0; jobIndex < combinations.Count; jobIndex++)
        {
            var combo = combinations[jobIndex];

            // Combine non-sweep CLI arguments with the current permutation's key=value pairs
            var iterationArgs = new List<string>(nonSweepArgs);
            foreach (var (k, v) in combo)
            {
                iterationArgs.Add($"{k}={v}");
            }

            // Build an isolated configuration tree for this specific parameter combination
            var builder = new OmegaConfBuilder()
                .SetRootDirectory(_rootDir)
                .WithSearchPaths(_searchPaths)
                .SetEntryFile(_entryFile)
                .WithStructMode(_structMode)
                .WithReadOnly(_readOnly)
                .AddCliArgs(iterationArgs);

            if (!string.IsNullOrWhiteSpace(_outputDirectory))
            {
                builder.WithOutputDirectory(Path.Combine(_outputDirectory, jobIndex.ToString()));
            }

            foreach (var (g, p) in _selectedPaths)
            {
                builder.SelectPath(g, p);
            }
            foreach (var d in _droppedGroups)
            {
                builder.DropGroup(d);
            }
            foreach (var r in _customResolvers)
            {
                builder.RegisterResolver(r);
            }

            results.Add(builder.BuildNode());
        }

        // If an output directory is configured for the sweep, also write multirun.yaml summary at the sweep root
        if (!string.IsNullOrWhiteSpace(_outputDirectory))
        {
            Directory.CreateDirectory(_outputDirectory);
            var summaryRoot = new ConfigObjectNode();
            summaryRoot.SetChild("job_count", new ConfigValueNode((long)combinations.Count));
            var jobsList = new ConfigListNode();
            for (int i = 0; i < combinations.Count; i++)
            {
                var jobNode = new ConfigObjectNode();
                jobNode.SetChild("job_id", new ConfigValueNode((long)i));
                foreach (var (k, v) in combinations[i])
                {
                    jobNode.SetChild(k, new ConfigValueNode(v));
                }
                jobsList.AddItem(jobNode);
            }
            summaryRoot.SetChild("jobs", jobsList);
            File.WriteAllText(Path.Combine(_outputDirectory, "multirun.yaml"), summaryRoot.ToYaml());
        }

        return results;
    }

    /// <summary>
    /// Executes a multi-run parameter sweep and binds every resulting configuration node to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target C# POCO or positional record type.</typeparam>
    public IReadOnlyList<T> BuildSweeps<T>()
    {
        var nodes = BuildSweepNodes();
        return nodes.Select(ConfigBinder.Bind<T>).ToList();
    }

    /// <summary>
    /// Builds the configuration tree and binds it to a strongly typed C# instance of <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target C# POCO or positional record type.</typeparam>
    public T Build<T>()
    {
        var rootNode = BuildNode();
        return ConfigBinder.Bind<T>(rootNode);
    }
}

/// <summary>
/// Strongly typed wrapper around <see cref="OmegaConfBuilder"/> that binds the resolved configuration to <typeparamref name="T"/>.
/// </summary>
/// <typeparam name="T">Target C# POCO or positional record type.</typeparam>
public class OmegaConfBuilder<T>
{
    private readonly OmegaConfBuilder _inner = new();

    /// <summary>
    /// Gets the underlying untyped <see cref="OmegaConfBuilder"/> instance.
    /// </summary>
    public OmegaConfBuilder InnerBuilder => _inner;

    /// <inheritdoc cref="OmegaConfBuilder.SetRootDirectory(string)"/>
    public OmegaConfBuilder<T> SetRootDirectory(string rootDir)
    {
        _inner.SetRootDirectory(rootDir);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.SetRoot(string)"/>
    public OmegaConfBuilder<T> SetRoot(string rootDir) => SetRootDirectory(rootDir);

    /// <inheritdoc cref="OmegaConfBuilder.AddSearchPath(string[])"/>
    public OmegaConfBuilder<T> AddSearchPath(params string[] paths)
    {
        _inner.AddSearchPath(paths);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.WithSearchPaths(IEnumerable{string})"/>
    public OmegaConfBuilder<T> WithSearchPaths(IEnumerable<string> paths)
    {
        _inner.WithSearchPaths(paths);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.SetEntryFile(string)"/>
    public OmegaConfBuilder<T> SetEntryFile(string entryFile)
    {
        _inner.SetEntryFile(entryFile);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.SelectPath(string, string)"/>
    public OmegaConfBuilder<T> SelectPath(string group, string path)
    {
        _inner.SelectPath(group, path);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.DropGroup(string)"/>
    public OmegaConfBuilder<T> DropGroup(string group)
    {
        _inner.DropGroup(group);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.AddCliArgs(IEnumerable{string})"/>
    public OmegaConfBuilder<T> AddCliArgs(IEnumerable<string> args)
    {
        _inner.AddCliArgs(args);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.AddCliArgs(string[])"/>
    public OmegaConfBuilder<T> AddCliArgs(params string[] args) => AddCliArgs((IEnumerable<string>)args);

    /// <inheritdoc cref="OmegaConfBuilder.RegisterResolver(string, Func{string, string})"/>
    public OmegaConfBuilder<T> RegisterResolver(string name, Func<string, string> handler)
    {
        _inner.RegisterResolver(name, handler);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.RegisterResolver(IResolver)"/>
    public OmegaConfBuilder<T> RegisterResolver(IResolver resolver)
    {
        _inner.RegisterResolver(resolver);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.WithStructMode(bool)"/>
    public OmegaConfBuilder<T> WithStructMode(bool enabled = true)
    {
        _inner.WithStructMode(enabled);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.WithReadOnly(bool)"/>
    public OmegaConfBuilder<T> WithReadOnly(bool enabled = true)
    {
        _inner.WithReadOnly(enabled);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.WithOutputDirectory(string)"/>
    public OmegaConfBuilder<T> WithOutputDirectory(string outputDir)
    {
        _inner.WithOutputDirectory(outputDir);
        return this;
    }

    /// <inheritdoc cref="OmegaConfBuilder.BuildNode()"/>
    public ConfigObjectNode BuildNode() => _inner.BuildNode();

    /// <inheritdoc cref="OmegaConfBuilder.BuildSweepNodes()"/>
    public IReadOnlyList<ConfigObjectNode> BuildSweepNodes() => _inner.BuildSweepNodes();

    /// <inheritdoc cref="OmegaConfBuilder.BuildSweeps{T}()"/>
    public IReadOnlyList<T> BuildSweeps() => _inner.BuildSweeps<T>();

    /// <inheritdoc cref="OmegaConfBuilder.Build{T}()"/>
    public T Build() => _inner.Build<T>();
}
