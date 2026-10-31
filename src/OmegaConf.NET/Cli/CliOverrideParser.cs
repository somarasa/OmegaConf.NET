using System.Globalization;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;

namespace OmegaConf.NET.Cli;

/// <summary>
/// Parses command-line arguments (<c>args[]</c>) into two categories:
/// 1. Pre-composition Config Group selections (<c>db=production/postgres</c> or <c>db=postgres</c>) and group exclusions (<c>~db</c>).
/// 2. Post-composition deep property overrides and mutation operators (<c>db.port=5433</c>, <c>workers+=4</c>, <c>tags+=[gpu]</c>).
/// </summary>
public static class CliOverrideParser
{
    /// <summary>
    /// Scans CLI arguments before tree composition to extract group path selections (<c>group=path</c>)
    /// and group exclusions (<c>~group</c>), placing remaining value overrides into <paramref name="remainingValueOverrides"/>.
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

            // 1. Check Hydra tilde syntax (~group) to drop a config group from defaults
            if (trimmed.StartsWith('~'))
            {
                droppedGroups.Add(trimmed[1..].Trim());
                continue;
            }

            // 2. Parse the operator to distinguish plain '=' assignments from mutation operators (+=, *=, !=, etc.)
            var (path, op, val) = OperatorParser.ParseAssignment(trimmed);
            if (op == ConfigOperator.Assign && !path.Contains('.'))
            {
                // Explicit subfolder path selection (e.g. db=staging/postgres)
                if (val.Contains('/') || val.Contains('\\'))
                {
                    selectedPaths[path] = val;
                    continue;
                }

                // If rootDir contains a subdirectory matching 'path' (e.g. configs/db/), treat as a config group selection
                if (!string.IsNullOrEmpty(rootDir) && Directory.Exists(Path.Combine(rootDir, path)))
                {
                    selectedPaths[path] = val;
                    continue;
                }

                // Otherwise record as a candidate group selection AND keep in remainingValueOverrides
                // in case it is a top-level scalar property (e.g. app_name=MyService)
                selectedPaths[path] = val;
            }

            remainingValueOverrides.Add(trimmed);
        }
    }

    /// <summary>
    /// Applies scalar, list, and deep dot-path property overrides (with mutation operators) to the composed configuration tree.
    /// </summary>
    public static void ApplyValueOverrides(ConfigObjectNode root, IEnumerable<string> overrideArgs)
    {
        foreach (var arg in overrideArgs)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;

            var (path, op, rawVal) = OperatorParser.ParseAssignment(arg);
            var overrideNode = CreateNodeFromCliValue(rawVal, op);
            SetDeepProperty(root, path, overrideNode);
        }
    }

    /// <summary>
    /// Navigates (or creates) intermediate <see cref="ConfigObjectNode"/> containers along <paramref name="path"/>
    /// and merges <paramref name="overrideNode"/> onto the target leaf property using <see cref="OperatorEngine"/>.
    /// </summary>
    private static void SetDeepProperty(ConfigObjectNode root, string path, ConfigNode overrideNode)
    {
        string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        ConfigObjectNode current = root;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            string part = parts[i];
            if (!current.TryGetChild(part, out var next) || next is not ConfigObjectNode objChild)
            {
                objChild = new ConfigObjectNode();
                current.SetChild(part, objChild);
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
            current.SetChild(lastPart, OperatorEngine.Apply(existing, overrideNode));
        }
        else
        {
            current.SetChild(lastPart, overrideNode);
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
