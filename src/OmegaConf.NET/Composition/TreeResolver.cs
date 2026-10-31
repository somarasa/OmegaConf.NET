using OmegaConf.NET.Ast;
using OmegaConf.NET.Cli;
using OmegaConf.NET.Common;
using OmegaConf.NET.Interpolation;
using OmegaConf.NET.Operators;
using OmegaConf.NET.Parsers;

namespace OmegaConf.NET.Composition;

/// <summary>
/// Resolves hierarchical configuration composition from a directory tree and/or in-memory <see cref="ConfigStore"/>, handling:
/// - Hydra-style <c>defaults:</c> lists, <c>_self_</c> precedence ordering, and interpolated default entries (<c>${...}</c>)
/// - Multi-level automatic folder cascading (e.g. <c>db/postgres.yaml</c> -> <c>db/cloud/postgres.yaml</c> -> <c>db/cloud/aws/postgres.yaml</c>)
/// - Explicit inheritance via <c>_inherits_</c> or <c>_base_</c> (including cross-format inheritance)
/// - Circular dependency detection via a canonical file path loading stack
/// </summary>
public class TreeResolver
{
    private static readonly string[] SupportedExtensions = [".yaml", ".yml", ".json", ".jsonc", ".toml"];
    private static readonly string[] InheritKeys = ["_inherits_", "_base_"];

    private readonly ConfigFormatRegistry _registry;
    private readonly ConfigStore _configStore;
    private readonly IReadOnlyList<IResolver> _customResolvers;
    private readonly HashSet<string> _loadingStack = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _searchPaths = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="TreeResolver"/> class.
    /// </summary>
    /// <param name="registry">Optional custom format registry; defaults to <see cref="ConfigFormatRegistry.Default"/>.</param>
    /// <param name="configStore">Optional in-memory <see cref="ConfigStore"/>; defaults to <see cref="ConfigStore.Instance"/>.</param>
    /// <param name="customResolvers">Optional custom interpolation resolvers used when evaluating interpolated <c>defaults:</c> entries.</param>
    public TreeResolver(
        ConfigFormatRegistry? registry = null,
        ConfigStore? configStore = null,
         IReadOnlyList<IResolver>? customResolvers = null)
    {
        _registry = registry ?? ConfigFormatRegistry.Default;
        _configStore = configStore ?? ConfigStore.Instance;
        _customResolvers = customResolvers ?? [];
    }

    /// <summary>
    /// Gets the active search paths resolved during the most recent <see cref="Resolve"/> call.
    /// </summary>
    public IReadOnlyList<string> ActiveSearchPaths => _searchPaths;

    /// <summary>
    /// Resolves and composes the configuration tree starting from <paramref name="rootDir"/> (and any additional <paramref name="searchPaths"/>)
    /// and <paramref name="entryFile"/>.
    /// </summary>
    /// <param name="rootDir">The primary root configuration directory.</param>
    /// <param name="entryFile">The entrypoint configuration filename (defaults to <c>"config.yaml"</c>).</param>
    /// <param name="selectedPaths">Optional config group path overrides supplied via builder or CLI.</param>
    /// <param name="droppedGroups">Optional config group names to exclude (e.g. <c>~db</c>).</param>
    /// <param name="searchPaths">Optional secondary configuration directories searched in order when a file or group is not found in <paramref name="rootDir"/>.</param>
    /// <returns>The composed <see cref="ConfigObjectNode"/> prior to CLI value overrides and variable interpolation.</returns>
    public ConfigObjectNode Resolve(
        string rootDir,
        string entryFile = "config.yaml",
        IDictionary<string, string>? selectedPaths = null,
        ISet<string>? droppedGroups = null,
        IEnumerable<string>? searchPaths = null)
    {
        _searchPaths = [rootDir];
        if (searchPaths != null)
        {
            foreach (var sp in searchPaths)
            {
                if (!string.IsNullOrWhiteSpace(sp) && !_searchPaths.Contains(sp, StringComparer.OrdinalIgnoreCase))
                {
                    _searchPaths.Add(sp);
                }
            }
        }

        var activeSelections = selectedPaths != null
            ? new Dictionary<string, string>(selectedPaths, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var activeDropped = droppedGroups != null
            ? new HashSet<string>(droppedGroups, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? fullEntryPath = FindConfigFileAcrossSearchPaths(entryFile);
        if (fullEntryPath != null)
        {
            return LoadFileWithHierarchy(rootDir, fullEntryPath, activeSelections, activeDropped, isEntry: true);
        }

        if (_configStore.TryLoad(entryFile, out var storedEntry) && storedEntry != null)
        {
            return LoadNodeWithHierarchy(rootDir, $"store://{storedEntry.Name}", storedEntry.Node, activeSelections, activeDropped, isEntry: true);
        }

        throw new FileNotFoundException($"Entry configuration file '{entryFile}' not found in directory '{rootDir}' or in ConfigStore.");
    }

    /// <summary>
    /// Loads and parses the raw primary entrypoint configuration node without resolving its <c>defaults:</c> list,
    /// or returns <c>null</c> if the entrypoint cannot be found. Used for early <c>hydra.aliases</c> extraction.
    /// </summary>
    public ConfigObjectNode? TryLoadPrimaryEntryRaw(string rootDir, string entryFile, IEnumerable<string>? searchPaths = null)
    {
        var dirs = new List<string> { rootDir };
        if (searchPaths != null)
        {
            dirs.AddRange(searchPaths.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        foreach (var dir in dirs)
        {
            var found = FindConfigFile(dir, entryFile);
            if (found != null)
            {
                return _registry.ParseFile(Path.GetFullPath(found));
            }
        }

        if (_configStore.TryLoad(entryFile, out var storedEntry) && storedEntry != null)
        {
            return storedEntry.Node;
        }

        return null;
    }

    /// <summary>
    /// Loads a single configuration file, resolves any explicit <c>_inherits_</c> base files,
    /// and processes its <c>defaults:</c> composition list if present.
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
        var rawNode = _registry.ParseFile(canonicalPath);
        if (!isEntry)
        {
            OverrideAliases.ValidateNoAliasesInIncludedConfig(rawNode, canonicalPath);
        }
        return LoadNodeWithHierarchy(rootDir, canonicalPath, rawNode, selectedPaths, droppedGroups, isEntry);
    }

    private ConfigObjectNode LoadNodeWithHierarchy(
        string rootDir,
        string canonicalId,
        ConfigObjectNode rawNode,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups,
        bool isEntry = false)
    {
        if (!_loadingStack.Add(canonicalId))
        {
            throw new ConfigInheritanceException($"Circular configuration dependency detected: '{canonicalId}'.", new SourceInfo(canonicalId, 0, 0));
        }

        try
        {
            string currentDir = canonicalId.StartsWith("store://", StringComparison.OrdinalIgnoreCase)
                ? rootDir
                : (Path.GetDirectoryName(canonicalId) ?? rootDir);

            // Step 1: Resolve explicit inheritance (_inherits_ or _base_) if declared in this file
            var baseNode = ResolveExplicitInheritance(rootDir, currentDir, rawNode, selectedPaths, droppedGroups);

            ConfigObjectNode currentNode = baseNode != null
                ? (ConfigObjectNode)OperatorEngine.Apply(baseNode, rawNode)
                : rawNode;

            // Step 2: Process defaults list if declared in this file (supported in both entrypoint and nested group files)
            if (isEntry)
            {
                ExtractSearchPathsFromConfig(rootDir, currentNode);
            }

            var defaults = DefaultsListParser.Parse(currentNode);

            if (isEntry)
            {
                // Append any dynamically added CLI config groups (e.g. "+db@replica=sqlite" or "+experiment@_global_=exp1")
                AppendDynamicSelectedGroups(defaults, selectedPaths);
            }

            if (defaults.Count == 0)
            {
                currentNode.RemoveChild(DefaultsListParser.DefaultsKey);
                return currentNode;
            }

            return ProcessDefaultsList(rootDir, canonicalId, currentNode, defaults, selectedPaths, droppedGroups);
        }
        finally
        {
            _loadingStack.Remove(canonicalId);
        }
    }

    private void ExtractSearchPathsFromConfig(string rootDir, ConfigObjectNode currentNode)
    {
        foreach (var sectionKey in new[] { "hydra", "omegaconf" })
        {
            if (currentNode.TryGetChild(sectionKey, out var secNode) &&
                secNode is ConfigObjectNode secObj &&
                secObj.TryGetChild("searchpath", out var spNode) &&
                spNode is ConfigListNode spList)
            {
                foreach (var item in spList.Items.OfType<ConfigValueNode>())
                {
                    string rawPath = item.Value?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(rawPath)) continue;
                    if (rawPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                    {
                        rawPath = rawPath["file://".Length..].TrimStart('/');
                    }
                    string resolved = Path.IsPathRooted(rawPath) ? rawPath : Path.GetFullPath(Path.Combine(rootDir, rawPath));
                    if (!_searchPaths.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                    {
                        _searchPaths.Add(resolved);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Appends any config groups requested via <paramref name="selectedPaths"/> (such as <c>+experiment@_global_=fast</c>
    /// or <c>db@analytics_db=postgres</c>) that are not already present in the entrypoint's <c>defaults:</c> list.
    /// </summary>
    private void AppendDynamicSelectedGroups(
        List<DefaultsEntry> defaults,
        Dictionary<string, string> selectedPaths)
    {
        foreach (var (rawKey, val) in selectedPaths)
        {
            if (rawKey.StartsWith("++", StringComparison.Ordinal))
            {
                string forceGroup = rawKey.TrimStart('+');
                DefaultsListParser.SplitPackage(forceGroup, out forceGroup, out _);
                if (IsKnownGroup(forceGroup))
                {
                    throw new ConfigSyntaxException($"Force-add ('++') of config groups is not supported: '{rawKey}={val}'.");
                }
            }

            string key = rawKey.TrimStart('+');
            DefaultsListParser.SplitPackage(key, out string group, out string? pkg);

            // Check if this group/package combination already exists in defaults
            bool alreadyInDefaults = defaults.Any(d =>
                !d.IsSelf &&
                string.Equals(d.Group, group, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.Package ?? "", pkg ?? "", StringComparison.OrdinalIgnoreCase));

            if (!alreadyInDefaults && ( pkg != null || IsKnownGroup(group)))
            {
                defaults.Add(DefaultsEntry.GroupChoice(group, val, isOptional: false, package: pkg));
            }
        }
    }

    private bool IsKnownGroup(string group)
    {
        return _searchPaths.Any(sp => Directory.Exists(Path.Combine(sp, group))) ||
               _configStore.HasGroup(group);
    }

    /// <summary>
    /// Merges all entries in the <c>defaults:</c> list in order, respecting the position of <c>_self_</c>.
    /// Entries before <c>_self_</c> are overridden by the entry file; entries after <c>_self_</c> override the entry file.
    /// Also resolves <c>override</c> entries (e.g. <c>- override db: mysql</c>) and interpolated default entries (<c>${...}</c>).
    /// </summary>
    private ConfigObjectNode ProcessDefaultsList(
        string rootDir,
        string entryFilePath,
        ConfigObjectNode entryNode,
        List<DefaultsEntry> rawDefaults,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups)
    {
        // Normalize 'override' entries:
        // 1. If an 'override' entry matches an earlier entry in the same defaults list, update that earlier entry in-place.
        // 2. Also register 'override' entries in selectedPaths (if not already overridden by CLI) so nested defaults lists pick them up.
        // 3. Reject conflicting non-override entries for the same group/package.
        var defaults = new List<DefaultsEntry>();
        foreach (var entry in rawDefaults)
        {
            if (!entry.IsSelf && entry.Group != null)
            {
                int existingIdx = defaults.FindIndex(d =>
                    !d.IsSelf &&
                    string.Equals(d.SelectionKey, entry.SelectionKey, StringComparison.OrdinalIgnoreCase));

                if (entry.IsOverride)
                {
                    if (!selectedPaths.ContainsKey(entry.SelectionKey) && entry.Path != null)
                    {
                        selectedPaths[entry.SelectionKey] = entry.Path;
                    }

                    if (existingIdx >= 0)
                    {
                        defaults[existingIdx] = DefaultsEntry.GroupChoice(
                            defaults[existingIdx].Group!,
                            entry.Path,
                            entry.IsOptional || defaults[existingIdx].IsOptional,
                            entry.Package ?? defaults[existingIdx].Package,
                            isMissing: entry.IsMissing,
                            isDisabled: entry.IsDisabled,
                            multiPaths: entry.MultiPaths);
                        continue;
                    }
                }
                else if (existingIdx >= 0 && !string.Equals(defaults[existingIdx].Path, entry.Path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ConfigSyntaxException(
                        $"Multiple values for '{entry.SelectionKey}' in defaults list ('{defaults[existingIdx].Path}' and '{entry.Path}'). " +
                        $"To override an earlier value, use 'override {entry.SelectionKey}: {entry.Path}'.",
                        entryNode.Source);
                }
            }

            defaults.Add(entry);
        }

        // Locate _self_ in the defaults list; if omitted, Hydra semantics place _self_ at the end
        int selfIndex = defaults.FindIndex(d => d.IsSelf);
        if (selfIndex < 0)
        {
            selfIndex = defaults.Count;
        }

        var beforeSelf = defaults.Take(selfIndex).Where(d => !d.IsSelf).ToList();
        var afterSelf = defaults.Skip(selfIndex + 1).Where(d => !d.IsSelf).ToList();

        ConfigObjectNode composed = new(source: new SourceInfo(entryFilePath, 0, 0));
        var entryClone = (ConfigObjectNode)entryNode.Clone();
        entryClone.RemoveChild(DefaultsListParser.DefaultsKey);

        var resolvedGroupChoices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Apply defaults listed before _self_
        foreach (var entry in beforeSelf)
        {
            if (entry.IsOverride && HasGroupAlreadyBeenMounted(composed, entry)) continue;
            ApplyDefaultEntry(composed, entryClone, rootDir, entry, defaults, selectedPaths, droppedGroups, resolvedGroupChoices);
        }

        // 2. Apply _self_ (the local properties of the entry file itself, excluding the 'defaults' key)
        composed = (ConfigObjectNode)OperatorEngine.Apply(composed, (ConfigObjectNode)entryClone.Clone());

        // 3. Apply defaults listed after _self_
        foreach (var entry in afterSelf)
        {
            if (entry.IsOverride && HasGroupAlreadyBeenMounted(composed, entry)) continue;
            ApplyDefaultEntry(composed, entryClone, rootDir, entry, defaults, selectedPaths, droppedGroups, resolvedGroupChoices);
        }

        return composed;
    }

    private static bool HasGroupAlreadyBeenMounted(ConfigObjectNode composed, DefaultsEntry entry)
    {
        string targetPkg = entry.Package ?? entry.Group ?? "";
        if (string.IsNullOrEmpty(targetPkg) || string.Equals(targetPkg, DefaultsEntry.GlobalPackage, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return composed.TryGetChild(targetPkg, out _);
    }

    /// <summary>
    /// Resolves a single <see cref="DefaultsEntry"/> and mounts the resulting node under its target package
    /// (which defaults to <see cref="DefaultsEntry.Group"/>, or merges into the root when <c>@_global_</c> is used).
    /// Evaluates any <c>${...}</c> interpolation in the default entry's path against earlier group choices and composed properties.
    /// </summary>
    private void ApplyDefaultEntry(
        ConfigObjectNode target,
        ConfigObjectNode entrySelfNode,
        string rootDir,
        DefaultsEntry entry,
        List<DefaultsEntry> allDefaults,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups,
        Dictionary<string, string> resolvedGroupChoices)
    {
        if (string.IsNullOrEmpty(entry.Group)) return;

        // Skip if the group (or group@package) was dropped via CLI (~group or ~group@package) or builder.DropGroup(...)
        if (droppedGroups.Contains(entry.SelectionKey) ||
            (string.IsNullOrEmpty(entry.Package) && droppedGroups.Contains(entry.Group)))
        {
            return;
        }

        // Check conditional group deletion (~group=expected_choice)
        foreach (var dropped in droppedGroups)
        {
            int eqIdx = dropped.IndexOf('=');
            if (eqIdx > 0)
            {
                string delKey = dropped[..eqIdx].Trim();
                string delExpected = dropped[(eqIdx + 1)..].Trim().TrimStart('/');
                if (string.Equals(delKey, entry.SelectionKey, StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrEmpty(entry.Package) && string.Equals(delKey, entry.Group, StringComparison.OrdinalIgnoreCase)))
                {
                    if (string.Equals(delExpected, entry.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    throw new ConfigSyntaxException(
                        $"Could not delete '{dropped}'. Active choice in defaults list is '{entry.Path}'.",
                        target.Source);
                }
            }
        }

        // Check if the group's path was overridden via CLI (group@pkg=new/path or group=new/path) or builder.SelectPath(...)
        string? subPath = null;
        bool wasOverridden = false;
        if (selectedPaths.TryGetValue(entry.SelectionKey, out var exactOverride))
        {
            subPath = exactOverride;
            wasOverridden = true;
        }
        else if (selectedPaths.TryGetValue("+" + entry.SelectionKey, out var plusOverride))
        {
            subPath = plusOverride;
            wasOverridden = true;
        }
        else if (string.IsNullOrEmpty(entry.Package) && selectedPaths.TryGetValue(entry.Group, out var groupOverride))
        {
            subPath = groupOverride;
            wasOverridden = true;
        }
        else if (!entry.IsDisabled)
        {
            subPath = entry.Path ?? entry.Group;
        }

        // Disabled group (- db: null or CLI db=null)
        if ((entry.IsDisabled && !wasOverridden) ||
            string.IsNullOrWhiteSpace(subPath) ||
            string.Equals(subPath, "null", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Evaluate interpolated defaults entry (e.g. "- db: ${default_db}", "- group1/group2/${group1}", "- db: ${oc.env:DB_TYPE, postgres}")
        if (subPath.Contains("${", StringComparison.Ordinal))
        {
            subPath = ResolveInterpolatedDefaultPath(subPath, entry, target, entrySelfNode, allDefaults, selectedPaths, resolvedGroupChoices);
        }

        // Mandatory missing group (- db: ??? when not overridden)
        if (string.Equals(subPath, "???", StringComparison.Ordinal))
        {
            throw new MissingValueMarkerException(entry.SelectionKey, target.Source);
        }

        resolvedGroupChoices[entry.SelectionKey] = subPath;
        if (string.IsNullOrEmpty(entry.Package))
        {
            resolvedGroupChoices[entry.Group] = subPath;
        }

        // Multi-config list selection (- callbacks: [a, b] or CLI callbacks=[a,b])
        List<string> pathsToLoad;
        if (!wasOverridden && entry.MultiPaths is { Count: > 0 })
        {
            pathsToLoad = entry.MultiPaths
                .Select(p => p.Contains("${", StringComparison.Ordinal)
                    ? ResolveInterpolatedDefaultPath(p, entry, target, entrySelfNode, allDefaults, selectedPaths, resolvedGroupChoices)
                    : p)
                .ToList();
        }
        else if (subPath.StartsWith('[') && subPath.EndsWith(']'))
        {
            pathsToLoad = subPath[1..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimStart('/'))
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }
        else
        {
            pathsToLoad = [subPath];
        }

        foreach (var singleSubPath in pathsToLoad)
        {
            var (groupNode, filePackageHeader) = ResolveGroupWithCascading(rootDir, entry.Group, singleSubPath, entry.IsOptional, selectedPaths, droppedGroups);
            if (groupNode != null)
            {
                // Precedence for target package: explicit DefaultsEntry @package > file '# @package' header > group name
                string targetPackage = entry.Package ?? filePackageHeader ?? entry.Group.Replace('/', '.');
                if (targetPackage.Equals("_group_", StringComparison.OrdinalIgnoreCase))
                {
                    targetPackage = entry.Group.Replace('/', '.');
                }
                else if (targetPackage.StartsWith("_group_.", StringComparison.OrdinalIgnoreCase))
                {
                    targetPackage = entry.Group.Replace('/', '.') + targetPackage["_group_".Length..];
                }

                MountPackageNode(target, targetPackage, groupNode);
            }
        }
    }

    private string ResolveInterpolatedDefaultPath(
        string rawSubPath,
        DefaultsEntry currentEntry,
        ConfigObjectNode composedSoFar,
        ConfigObjectNode entrySelfNode,
        List<DefaultsEntry> allDefaults,
        Dictionary<string, string> selectedPaths,
        Dictionary<string, string> resolvedGroupChoices)
    {
        // Detect circular interpolation across defaults entries (e.g. group1: ${group2}, group2: ${group1})
        foreach (var other in allDefaults)
        {
            if (other.IsSelf || string.IsNullOrEmpty(other.Group) || ReferenceEquals(other, currentEntry)) continue;
            if (rawSubPath.Contains($"${{{other.SelectionKey}}}", StringComparison.OrdinalIgnoreCase) ||
                rawSubPath.Contains($"${{{other.Group}}}", StringComparison.OrdinalIgnoreCase))
            {
                string? otherPath = selectedPaths.TryGetValue(other.SelectionKey, out var ov) ? ov : other.Path;
                if (otherPath != null &&
                    (otherPath.Contains($"${{{currentEntry.SelectionKey}}}", StringComparison.OrdinalIgnoreCase) ||
                     otherPath.Contains($"${{{currentEntry.Group}}}", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ConfigInterpolationException(
                        $"Circular interpolation detected in defaults list between '{currentEntry.SelectionKey}' and '{other.SelectionKey}'.",
                        composedSoFar.Source);
                }
            }
        }

        // Build evaluation context combining composed properties, _self_ properties, and earlier resolved group choices
        var contextNode = (ConfigObjectNode)OperatorEngine.Apply(composedSoFar.Clone(), entrySelfNode.Clone());
        foreach (var (groupKey, choiceVal) in resolvedGroupChoices)
        {
            // If groupKey in contextNode is a mounted ConfigObjectNode (or absent), allow ${groupKey} to resolve to the chosen config name
            if (!contextNode.TryGetChild(groupKey, out var existing) || existing is ConfigObjectNode)
            {
                contextNode.SetChild(groupKey, new ConfigValueNode(choiceVal), allowNewKeyInStruct: true);
            }
        }
        foreach (var (selKey, selVal) in selectedPaths)
        {
            if (!contextNode.TryGetChild(selKey, out var existingSel) || existingSel is ConfigValueNode)
            {
                contextNode.SetChild(selKey, new ConfigValueNode(selVal), allowNewKeyInStruct: true);
            }
        }

        var engine = new InterpolationEngine(contextNode, _customResolvers);
        var resolvedObj = engine.ResolveStringValue(rawSubPath, $"defaults.{currentEntry.SelectionKey}", composedSoFar.Source);
        return resolvedObj?.ToString()?.Trim().TrimStart('/') ?? "";
    }

    /// <summary>
    /// Mounts <paramref name="groupNode"/> onto <paramref name="root"/> according to <paramref name="targetPackage"/>:
    /// - <c>"_global_"</c>: merges <paramref name="groupNode"/>'s properties directly into <paramref name="root"/>.
    /// - Dot-separated package path (e.g. <c>"primary_db"</c> or <c>"storage.primary_db"</c>): mounts at that nested path.
    /// </summary>
    private static void MountPackageNode(ConfigObjectNode root, string targetPackage, ConfigObjectNode groupNode)
    {
        if (string.Equals(targetPackage, DefaultsEntry.GlobalPackage, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var (k, v) in groupNode.Children)
            {
                if (root.TryGetChild(k, out var existingChild))
                {
                    root.SetChild(k, OperatorEngine.Apply(existingChild, v));
                }
                else
                {
                    root.SetChild(k, v.Clone());
                }
            }
            return;
        }

        string[] parts = targetPackage.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        ConfigObjectNode current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetChild(parts[i], out var next) || next is not ConfigObjectNode nextObj)
            {
                nextObj = new ConfigObjectNode();
                current.SetChild(parts[i], nextObj);
            }
            current = nextObj;
        }

        string leafKey = parts[^1];
        if (current.TryGetChild(leafKey, out var existingGroupNode))
        {
            current.SetChild(leafKey, OperatorEngine.Apply(existingGroupNode, groupNode));
        }
        else
        {
            current.SetChild(leafKey, groupNode);
        }
    }

    /// <summary>
    /// Loads a config group file across the configured search paths, automatically cascading down from parent directories
    /// that contain a file with the same name. Also extracts any <c># @package</c> or <c>// @package</c> directive from the top of the leaf file.
    /// </summary>
    private (ConfigObjectNode? Node, string? FilePackageHeader) ResolveGroupWithCascading(
        string rootDir,
        string group,
        string subPath,
        bool isOptional,
        Dictionary<string, string> selectedPaths,
        HashSet<string> droppedGroups)
    {
        string? resolvedFile = null;
        string matchedGroupDir = Path.Combine(rootDir, group);

        foreach (var sp in _searchPaths)
        {
            string candidateGroupDir = Path.Combine(sp, group);
            resolvedFile = FindConfigFile(candidateGroupDir, subPath);
            if (resolvedFile != null)
            {
                matchedGroupDir = candidateGroupDir;
                break;
            }
        }

        if (resolvedFile == null)
        {
            if (_configStore.TryLoad(group, subPath, out var storedGroup) && storedGroup != null)
            {
                var composedStored = LoadNodeWithHierarchy(
                    rootDir,
                    $"store://{storedGroup.Group}/{storedGroup.Name}",
                    storedGroup.Node,
                    selectedPaths,
                    droppedGroups);
                return (composedStored, storedGroup.Package);
            }

            if (string.Equals(group, subPath, StringComparison.OrdinalIgnoreCase) &&
                _configStore.TryLoad(null, subPath, out var storedRoot) &&
                storedRoot != null)
            {
                var composedRootStored = LoadNodeWithHierarchy(
                    rootDir,
                    $"store://{storedRoot.Name}",
                    storedRoot.Node,
                    selectedPaths,
                    droppedGroups);
                return (composedRootStored, storedRoot.Package ?? DefaultsEntry.GlobalPackage);
            }

            if (isOptional) return (null, null);
            throw new FileNotFoundException($"Configuration file for group '{group}' at path '{subPath}' was not found in '{matchedGroupDir}' or in ConfigStore.");
        }

        string? filePackageHeader = ExtractFilePackageHeader(resolvedFile);
        string fileName = Path.GetFileName(resolvedFile);
        string relativeSubDir = Path.GetDirectoryName(subPath) ?? "";

        ConfigObjectNode? accumulatedCascadeNode = null;

        if (!string.IsNullOrEmpty(relativeSubDir))
        {
            // Walk from matchedGroupDir down through each intermediate subdirectory (excluding the leaf file itself)
            string[] subSegments = relativeSubDir.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            string currentCascadeDir = matchedGroupDir;

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
            return ((ConfigObjectNode)OperatorEngine.Apply(accumulatedCascadeNode, currentNode), filePackageHeader);
        }

        return (currentNode, filePackageHeader);
    }

    /// <summary>
    /// Scans the leading comment lines of a configuration file for a Hydra <c># @package &lt;target&gt;</c> or <c>// @package &lt;target&gt;</c> header.
    /// </summary>
    private static string? ExtractFilePackageHeader(string filePath)
    {
        try
        {
            foreach (var rawLine in File.ReadLines(filePath).Take(10))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                {
                    string commentBody = line.TrimStart('#', '/').Trim();
                    if (commentBody.StartsWith("@package ", StringComparison.OrdinalIgnoreCase))
                    {
                        string pkg = commentBody["@package ".Length..].Trim();
                        if (!string.IsNullOrEmpty(pkg))
                        {
                            return pkg;
                        }
                    }
                }
                else
                {
                    // Stop scanning once actual content lines begin
                    break;
                }
            }
        }
        catch
        {
            // Ignore I/O issues during header probing
        }

        return null;
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
    /// Searches for a configuration file across all registered search paths in priority order.
    /// </summary>
    private string? FindConfigFileAcrossSearchPaths(string filename)
    {
        foreach (var sp in _searchPaths)
        {
            var found = FindConfigFile(sp, filename);
            if (found != null) return found;
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
