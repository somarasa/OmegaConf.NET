using System.Globalization;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;

namespace OmegaConf.NET.Cli;

/// <summary>
/// Parses command-line arguments (<c>args[]</c>) into two categories:
/// 1. Pre-composition Config Group selections (<c>db=production/postgres</c>, <c>db@primary=postgres</c>, <c>+experiment@_global_=exp1</c>) and group exclusions (<c>~db</c>).
/// 2. Post-composition deep property overrides, key deletions (<c>~db.password</c>), struct-mode additions (<c>+new_key=1</c>, <c>++upsert_key=2</c>), and mutation operators (<c>db.port=5433</c>, <c>workers+=4</c>).
/// </summary>
public static class CliOverrideParser
{
    /// <summary>
    /// Scans CLI arguments before tree composition to extract group path selections (<c>group=path</c>, <c>group@pkg=path</c>)
    /// and group exclusions (<c>~group</c>), placing remaining value overrides and deep deletions into <paramref name="remainingValueOverrides"/>.
    /// </summary>
    public static void ExtractGroupSelections(
        IEnumerable<string> args,
        IDictionary<string, string> selectedPaths,
        ISet<string> droppedGroups,
        List<string> remainingValueOverrides,
        string? rootDir = null)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            string trimmed = arg.Trim();

            // 1. Check Hydra tilde syntax (~group or ~deep.key.path)
            if (trimmed.StartsWith('~'))
            {
                string target = trimmed[1..].Trim();
                if (!target.Contains('.'))
                {
                    droppedGroups.Add(target);
                }
                // Also forward to remainingValueOverrides so deep properties or top-level scalar properties are deleted from the AST
                remainingValueOverrides.Add(trimmed);
                continue;
            }

            // 2. Parse the operator to distinguish plain '=' assignments from mutation operators (+=, *=, !=, etc.)
            var (rawPath, op, val) = OperatorParser.ParseAssignment(trimmed);
            string cleanPath = rawPath.TrimStart('+');
            int atIdx = cleanPath.IndexOf('@');
            string groupCandidate = atIdx > 0 ? cleanPath[..atIdx] : cleanPath;

            if (op == ConfigOperator.Assign && !groupCandidate.Contains('.'))
            {
                // Explicit @package redirection (e.g. db@primary_db=postgres or +experiment@_global_=fast)
                if (atIdx > 0)
                {
                    selectedPaths[cleanPath] = val;
                    continue;
                }

                // Explicit subfolder path selection (e.g. db=staging/postgres)
                if (val.Contains('/') || val.Contains('\\'))
                {
                    selectedPaths[cleanPath] = val;
                    continue;
                }

                // If rootDir contains a subdirectory matching 'groupCandidate' (e.g. configs/db/), treat as a config group selection
                if (!string.IsNullOrEmpty(rootDir) && Directory.Exists(Path.Combine(rootDir, groupCandidate)))
                {
                    selectedPaths[cleanPath] = val;
                    continue;
                }

                // Otherwise record as a candidate group selection (if not prefixed with +/++) AND keep in remainingValueOverrides
                // in case it is a top-level scalar property (e.g. app_name=MyService)
                if (!rawPath.StartsWith('+'))
                {
                    selectedPaths[cleanPath] = val;
                }
            }

            remainingValueOverrides.Add(trimmed);
        }
    }

    /// <summary>
    /// Applies scalar, list, and deep dot-path property overrides, key deletions (<c>~key.path</c>),
    /// struct-mode modifiers (<c>+key</c>, <c>++key</c>), and mutation operators to the composed configuration tree.
    /// </summary>
    /// <param name="root">The composed configuration root object.</param>
    /// <param name="overrideArgs">The CLI value override arguments.</param>
    /// <param name="structMode">Whether strict Struct Mode is active (forbidding creation of undeclared keys without <c>+</c> or <c>++</c>).</param>
    /// <param name="droppedGroups">Optional set of config groups that were already dropped during composition.</param>
    public static void ApplyValueOverrides(
        ConfigObjectNode root,
        IEnumerable<string> overrideArgs,
        bool structMode = false,
        ISet<string>? droppedGroups = null)
    {
        bool effectiveStructMode = structMode || root.IsStruct;

        foreach (var arg in overrideArgs)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            string trimmed = arg.Trim();

            // Handle key/subtree deletion (~key or ~db.password)
            if (trimmed.StartsWith('~'))
            {
                string pathToDelete = trimmed[1..].Trim();
                bool wasDroppedGroup = droppedGroups != null && droppedGroups.Contains(pathToDelete);
                bool removed = RemoveDeepProperty(root, pathToDelete);

                if (!removed && !wasDroppedGroup && effectiveStructMode)
                {
                    throw new ConfigStructException(
                        pathToDelete,
                        $"Cannot delete '~{pathToDelete}': key does not exist in struct-locked configuration.",
                        new SourceInfo("(cli)", 0, 0));
                }
                continue;
            }

            var (rawPath, op, rawVal) = OperatorParser.ParseAssignment(trimmed);
            bool isForceUpsert = rawPath.StartsWith("++", StringComparison.Ordinal);
            bool isStrictAppend = !isForceUpsert && rawPath.StartsWith('+');
            string cleanPath = rawPath.TrimStart('+');

            var overrideNode = CreateNodeFromCliValue(rawVal, op);
            SetDeepProperty(root, cleanPath, overrideNode, isStrictAppend, isForceUpsert, effectiveStructMode);
        }
    }

    /// <summary>
    /// Removes a nested property or subtree at <paramref name="path"/> from <paramref name="root"/>.
    /// Returns <c>true</c> if the target key existed and was removed.
    /// </summary>
    private static bool RemoveDeepProperty(ConfigObjectNode root, string path)
    {
        string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        ConfigObjectNode current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetChild(parts[i], out var next) || next is not ConfigObjectNode nextObj)
            {
                return false;
            }
            current = nextObj;
        }

        return current.RemoveChild(parts[^1]);
    }

    /// <summary>
    /// Navigates (or creates) intermediate <see cref="ConfigObjectNode"/> containers along <paramref name="path"/>
    /// and merges <paramref name="overrideNode"/> onto the target leaf property using <see cref="OperatorEngine"/>,
    /// enforcing Hydra <c>+key</c>, <c>++key</c>, and Struct Mode rules.
    /// </summary>
    private static void SetDeepProperty(
        ConfigObjectNode root,
        string path,
        ConfigNode overrideNode,
        bool isStrictAppend,
        bool isForceUpsert,
        bool structMode)
    {
        string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        ConfigObjectNode current = root;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            string part = parts[i];
            if (!current.TryGetChild(part, out var next) || next is not ConfigObjectNode objChild)
            {
                if ((structMode || current.IsStruct) && !isStrictAppend && !isForceUpsert)
                {
                    throw new ConfigStructException(
                        path,
                        $"Cannot set '{path}': parent container '{part}' does not exist in struct-locked configuration. Use '+{path}=...' or '++{path}=...' to add new keys.",
                        overrideNode.Source);
                }

                objChild = new ConfigObjectNode();
                if (structMode || current.IsStruct)
                {
                    objChild.SetStruct(true);
                }
                current.SetChild(part, objChild, allowNewKeyInStruct: true);
            }
            current = objChild;
        }

        string lastPart = parts[^1];
        if (current.TryGetChild(lastPart, out var existing))
        {
            // Guard: Do not overwrite an already-composed config group object with a plain scalar CLI group name
            if (parts.Length == 1 && existing is ConfigObjectNode && overrideNode is ConfigValueNode { Operator: ConfigOperator.Assign })
            {
                return;
            }

            if (isStrictAppend)
            {
                throw new ConfigStructException(
                    path,
                    $"Cannot append '+{path}': key already exists in configuration. Use '{path}=...' to override or '++{path}=...' to upsert.",
                    overrideNode.Source);
            }

            current.SetChild(lastPart, OperatorEngine.Apply(existing, overrideNode), allowNewKeyInStruct: true);
        }
        else
        {
            if ((structMode || current.IsStruct) && !isStrictAppend && !isForceUpsert)
            {
                throw new ConfigStructException(
                    path,
                    $"Cannot override '{path}': key does not exist in struct-locked configuration. Use '+{path}=...' to append a new key or '++{path}=...' to upsert.",
                    overrideNode.Source);
            }

            current.SetChild(lastPart, overrideNode, allowNewKeyInStruct: true);
        }
    }

    /// <summary>
    /// Parses a raw CLI value string into a typed <see cref="ConfigNode"/> (supporting <c>???</c>, booleans,
    /// 64-bit integers, floating-point numbers, bracketed lists <c>[a,b,c]</c>, and strings).
    /// </summary>
    private static ConfigNode CreateNodeFromCliValue(string raw, ConfigOperator op)
    {
        var source = new SourceInfo("(cli)", 0, 0);

        if (raw == ConfigValueNode.MissingMarker)
        {
            return new ConfigValueNode(ConfigValueNode.MissingMarker, op, source);
        }

        if (bool.TryParse(raw, out bool b))
        {
            return new ConfigValueNode(b, op, source);
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
        {
            return new ConfigValueNode(l, op, source);
        }

        if (raw.Contains('.') && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return new ConfigValueNode(d, op, source);
        }

        // Parse bracketed CLI list literal: e.g. tags=[gpu,fast,cluster]
        if (raw.StartsWith('[') && raw.EndsWith(']'))
        {
            string inner = raw[1..^1];
            var items = inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(item => CreateNodeFromCliValue(item, ConfigOperator.Assign));
            return new ConfigListNode(items, op, source);
        }

        return new ConfigValueNode(raw, op, source);
    }
}
