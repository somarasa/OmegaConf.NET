using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Composition;

/// <summary>
/// Represents a single entry in a Hydra-style <c>defaults:</c> configuration list.
/// Can represent either the <c>_self_</c> precedence marker or a config group path selection (optionally marked <c>optional</c>).
/// </summary>
public sealed class DefaultsEntry
{
    /// <summary>
    /// Special Hydra keyword (<c>"_self_"</c>) indicating where the current file's own properties are merged relative to its defaults.
    /// </summary>
    public const string SelfMarker = "_self_";

    /// <summary>
    /// Gets the configuration group folder name (e.g. <c>"db"</c>).
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Gets the relative path within the group folder (e.g. <c>"staging/postgres"</c>).
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets a value indicating whether this entry is the <c>_self_</c> marker.
    /// </summary>
    public bool IsSelf { get; init; }

    /// <summary>
    /// Gets a value indicating whether missing files for this default entry should be silently ignored (<c>optional group: path</c>).
    /// </summary>
    public bool IsOptional { get; init; }

    /// <summary>
    /// Creates a <see cref="DefaultsEntry"/> representing the <c>_self_</c> marker.
    /// </summary>
    public static DefaultsEntry Self() => new() { IsSelf = true };

    /// <summary>
    /// Creates a <see cref="DefaultsEntry"/> representing a config group selection.
    /// </summary>
    public static DefaultsEntry GroupChoice(string group, string path, bool isOptional = false) =>
        new() { Group = group, Path = path, IsOptional = isOptional };

    /// <inheritdoc />
    public override string ToString() => IsSelf ? SelfMarker : $"{Group}: {Path}{(IsOptional ? " (optional)" : "")}";
}

/// <summary>
/// Parses the top-level <c>defaults:</c> sequence in an entrypoint configuration file.
/// Supports both mapping syntax (<c>- db: staging/postgres</c>, <c>- optional secrets: local</c>)
/// and shorthand string syntax (<c>- db/postgres</c>, <c>- _self_</c>).
/// </summary>
public static class DefaultsListParser
{
    /// <summary>
    /// Reserved key name (<c>"defaults"</c>) for the composition list.
    /// </summary>
    public const string DefaultsKey = "defaults";

    /// <summary>
    /// Extracts and parses all <see cref="DefaultsEntry"/> items from the <c>defaults</c> child of <paramref name="root"/>.
    /// </summary>
    public static List<DefaultsEntry> Parse(ConfigObjectNode root)
    {
        var result = new List<DefaultsEntry>();

        if (!root.TryGetChild(DefaultsKey, out var defaultsNode) || defaultsNode is not ConfigListNode list)
        {
            return result;
        }

        foreach (var item in list.Items)
        {
            // Case 1: Scalar string entry (e.g. "_self_", "db/postgres", or "optional db/postgres")
            if (item is ConfigValueNode val && val.Value is string strVal)
            {
                string trimmed = strVal.Trim();
                if (string.Equals(trimmed, DefaultsEntry.SelfMarker, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(DefaultsEntry.Self());
                }
                else
                {
                    bool isOptional = false;
                    if (trimmed.StartsWith("optional ", StringComparison.OrdinalIgnoreCase))
                    {
                        isOptional = true;
                        trimmed = trimmed["optional ".Length..].Trim();
                    }

                    int slashIdx = trimmed.IndexOf('/');
                    if (slashIdx > 0)
                    {
                        string group = trimmed[..slashIdx];
                        string path = trimmed[(slashIdx + 1)..];
                        result.Add(DefaultsEntry.GroupChoice(group, path, isOptional));
                    }
                    else
                    {
                        result.Add(DefaultsEntry.GroupChoice(trimmed, trimmed, isOptional));
                    }
                }
            }
            // Case 2: Mapping entry (e.g. "- db: staging/postgres" or "- optional db: postgres")
            else if (item is ConfigObjectNode obj)
            {
                foreach (var (k, v) in obj.Children)
                {
                    string group = k;
                    bool isOptional = false;

                    if (group.StartsWith("optional ", StringComparison.OrdinalIgnoreCase))
                    {
                        isOptional = true;
                        group = group["optional ".Length..].Trim();
                    }

                    string path = v is ConfigValueNode vn ? vn.Value?.ToString() ?? "" : "";
                    result.Add(DefaultsEntry.GroupChoice(group, path, isOptional));
                }
            }
        }

        return result;
    }
}
