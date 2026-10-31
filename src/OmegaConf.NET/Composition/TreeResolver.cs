using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;
using OmegaConf.NET.Parsers;

namespace OmegaConf.NET.Composition;

public class TreeResolver
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml", ".json", ".jsonc", ".toml"];
    private static readonly string[] InheritKeys = ["_inherits_", "_base_"];

    private readonly ConfigFormatRegistry _registry;
    private readonly HashSet<string> _loadingStack = new(StringComparer.OrdinalIgnoreCase);

    public TreeResolver(ConfigFormatRegistry? registry = null)
    {
        _registry = registry ?? ConfigFormatRegistry.Default;
    }

    /// <summary>
    /// Resolves and composes the configuration tree starting from root directory and entry file.
    /// </summary>
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

            // Handle explicit inheritance (_inherits_ or _base_)
            var baseNode = ResolveExplicitInheritance(rootDir, Path.GetDirectoryName(canonicalPath)!, rawNode, selectedPaths, droppedGroups);

            ConfigObjectNode currentNode = baseNode != null
                ? (ConfigObjectNode)OperatorEngine.Apply(baseNode, rawNode)
                : rawNode;

            if (!isEntry)
            {
                return currentNode;
            }

            // If this is the entry file, process defaults list and config groups
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

    private ConfigObjectNode ProcessDefaultsList(
        string rootDir,
        string entryFilePath,
        ConfigObjectNode entryNode,
        List<DefaultsEntry> defaults,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups)
    {
        // Find if _self_ is explicitly listed
        int selfIndex = defaults.FindIndex(d => d.IsSelf);
        if (selfIndex < 0)
        {
            // By default Hydra appends _self_ at the end
            selfIndex = defaults.Count;
        }

        var beforeSelf = defaults.Take(selfIndex).Where(d => !d.IsSelf).ToList();
        var afterSelf = defaults.Skip(selfIndex + 1).Where(d => !d.IsSelf).ToList();

        ConfigObjectNode composed = new(source: new SourceInfo(entryFilePath, 0, 0));

        // 1. Apply defaults before _self_
        foreach (var entry in beforeSelf)
        {
            ApplyDefaultEntry(composed, rootDir, entry, selectedPaths, droppedGroups);
        }

        // 2. Apply _self_ (entry file)
        var entryClone = (ConfigObjectNode)entryNode.Clone();
        entryClone.RemoveChild(DefaultsListParser.DefaultsKey);
        composed = (ConfigObjectNode)OperatorEngine.Apply(composed, entryClone);

        // 3. Apply defaults after _self_
        foreach (var entry in afterSelf)
        {
            ApplyDefaultEntry(composed, rootDir, entry, selectedPaths, droppedGroups);
        }

        return composed;
    }

    private void ApplyDefaultEntry(
        ConfigObjectNode target,
        string rootDir,
        DefaultsEntry entry,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups)
    {
        if (string.IsNullOrEmpty(entry.Group)) return;

        // Check if group is dropped via CLI (e.g. ~db)
        if (droppedGroups.Contains(entry.Group))
        {
            return;
        }

        // Check if path is overridden by CLI or builder selection
        string subPath = selectedPaths.TryGetValue(entry.Group, out var overridePath)
            ? overridePath
            : entry.Path ?? entry.Group;

        // Resolve group file with auto-cascading
        var groupNode = ResolveGroupWithCascading(rootDir, entry.Group, subPath, entry.IsOptional, selectedPaths, droppedGroups);
        if (groupNode != null)
        {
            // Mount group into composed configuration:
            // If the group has a key, mount it under target[group] or at root depending on whether the group is a container
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
    /// Loads a config group file, automatically cascading up parent directories with the same filename.
    /// E.g. db/cloud/aws/postgres.yaml cascades on top of db/cloud/postgres.yaml and db/postgres.yaml.
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
            // Walk from groupDir down through each intermediate subdirectory (excluding the target file itself)
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
