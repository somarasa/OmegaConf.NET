using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Cli;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Interpolation;
using OmegaConf.NET.Parsers;

namespace OmegaConf.NET;

/// <summary>
/// Fluent builder for composing, overriding, interpolating, and binding hierarchical configurations.
/// Executes a four-stage resolution pipeline:
/// 1. CLI Group Selection Extraction (<c>db=staging/postgres</c>, <c>~db</c>)
/// 2. Tree Composition, Auto-Cascading &amp; Explicit Inheritance (<see cref="TreeResolver"/>)
/// 3. Deep CLI Property &amp; Operator Overrides (<see cref="CliOverrideParser"/>)
/// 4. Variable &amp; Expression Interpolation (<see cref="InterpolationEngine"/>)
/// </summary>
public class OmegaConfBuilder
{
    private string _rootDir = Directory.GetCurrentDirectory();
    private string _entryFile = "config.yaml";
    private readonly Dictionary<string, string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _droppedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _cliArgs = [];
    private readonly List<IResolver> _customResolvers = [];
    private readonly ConfigFormatRegistry _registry = ConfigFormatRegistry.Default;

    /// <summary>
    /// Creates a new untyped <see cref="OmegaConfBuilder"/> instance.
    /// </summary>
    public static OmegaConfBuilder Create() => new();

    /// <summary>
    /// Creates a new strongly typed <see cref="OmegaConfBuilder{T}"/> instance for target type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The POCO or positional record type to bind the configuration into.</typeparam>
    public static OmegaConfBuilder<T> Create<T>() => new();

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
    /// <param name="group">The config group name (subdirectory under root).</param>
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
    /// Executes the full composition, CLI override, and interpolation pipeline and returns the resolved root AST node.
    /// </summary>
    /// <returns>The composed and interpolated <see cref="ConfigObjectNode"/>.</returns>
    public ConfigObjectNode BuildNode()
    {
        // Stage 1: Separate CLI config-group selections (e.g. db=postgres, ~db) from deep value overrides
        var remainingCliOverrides = new List<string>();
        CliOverrideParser.ExtractGroupSelections(_cliArgs, _selectedPaths, _droppedGroups, remainingCliOverrides, _rootDir);

        // Stage 2: Resolve directory tree composition, defaults list, auto-cascading, and _inherits_
        var resolver = new TreeResolver(_registry);
        var composedRoot = resolver.Resolve(_rootDir, _entryFile, _selectedPaths, _droppedGroups);

        // Stage 3: Apply deep property overrides and mutation operators from CLI (e.g. app.timeout+=10, debug!=true)
        CliOverrideParser.ApplyValueOverrides(composedRoot, remainingCliOverrides);

        // Stage 4: Resolve variable interpolations (${db.host}, ${.sibling}, ${eval:...}, ${env:...})
        var interpolationEngine = new InterpolationEngine(composedRoot, _customResolvers);
        interpolationEngine.ResolveAll();

        return composedRoot;
    }

    /// <summary>
    /// Executes a multi-run parameter sweep (Hydra <c>-m</c> / <c>--multirun</c> style),
    /// generating a resolved <see cref="ConfigObjectNode"/> for every combination in the Cartesian product.
    /// </summary>
    /// <returns>Read-only list of resolved AST nodes, one per parameter combination.</returns>
    public IReadOnlyList<ConfigObjectNode> BuildSweepNodes()
    {
        // Extract sweep matrix (keys with comma-separated values) and static non-sweep arguments
        var (nonSweepArgs, sweepMatrix) = MultiRun.SweepEngine.ParseSweepArgs(_cliArgs);
        var combinations = MultiRun.SweepEngine.GenerateCartesianProduct(sweepMatrix);

        var results = new List<ConfigObjectNode>();
        foreach (var combo in combinations)
        {
            // Combine non-sweep CLI arguments with the current permutation's key=value pairs
            var iterationArgs = new List<string>(nonSweepArgs);
            foreach (var (k, v) in combo)
            {
                iterationArgs.Add($"{k}={v}");
            }

            // Build an isolated configuration tree for this specific parameter combination
            var builder = new OmegaConfBuilder()
                .SetRootDirectory(_rootDir)
                .SetEntryFile(_entryFile)
                .AddCliArgs(iterationArgs);

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

    /// <inheritdoc cref="OmegaConfBuilder.BuildNode()"/>
    public ConfigObjectNode BuildNode() => _inner.BuildNode();

    /// <inheritdoc cref="OmegaConfBuilder.BuildSweepNodes()"/>
    public IReadOnlyList<ConfigObjectNode> BuildSweepNodes() => _inner.BuildSweepNodes();

    /// <inheritdoc cref="OmegaConfBuilder.BuildSweeps{T}()"/>
    public IReadOnlyList<T> BuildSweeps() => _inner.BuildSweeps<T>();

    /// <inheritdoc cref="OmegaConfBuilder.Build{T}()"/>
    public T Build() => _inner.Build<T>();
}
