using System.Globalization;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;

namespace OmegaConf.NET.Cli;

public static class CliOverrideParser
{
    /// <summary>
    /// Parses CLI args to extract group choices (~group or group=path) before resolving the tree.
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

            // Check ~group (drop group)
            if (trimmed.StartsWith('~'))
            {
                droppedGroups.Add(trimmed[1..].Trim());
                continue;
            }

            // Parse operator to see if this is a plain '=' assignment
            var (path, op, val) = OperatorParser.ParseAssignment(trimmed);
            if (op == ConfigOperator.Assign && !path.Contains('.'))
            {
                // Explicit subfolder path selection: e.g. db=staging/postgres
                if (val.Contains('/') || val.Contains('\\'))
                {
                    selectedPaths[path] = val;
                    continue;
                }

                // If rootDir is provided and a matching config group folder exists, treat as a group selection
                if (!string.IsNullOrEmpty(rootDir) && Directory.Exists(Path.Combine(rootDir, path)))
                {
                    selectedPaths[path] = val;
                    continue;
                }

                // Otherwise record as candidate group selection AND keep as value override if not consumed as group
                selectedPaths[path] = val;
            }

            remainingValueOverrides.Add(trimmed);
        }
    }

    /// <summary>
    /// Applies remaining scalar and deep property overrides to the resolved configuration object.
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
            // Do not overwrite an already-composed config group object with a plain scalar CLI group name
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

        // Check if raw is list: [a, b, c]
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
