using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Composition;

/// <summary>
/// Represents a single entry in a Hydra-style <c>defaults:</c> configuration list.
/// Can represent either the <c>_self_</c> precedence marker or a config group path selection
/// (optionally marked <c>optional</c> and/or redirected to a custom target <see cref="Package"/> via <c>@package</c> or <c>@_global_</c>).
/// </summary>
public sealed class DefaultsEntry
{
    /// <summary>
    /// Special Hydra keyword (<c>"_self_"</c>) indicating where the current file's own properties are merged relative to its defaults.
    /// </summary>
    public const string SelfMarker = "_self_";

    /// <summary>
    /// Special Hydra package keyword (<c>"_global_"</c>) indicating that the config group's properties should be merged directly into the root node.
    /// </summary>
    public const string GlobalPackage = "_global_";

    /// <summary>
    /// Gets the configuration group folder name (e.g. <c>"db"</c>).
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Gets the relative path within the group folder (e.g. <c>"staging/postgres"</c>).
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets the optional target package namespace where the loaded group should be mounted (e.g. <c>"primary_db"</c>, <c>"storage.db"</c>, or <c>"_global_"</c>).
    /// When <c>null</c>, defaults to <see cref="Group"/> (unless overridden by a <c># @package</c> file header).
    /// </summary>
    public string? Package { get; init; }

    /// <summary>
    /// Gets the unique selection key for CLI/builder overrides (<c>"group@package"</c> if <see cref="Package"/> is set, otherwise <see cref="Group"/>).
    /// </summary>
    public string SelectionKey => string.IsNullOrEmpty(Package) ? (Group ?? "") : $"{Group}@{Package}";

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
    public static DefaultsEntry GroupChoice(string group, string path, bool isOptional = false, string? package = null) =>
        new() { Group = group, Path = path, IsOptional = isOptional, Package = package };

    /// <inheritdoc />
    public override string ToString()
    {
        if (IsSelf) return SelfMarker;
        string pkgSuffix = string.IsNullOrEmpty(Package) ? "" : $"@{Package}";
        return $"{Group}{pkgSuffix}: {Path}{(IsOptional ? " (optional)" : "")}";
    }
}

/// <summary>
/// Parses the top-level <c>defaults:</c> sequence in an entrypoint configuration file.
/// Supports mapping syntax (<c>- db: staging/postgres</c>, <c>- db@primary_db: postgres</c>, <c>- experiment@_global_: exp1</c>, <c>- optional secrets: local</c>)
/// and shorthand string syntax (<c>- db/postgres</c>, <c>- db/postgres@primary_db</c>, <c>- _self_</c>).
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
            // Case 1: Scalar string entry (e.g. "_self_", "db/postgres", "db/postgres@primary_db", or "optional db/postgres")
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

                    SplitPackage(trimmed, out string withoutPkg, out string? pkg);

                    int slashIdx = withoutPkg.IndexOf('/');
                    if (slashIdx > 0)
                    {
                        string group = withoutPkg[..slashIdx];
                        string path = withoutPkg[(slashIdx + 1)..];
                        SplitPackage(group, out group, out string? groupPkg);
                        result.Add(DefaultsEntry.GroupChoice(group, path, isOptional, pkg ?? groupPkg));
                    }
                    else
                    {
                        result.Add(DefaultsEntry.GroupChoice(withoutPkg, withoutPkg, isOptional, pkg));
                    }
                }
            }
            // Case 2: Mapping entry (e.g. "- db: staging/postgres", "- db@primary_db: postgres", or "- optional db: postgres")
            else if (item is ConfigObjectNode obj)
            {
                foreach (var (k, v) in obj.Children)
                {
                    string rawGroup = k;
                    bool isOptional = false;

                    if (rawGroup.StartsWith("optional ", StringComparison.OrdinalIgnoreCase))
                    {
                        isOptional = true;
                        rawGroup = rawGroup["optional ".Length..].Trim();
                    }

                    SplitPackage(rawGroup, out string group, out string? keyPkg);

                    string rawPath = v is ConfigValueNode vn ? vn.Value?.ToString() ?? "" : "";
                    SplitPackage(rawPath, out string path, out string? valPkg);

                    result.Add(DefaultsEntry.GroupChoice(group, path, isOptional, keyPkg ?? valPkg));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Splits an optional <c>@package</c> suffix from a group or path token (e.g. <c>"db@primary_db"</c> -> <c>("db", "primary_db")</c>).
    /// </summary>
    public static void SplitPackage(string raw, out string clean, out string? package)
    {
        int atIdx = raw.IndexOf('@');
        if (atIdx > 0)
        {
            clean = raw[..atIdx].Trim();
            package = raw[(atIdx + 1)..].Trim();
            if (string.IsNullOrEmpty(package)) package = null;
        }
        else
        {
            clean = raw.Trim();
            package = null;
        }
    }
}
