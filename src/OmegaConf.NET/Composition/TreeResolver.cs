using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;
using OmegaConf.NET.Parsers;

namespace OmegaConf.NET.Composition;

/// <summary>
/// Resolves hierarchical configuration composition from a directory tree, handling:
/// - Hydra-style <c>defaults:</c> lists and <c>_self_</c> precedence ordering
/// - Multi-level automatic folder cascading (e.g. <c>db/postgres.yaml</c> -> <c>db/cloud/postgres.yaml</c> -> <c>db/cloud/aws/postgres.yaml</c>)
/// - Explicit inheritance via <c>_inherits_</c> or <c>_base_</c> (including cross-format inheritance)
/// - Circular dependency detection via a canonical file path loading stack
/// </summary>
public class TreeResolver
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml", ".json", ".jsonc", ".toml"];
    private static readonly string[] InheritKeys = ["_inherits_", "_base_"];

    private readonly ConfigFormatRegistry _registry;
    private readonly HashSet<string> _loadingStack = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="TreeResolver"/> class.
    /// </summary>
    /// <param name="registry">Optional custom format registry; defaults to <see cref="ConfigFormatRegistry.Default"/>.</param>
    public TreeResolver(ConfigFormatRegistry? registry = null)
    {
        _registry = registry ?? ConfigFormatRegistry.Default;
    }

    /// <summary>
    /// Resolves and composes the configuration tree starting from <paramref name="rootDir"/> and <paramref name="entryFile"/>.
    /// </summary>
    /// <param name="rootDir">The root configuration directory.</param>
    /// <param name="entryFile">The entrypoint configuration filename (defaults to <c>"config.yaml"</c>).</param>
    /// <param name="selectedPaths">Optional config group path overrides supplied via builder or CLI.</param>
    /// <param name="droppedGroups">Optional config group names to exclude (e.g. <c>~db</c>).</param>
    /// <returns>The composed <see cref="ConfigObjectNode"/> prior to CLI value overrides and variable interpolation.</returns>
    public ConfigObjectNode Resolve(
        string rootDir,
        string entryFile = "config.yaml",
        IDictionary<string, string>? selectedPaths = null,
        ISet<string>? droppedGroups = null)
    {
        var activeSelections = selectedPaths != null
            ? new Dictionary<string, string>(selectedPaths, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var activeDropped = droppedGroups != null
            ? new HashSet<string>(droppedGroups, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string fullEntryPath = FindConfigFile(rootDir, entryFile)
            ?? throw new FileNotFoundException($"Entry configuration file '{entryFile}' not found in directory '{rootDir}'.");

        return LoadFileWithHierarchy(rootDir, fullEntryPath, activeSelections, activeDropped, isEntry: true);
    }

    /// <summary>
    /// Loads a single configuration file, resolves any explicit <c>_inherits_</c> base files,
    /// and (if it is the entrypoint) processes its <c>defaults:</c> composition list.
    /// Guards against circular inheritance using <see cref="_loadingStack"/>.
    /// </summary>
    private ConfigObjectNode LoadFileWithHierarchy(
        string rootDir,
        string filePath,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups,
        bool isEntry = false)
    {
        string canonicalPath = Path.GetFullPath(filePath);
        if (!_loadingStack.Add(canonicalPath))
        {
            throw new ConfigInheritanceException($"Circular configuration dependency detected: '{canonicalPath}'.", new SourceInfo(filePath, 0, 0));
        }

        try
        {
            var rawNode = _registry.ParseFile(canonicalPath);

            // Step 1: Resolve explicit inheritance (_inherits_ or _base_) if declared in this file
            var baseNode = ResolveExplicitInheritance(rootDir, Path.GetDirectoryName(canonicalPath)!, rawNode, selectedPaths, droppedGroups);

            ConfigObjectNode currentNode = baseNode != null
                ? (ConfigObjectNode)OperatorEngine.Apply(baseNode, rawNode)
                : rawNode;

            if (!isEntry)
            {
                return currentNode;
            }

            // Step 2: If this is the entrypoint file, process its defaults list and config groups
            var defaults = DefaultsListParser.Parse(currentNode);
            if (defaults.Count == 0)
            {
                currentNode.RemoveChild(DefaultsListParser.DefaultsKey);
                return currentNode;
            }

            return ProcessDefaultsList(rootDir, canonicalPath, currentNode, defaults, selectedPaths, droppedGroups);
        }
        finally
        {
            _loadingStack.Remove(canonicalPath);
        }
    }

    /// <summary>
    /// Merges all entries in the <c>defaults:</c> list in order, respecting the position of <c>_self_</c>.
    /// Entries before <c>_self_</c> are overridden by the entry file; entries after <c>_self_</c> override the entry file.
    /// </summary>
    private ConfigObjectNode ProcessDefaultsList(
        string rootDir,
        string entryFilePath,
        ConfigObjectNode entryNode,
        List<DefaultsEntry> defaults,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups)
    {
        // Locate _self_ in the defaults list; if omitted, Hydra semantics place _self_ at the end
        int selfIndex = defaults.FindIndex(d => d.IsSelf);
        if (selfIndex < 0)
        {
            selfIndex = defaults.Count;
        }

        var beforeSelf = defaults.Take(selfIndex).Where(d => !d.IsSelf).ToList();
        var afterSelf = defaults.Skip(selfIndex + 1).Where(d => !d.IsSelf).ToList();

        ConfigObjectNode composed = new(source: new SourceInfo(entryFilePath, 0, 0));

        // 1. Apply defaults listed before _self_
        foreach (var entry in beforeSelf)
        {
            ApplyDefaultEntry(composed, rootDir, entry, selectedPaths, droppedGroups);
        }

        // 2. Apply _self_ (the local properties of the entry file itself, excluding the 'defaults' key)
        var entryClone = (ConfigObjectNode)entryNode.Clone();
        entryClone.RemoveChild(DefaultsListParser.DefaultsKey);
        composed = (ConfigObjectNode)OperatorEngine.Apply(composed, entryClone);

        // 3. Apply defaults listed after _self_
        foreach (var entry in afterSelf)
        {
            ApplyDefaultEntry(composed, rootDir, entry, selectedPaths, droppedGroups);
        }

        return composed;
    }

    /// <summary>
    /// Resolves a single <see cref="DefaultsEntry"/> and mounts the resulting node under its group key on <paramref name="target"/>.
    /// </summary>
    private void ApplyDefaultEntry(
        ConfigObjectNode target,
        string rootDir,
        DefaultsEntry entry,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups)
    {
        if (string.IsNullOrEmpty(entry.Group)) return;

        // Skip if the group was dropped via CLI (~group) or builder.DropGroup(group)
        if (droppedGroups.Contains(entry.Group))
        {
            return;
        }

        // Check if the group's path was overridden via CLI (group=new/path) or builder.SelectPath(...)
        string subPath = selectedPaths.TryGetValue(entry.Group, out var overridePath)
            ? overridePath
            : entry.Path ?? entry.Group;

        var groupNode = ResolveGroupWithCascading(rootDir, entry.Group, subPath, entry.IsOptional, selectedPaths, droppedGroups);
        if (groupNode != null)
        {
            if (target.TryGetChild(entry.Group, out var existingGroupNode))
            {
                target.SetChild(entry.Group, OperatorEngine.Apply(existingGroupNode, groupNode));
            }
            else
            {
                target.SetChild(entry.Group, groupNode);
            }
        }
    }

    /// <summary>
    /// Loads a config group file, automatically cascading down from parent directories that contain a file with the same name.
    /// For example, selecting <c>db=cloud/aws/postgres</c> automatically loads and merges:
    /// 1. <c>db/postgres.yaml</c> (if present)
    /// 2. <c>db/cloud/postgres.yaml</c> (if present)
    /// 3. <c>db/cloud/aws/postgres.yaml</c>
    /// </summary>
    private ConfigObjectNode? ResolveGroupWithCascading(
        string rootDir,
        string group,
        string subPath,
        bool isOptional,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups)
    {
        string groupDir = Path.Combine(rootDir, group);

        string? resolvedFile = FindConfigFile(groupDir, subPath);

        if (resolvedFile == null)
        {
            if (isOptional) return null;
            throw new FileNotFoundException($"Configuration file for group '{group}' at path '{subPath}' was not found in '{groupDir}'.");
        }

        string fileName = Path.GetFileName(resolvedFile);
        string relativeSubDir = Path.GetDirectoryName(subPath) ?? "";

        ConfigObjectNode? accumulatedCascadeNode = null;

        if (!string.IsNullOrEmpty(relativeSubDir))
        {
            // Walk from groupDir down through each intermediate subdirectory (excluding the leaf file itself)
            string[] subSegments = relativeSubDir.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            string currentCascadeDir = groupDir;

            for (int i = 0; i < subSegments.Length; i++)
            {
                string parentFileCandidate = Path.Combine(currentCascadeDir, fileName);
                if (File.Exists(parentFileCandidate) &&
                    !string.Equals(Path.GetFullPath(parentFileCandidate), Path.GetFullPath(resolvedFile), StringComparison.OrdinalIgnoreCase))
                {
                    var layerNode = LoadFileWithHierarchy(rootDir, parentFileCandidate, selectedPaths, droppedGroups);
                    accumulatedCascadeNode = accumulatedCascadeNode != null
                        ? (ConfigObjectNode)OperatorEngine.Apply(accumulatedCascadeNode, layerNode)
                        : layerNode;
                }

                currentCascadeDir = Path.Combine(currentCascadeDir, subSegments[i]);
            }
        }

        var currentNode = LoadFileWithHierarchy(rootDir, resolvedFile, selectedPaths, droppedGroups);

        if (accumulatedCascadeNode != null)
        {
            return (ConfigObjectNode)OperatorEngine.Apply(accumulatedCascadeNode, currentNode);
        }

        return currentNode;
    }

    /// <summary>
    /// Resolves explicit file inheritance declared via <c>_inherits_</c> or <c>_base_</c> (supports a single path or a list of paths).
    /// </summary>
    private ConfigObjectNode? ResolveExplicitInheritance(
        string rootDir,
        string currentDir,
        ConfigObjectNode node,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups)
    {
        foreach (var inheritKey in InheritKeys)
        {
            if (node.TryGetChild(inheritKey, out var inheritNode))
            {
                node.RemoveChild(inheritKey);

                List<string> pathsToLoad = [];
                if (inheritNode is ConfigValueNode val && val.Value is string single)
                {
                    pathsToLoad.Add(single);
                }
                else if (inheritNode is ConfigListNode list)
                {
                    foreach (var it in list.Items)
                    {
                        if (it is ConfigValueNode iv && iv.Value is string s)
                        {
                            pathsToLoad.Add(s);
                        }
                    }
                }

                ConfigObjectNode? accumulatedBase = null;

                foreach (var relPath in pathsToLoad)
                {
                    string targetFile = Path.Combine(currentDir, relPath);
                    string? foundFile = FindConfigFile(Path.GetDirectoryName(targetFile)!, Path.GetFileName(targetFile));

                    if (foundFile == null || !File.Exists(foundFile))
                    {
                        throw new FileNotFoundException($"Inherited configuration file '{relPath}' not found from '{currentDir}'.");
                    }

                    var loaded = LoadFileWithHierarchy(rootDir, foundFile, selectedPaths, droppedGroups);
                    accumulatedBase = accumulatedBase != null
                        ? (ConfigObjectNode)OperatorEngine.Apply(accumulatedBase, loaded)
                        : loaded;
                }

                return accumulatedBase;
            }
        }

        return null;
    }

    /// <summary>
    /// Searches for a configuration file in <paramref name="directory"/>, probing supported extensions
    /// (<c>.yaml</c>, <c>.yml</c>, <c>.json</c>, <c>.jsonc</c>, <c>.toml</c>) when no extension is specified.
    /// </summary>
    private static string? FindConfigFile(string directory, string filename)
    {
        if (File.Exists(filename)) return filename;

        string direct = Path.Combine(directory, filename);
        if (File.Exists(direct)) return direct;

        string withoutExt = Path.Combine(directory, filename);
        foreach (var ext in SupportedExtensions)
        {
            string candidate = withoutExt + ext;
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
