using System.Text;
using OmegaConf.NET.Ast;

namespace OmegaConf.NET.Diff;

/// <summary>
/// Categorizes the type of difference detected between two configuration nodes.
/// </summary>
public enum ConfigDiffChangeType
{
    /// <summary> + Key was added in the target configuration. </summary>
    Added,

    /// <summary> - Key was removed in the target configuration. </summary>
    Removed,

    /// <summary> ~ Value or type of the key was modified between base and target. </summary>
    Modified
}

/// <summary>
/// Represents a single property difference between two configuration trees.
/// </summary>
/// <param name="Path">Dot-separated configuration key path (e.g. <c>"db.max_connections"</c>).</param>
/// <param name="ChangeType">Whether the key was Added (<c>+</c>), Removed (<c>-</c>), or Modified (<c>~</c>).</param>
/// <param name="OldValue">Previous value in the base tree, or <c>null</c> if added.</param>
/// <param name="NewValue">Updated value in the target tree, or <c>null</c> if removed.</param>
public sealed record ConfigDiffEntry(
    string Path,
    ConfigDiffChangeType ChangeType,
    object? OldValue,
    object? NewValue)
{
    /// <inheritdoc />
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

/// <summary>
/// Holds the complete list of hierarchical differences between two <see cref="ConfigNode"/> trees.
/// </summary>
public sealed class ConfigDiff
{
    /// <summary>
    /// Gets the ordered list of detected configuration changes.
    /// </summary>
    public IReadOnlyList<ConfigDiffEntry> Entries { get; }

    /// <summary>
    /// Gets a value indicating whether any differences were found between the two configurations.
    /// </summary>
    public bool HasChanges => Entries.Count > 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigDiff"/> class.
    /// </summary>
    public ConfigDiff(IEnumerable<ConfigDiffEntry> entries)
    {
        Entries = entries.ToList();
    }

    /// <summary>
    /// Formats the diff report as a human-readable multi-line string (<c>+ key: val</c>, <c>- key: val</c>, <c>~ key: old -> new</c>).
    /// </summary>
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

/// <summary>
/// Hierarchically compares two <see cref="ConfigNode"/> trees and produces a structured <see cref="ConfigDiff"/> report.
/// </summary>
public static class ConfigDiffer
{
    /// <summary>
    /// Compares <paramref name="baseNode"/> against <paramref name="targetNode"/> and returns all additions, removals, and modifications.
    /// </summary>
    public static ConfigDiff Compare(ConfigNode? baseNode, ConfigNode? targetNode)
    {
        var entries = new List<ConfigDiffEntry>();
        CompareNodes(baseNode, targetNode, "", entries);
        return new ConfigDiff(entries);
    }

    private static void CompareNodes(ConfigNode? a, ConfigNode? b, string currentPath, List<ConfigDiffEntry> entries)
    {
        // Case 1: One side is null
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

        // Case 2: Both are object mappings — recursively compare union of all keys in alphabetical order
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

        // Case 3: Both are lists — compare serialized sequence representations
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

        // Case 4: Scalar value comparison or node type change
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

    /// <summary>
    /// Extension method to compare <paramref name="baseNode"/> with <paramref name="targetNode"/>.
    /// </summary>
    public static ConfigDiff Diff(this ConfigNode baseNode, ConfigNode targetNode)
    {
        return Compare(baseNode, targetNode);
    }
}
