using System.Text;
using OmegaConf.NET.Ast;

namespace OmegaConf.NET.Diff;

public enum ConfigDiffChangeType
{
    Added,
    Removed,
    Modified
}

public sealed record ConfigDiffEntry(
    string Path,
    ConfigDiffChangeType ChangeType,
    object? OldValue,
    object? NewValue)
{
    public override string ToString()
    {
        return ChangeType switch
        {
            ConfigDiffChangeType.Added => $"+ {Path}: {FormatValue(NewValue)}",
            ConfigDiffChangeType.Removed => $"- {Path}: {FormatValue(OldValue)}",
            ConfigDiffChangeType.Modified => $"~ {Path}: {FormatValue(OldValue)} -> {FormatValue(NewValue)}",
            _ => $"{Path}"
        };
    }

    private static string FormatValue(object? val)
    {
        if (val == null) return "null";
        if (val is string s) return $"\"{s}\"";
        return val.ToString() ?? "null";
    }
}

public sealed class ConfigDiff
{
    public IReadOnlyList<ConfigDiffEntry> Entries { get; }

    public bool HasChanges => Entries.Count > 0;

    public ConfigDiff(IEnumerable<ConfigDiffEntry> entries)
    {
        Entries = entries.ToList();
    }

    public override string ToString()
    {
        if (!HasChanges)
        {
            return "No configuration differences.";
        }

        var sb = new StringBuilder();
        foreach (var entry in Entries)
        {
            sb.AppendLine(entry.ToString());
        }
        return sb.ToString().TrimEnd();
    }
}

public static class ConfigDiffer
{
    /// <summary>
    /// Compares two configuration nodes and returns the hierarchical diff.
    /// </summary>
    public static ConfigDiff Compare(ConfigNode? baseNode, ConfigNode? targetNode)
    {
        var entries = new List<ConfigDiffEntry>();
        CompareNodes(baseNode, targetNode, "", entries);
        return new ConfigDiff(entries);
    }

    private static void CompareNodes(ConfigNode? a, ConfigNode? b, string currentPath, List<ConfigDiffEntry> entries)
    {
        // 1. One is null
        if (a == null && b != null)
        {
            entries.Add(new ConfigDiffEntry(currentPath, ConfigDiffChangeType.Added, null, GetNodeValue(b)));
            return;
        }

        if (a != null && b == null)
        {
            entries.Add(new ConfigDiffEntry(currentPath, ConfigDiffChangeType.Removed, GetNodeValue(a), null));
            return;
        }

        if (a == null && b == null) return;

        // 2. Both are objects
        if (a is ConfigObjectNode objA && b is ConfigObjectNode objB)
        {
            var allKeys = new HashSet<string>(objA.Children.Keys, StringComparer.OrdinalIgnoreCase);
            allKeys.UnionWith(objB.Children.Keys);

            foreach (var key in allKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                string childPath = string.IsNullOrEmpty(currentPath) ? key : $"{currentPath}.{key}";
                bool hasA = objA.TryGetChild(key, out var childA);
                bool hasB = objB.TryGetChild(key, out var childB);

                if (!hasA && hasB)
                {
                    entries.Add(new ConfigDiffEntry(childPath, ConfigDiffChangeType.Added, null, GetNodeValue(childB!)));
                }
                else if (hasA && !hasB)
                {
                    entries.Add(new ConfigDiffEntry(childPath, ConfigDiffChangeType.Removed, GetNodeValue(childA!), null));
                }
                else
                {
                    CompareNodes(childA, childB, childPath, entries);
                }
            }
            return;
        }

        // 3. Both are lists
        if (a is ConfigListNode listA && b is ConfigListNode listB)
        {
            string strA = listA.ToString();
            string strB = listB.ToString();
            if (strA != strB)
            {
                entries.Add(new ConfigDiffEntry(currentPath, ConfigDiffChangeType.Modified, strA, strB));
            }
            return;
        }

        // 4. Scalar or type change
        object? valA = GetNodeValue(a);
        object? valB = GetNodeValue(b);

        if (!Equals(valA, valB))
        {
            entries.Add(new ConfigDiffEntry(currentPath, ConfigDiffChangeType.Modified, valA, valB));
        }
    }

    private static object? GetNodeValue(ConfigNode? node)
    {
        if (node == null) return null;

        return node switch
        {
            ConfigValueNode vn => vn.IsMissingMarker ? "???" : vn.Value,
            _ => node.ToString()
        };
    }

    public static ConfigDiff Diff(this ConfigNode baseNode, ConfigNode targetNode)
    {
        return Compare(baseNode, targetNode);
    }
}
