using OmegaConf.NET.Ast;
using OmegaConf.NET.Binding;
using OmegaConf.NET.Cli;
using OmegaConf.NET.Composition;
using OmegaConf.NET.Interpolation;
using OmegaConf.NET.Parsers;

namespace OmegaConf.NET;

public class OmegaConfBuilder
{
    private string _rootDir = Directory.GetCurrentDirectory();
    private string _entryFile = "config.yaml";
    private readonly Dictionary<string, string> _selectedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _droppedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _cliArgs = [];
    private readonly List<IResolver> _customResolvers = [];
    private readonly ConfigFormatRegistry _registry = ConfigFormatRegistry.Default;

    public static OmegaConfBuilder Create() => new();

    public static OmegaConfBuilder<T> Create<T>() => new();

    public OmegaConfBuilder SetRootDirectory(string rootDir)
    {
        _rootDir = rootDir;
        return this;
    }

    public OmegaConfBuilder SetRoot(string rootDir) => SetRootDirectory(rootDir);

    public OmegaConfBuilder SetEntryFile(string entryFile)
    {
        _entryFile = entryFile;
        return this;
    }

    public OmegaConfBuilder SelectPath(string group, string path)
    {
        _selectedPaths[group] = path;
        return this;
    }

    public OmegaConfBuilder DropGroup(string group)
    {
        _droppedGroups.Add(group);
        return this;
    }

    public OmegaConfBuilder AddCliArgs(IEnumerable<string> args)
    {
        _cliArgs.AddRange(args);
        return this;
    }

    public OmegaConfBuilder AddCliArgs(params string[] args) => AddCliArgs((IEnumerable<string>)args);

    public OmegaConfBuilder RegisterResolver(string name, Func<string, string> handler)
    {
        _customResolvers.Add(new CustomResolver(name, handler));
        return this;
    }

    public OmegaConfBuilder RegisterResolver(IResolver resolver)
    {
        _customResolvers.Add(resolver);
        return this;
    }

    public ConfigObjectNode BuildNode()
    {
        // 1. Extract group selections and ~group from CLI args
        var remainingCliOverrides = new List<string>();
        CliOverrideParser.ExtractGroupSelections(_cliArgs, _selectedPaths, _droppedGroups, remainingCliOverrides);

        // 2. Resolve tree composition with defaults and cascading
        var resolver = new TreeResolver(_registry);
        var composedRoot = resolver.Resolve(_rootDir, _entryFile, _selectedPaths, _droppedGroups);

        // 3. Apply CLI value overrides (e.g. app.timeout+=10, logging.level=Debug)
        CliOverrideParser.ApplyValueOverrides(composedRoot, remainingCliOverrides);

        // 4. Resolve variable interpolations (${db.host}, ${env:PORT}, etc.)
        var interpolationEngine = new InterpolationEngine(composedRoot, _customResolvers);
        interpolationEngine.ResolveAll();

        return composedRoot;
    }

    /// <summary>
    /// Executes a multi-run sweep (Hydra -m style), generating a configuration for every parameter combination.
    /// </summary>
    public IReadOnlyList<ConfigObjectNode> BuildSweepNodes()
    {
        var (nonSweepArgs, sweepMatrix) = MultiRun.SweepEngine.ParseSweepArgs(_cliArgs);
        var combinations = MultiRun.SweepEngine.GenerateCartesianProduct(sweepMatrix);

        var results = new List<ConfigObjectNode>();
        foreach (var combo in combinations)
        {
            var iterationArgs = new List<string>(nonSweepArgs);
            foreach (var (k, v) in combo)
            {
                iterationArgs.Add($"{k}={v}");
            }

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

    public IReadOnlyList<T> BuildSweeps<T>()
    {
        var nodes = BuildSweepNodes();
        return nodes.Select(ConfigBinder.Bind<T>).ToList();
    }

    public T Build<T>()
    {
        var rootNode = BuildNode();
        return ConfigBinder.Bind<T>(rootNode);
    }
}

public class OmegaConfBuilder<T>
{
    private readonly OmegaConfBuilder _inner = new();

    public OmegaConfBuilder<T> SetRootDirectory(string rootDir)
    {
        _inner.SetRootDirectory(rootDir);
        return this;
    }

    public OmegaConfBuilder<T> SetRoot(string rootDir) => SetRootDirectory(rootDir);

    public OmegaConfBuilder<T> SetEntryFile(string entryFile)
    {
        _inner.SetEntryFile(entryFile);
        return this;
    }

    public OmegaConfBuilder<T> SelectPath(string group, string path)
    {
        _inner.SelectPath(group, path);
        return this;
    }

    public OmegaConfBuilder<T> DropGroup(string group)
    {
        _inner.DropGroup(group);
        return this;
    }

    public OmegaConfBuilder<T> AddCliArgs(IEnumerable<string> args)
    {
        _inner.AddCliArgs(args);
        return this;
    }

    public OmegaConfBuilder<T> AddCliArgs(params string[] args) => AddCliArgs((IEnumerable<string>)args);

    public OmegaConfBuilder<T> RegisterResolver(string name, Func<string, string> handler)
    {
        _inner.RegisterResolver(name, handler);
        return this;
    }

    public OmegaConfBuilder<T> RegisterResolver(IResolver resolver)
    {
        _inner.RegisterResolver(resolver);
        return this;
    }

    public ConfigObjectNode BuildNode() => _inner.BuildNode();

    public IReadOnlyList<ConfigObjectNode> BuildSweepNodes() => _inner.BuildSweepNodes();

    public IReadOnlyList<T> BuildSweeps() => _inner.BuildSweeps<T>();

    public T Build() => _inner.Build<T>();
}
