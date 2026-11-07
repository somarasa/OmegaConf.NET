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
    /// Special Hydra package keyword (<c>"_group_"</c>) that expands to the group's own name (e.g. <c>preprocess@_group_.suffix</c> -> <c>preprocess.suffix</c>).
    /// </summary>
    public const string GroupPackage = "_group_";

    /// <summary>
    /// Special Hydra package keyword (<c>"_here_"</c>) indicating that the loaded config should be merged into the enclosing file's own package.
    /// </summary>
    public const string HerePackage = "_here_";

    /// <summary>
    /// Gets the configuration group folder name (e.g. <c>"db"</c>).
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Gets a value indicating whether the group path in the defaults list began with a leading <c>'/'</c> (e.g. <c>- /db_schema/base_mysql@_here_</c>),
    /// forcing resolution from the root configuration directory rather than the enclosing group folder.
    /// </summary>
    public bool IsRooted { get; init; }

    /// <summary>
    /// Gets a value indicating whether this entry was written as a bare single-segment name without a slash or mapping colon (e.g. <c>- base_mysql</c>).
    /// </summary>
    public bool IsBareSingle { get; init; }

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
    /// Gets the raw unexpanded <c>@package</c> directive as written in the defaults list (e.g. <c>"_group_"</c> or <c>"_group_.suffix"</c>),
    /// if different from the expanded <see cref="Package"/>.
    /// </summary>
    public string? RawPackage { get; init; }

    /// <summary>
    /// Gets the unique selection key for CLI/builder overrides (<c>"group@package"</c> if <see cref="Package"/> is set, otherwise <see cref="Group"/>).
    /// </summary>
    public string SelectionKey => string.IsNullOrEmpty(Package) ? (Group ?? "") : $"{Group}@{Package}";

    /// <summary>
    /// Gets a value indicating whether the given CLI override key (e.g. <c>"preprocess@_group_"</c>, <c>"preprocess@_group_.suffix"</c>,
    /// <c>"preprocess@preprocess"</c>, or <c>"preprocess"</c>) matches this default entry.
    /// </summary>
    public bool MatchesOverrideKey(string candidateKey)
    {
        if (string.IsNullOrWhiteSpace(candidateKey)) return false;
        string cleanCandidate = candidateKey.Trim().TrimStart('+').TrimStart('/');

        if (string.Equals(cleanCandidate, SelectionKey, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(Package) && string.Equals(cleanCandidate, Group, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(RawPackage))
        {
            string rawSelectionKey = string.IsNullOrEmpty(Group) ? $"@{RawPackage}" : $"{Group}@{RawPackage}";
            if (string.Equals(cleanCandidate, rawSelectionKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(RawPackage, GroupPackage, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cleanCandidate, Group, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Also check if candidateKey itself uses @_group_ or @_group_.suffix for this entry's Group
        DefaultsListParser.SplitPackage(cleanCandidate, out string candGroup, out string? candPkg);
        candGroup = candGroup.TrimStart('/');
        if (!string.IsNullOrEmpty(candPkg) && string.Equals(candGroup, Group, StringComparison.OrdinalIgnoreCase))
        {
            string? expandedCandPkg = ExpandGroupPackageKeyword(Group ?? "", candPkg, IsBareSingle);
            if (string.Equals(expandedCandPkg, Package ?? (Group ?? "").Replace('/', '.'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets a value indicating whether this entry is the <c>_self_</c> marker.
    /// </summary>
    public bool IsSelf { get; init; }

    /// <summary>
    /// Gets a value indicating whether missing files for this default entry should be silently ignored (<c>optional group: path</c>).
    /// </summary>
    public bool IsOptional { get; init; }

    /// <summary>
    /// Gets a value indicating whether this entry explicitly overrides an earlier default entry (<c>override group: path</c>).
    /// </summary>
    public bool IsOverride { get; init; }

    /// <summary>
    /// Gets a value indicating whether this entry is marked as mandatory missing (<c>group: ???</c>), requiring a CLI or builder override.
    /// </summary>
    public bool IsMissing { get; init; }

    /// <summary>
    /// Gets a value indicating whether this entry is explicitly disabled (<c>group: null</c>).
    /// </summary>
    public bool IsDisabled { get; init; }

    /// <summary>
    /// Gets the list of sub-configuration paths when a single group entry selects multiple configs (e.g. <c>- callbacks: [checkpoint, early_stopping]</c>).
    /// </summary>
    public IReadOnlyList<string>? MultiPaths { get; init; }

    /// <summary>
    /// Creates a <see cref="DefaultsEntry"/> representing the <c>_self_</c> marker.
    /// </summary>
    public static DefaultsEntry Self() => new() { IsSelf = true };

    /// <summary>
    /// Creates a <see cref="DefaultsEntry"/> representing a config group selection.
    /// </summary>
    public static DefaultsEntry GroupChoice(
        string group,
        string? path,
        bool isOptional = false,
        string? package = null,
        bool isOverride = false,
        bool isMissing = false,
        bool isDisabled = false,
        IReadOnlyList<string>? multiPaths = null,
        bool isRooted = false,
        bool isBareSingle = false)
    {
        bool rooted = isRooted || group.Trim().StartsWith('/');
        string normalizedGroup = group.Trim().TrimStart('/');
        string? normalizedPkg = ExpandGroupPackageKeyword(normalizedGroup, package, isBareSingle);
        return new()
        {
            Group = normalizedGroup,
            Path = path?.Trim().TrimStart('/'),
            IsOptional = isOptional,
            Package = normalizedPkg,
            RawPackage = package?.Trim(),
            IsOverride = isOverride,
            IsMissing = isMissing,
            IsDisabled = isDisabled,
            MultiPaths = multiPaths,
            IsRooted = rooted,
            IsBareSingle = isBareSingle
        };
    }

    /// <summary>
    /// Expands <c>_group_</c> and <c>_group_.suffix</c> package placeholders against <paramref name="group"/>,
    /// converting group path slashes (<c>'/'</c>) into dot-separated package segments (<c>'.'</c>) and treating
    /// bare root config files (<paramref name="isBareSingle"/>) as having an empty group path.
    /// </summary>
    public static string? ExpandGroupPackageKeyword(string group, string? package, bool isBareSingle = false)
    {
        if (string.IsNullOrEmpty(package)) return null;
        string effectiveGroup = isBareSingle ? "" : group.Replace('/', '.').Trim('.');

        if (string.Equals(package, GroupPackage, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(effectiveGroup) ? GlobalPackage : effectiveGroup;
        }
        if (package.StartsWith(GroupPackage + ".", StringComparison.OrdinalIgnoreCase))
        {
            string suffix = package[(GroupPackage.Length + 1)..];
            return string.IsNullOrEmpty(effectiveGroup) ? suffix : $"{effectiveGroup}.{suffix}";
        }
        return package;
    }

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
                    if (result.Any(d => d.IsSelf))
                    {
                        throw new ConfigSyntaxException("Duplicate '_self_' defined in defaults list.", item.Source);
                    }
                    result.Add(DefaultsEntry.Self());
                }
                else
                {
                    ExtractDefaultKeywords(ref trimmed, out bool isOptional, out bool isOverride);
                    bool isRooted = trimmed.StartsWith('/');
                    trimmed = trimmed.TrimStart('/');

                    SplitPackage(trimmed, out string withoutPkg, out string? pkg);

                    int slashIdx = withoutPkg.LastIndexOf('/');
                    if (slashIdx > 0)
                    {
                        string group = withoutPkg[..slashIdx];
                        string path = withoutPkg[(slashIdx + 1)..];
                        SplitPackage(group, out group, out string? groupPkg);
                        result.Add(DefaultsEntry.GroupChoice(group, path, isOptional, pkg ?? groupPkg, isOverride, isRooted: isRooted));
                    }
                    else
                    {
                        result.Add(DefaultsEntry.GroupChoice(withoutPkg, withoutPkg, isOptional, pkg, isOverride, isRooted: isRooted, isBareSingle: true));
                    }
                }
            }
            // Case 2: Mapping entry (e.g. "- db: staging/postgres", "- db@primary_db: postgres", "- override db: mysql", or "- optional db: postgres")
            else if (item is ConfigObjectNode obj)
            {
                if (obj.Children.Count > 1)
                {
                    throw new ConfigSyntaxException($"Too many keys in defaults list item: {{{string.Join(", ", obj.Children.Keys)}}}.", obj.Source);
                }

                foreach (var (k, v) in obj.Children)
                {
                    string rawGroup = k;
                    ExtractDefaultKeywords(ref rawGroup, out bool isOptional, out bool isOverride);

                    SplitPackage(rawGroup, out string group, out string? keyPkg);
                    if (string.IsNullOrWhiteSpace(group))
                    {
                        throw new ConfigSyntaxException("Missing group name in defaults list.", obj.Source);
                    }

                    if (v is ConfigListNode listVal)
                    {
                        var multi = listVal.Items
                            .OfType<ConfigValueNode>()
                            .Select(i => i.Value?.ToString()?.Trim().TrimStart('/') ?? "")
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                        result.Add(DefaultsEntry.GroupChoice(group, null, isOptional, keyPkg, isOverride, multiPaths: multi));
                        continue;
                    }

                    if (v is ConfigValueNode vn)
                    {
                        if (vn.IsMissingMarker)
                        {
                            result.Add(DefaultsEntry.GroupChoice(group, "???", isOptional, keyPkg, isOverride, isMissing: true));
                            continue;
                        }
                        if (vn.Value == null)
                        {
                            result.Add(DefaultsEntry.GroupChoice(group, null, isOptional, keyPkg, isOverride, isDisabled: true));
                            continue;
                        }
                    }

                    string rawPath = v is ConfigValueNode scalarVn ? scalarVn.Value?.ToString() ?? "" : "";
                    SplitPackage(rawPath, out string path, out string? valPkg);

                    result.Add(DefaultsEntry.GroupChoice(group, path, isOptional, keyPkg ?? valPkg, isOverride));
                }
            }
        }

        return result;
    }

    private static void ExtractDefaultKeywords(ref string token, out bool isOptional, out bool isOverride)
    {
        isOptional = false;
        isOverride = false;

        string[] words = token.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1) return;

        int idx = 0;
        while (idx < words.Length - 1)
        {
            string kw = words[idx];
            if (string.Equals(kw, "optional", StringComparison.OrdinalIgnoreCase))
            {
                isOptional = true;
                idx++;
            }
            else if (string.Equals(kw, "override", StringComparison.OrdinalIgnoreCase))
            {
                isOverride = true;
                idx++;
            }
            else
            {
                throw new ConfigSyntaxException($"Unsupported keyword '{kw}' in defaults list entry '{token}'.");
            }
        }

        if (isOptional && isOverride)
        {
            throw new ConfigSyntaxException($"'optional' and 'override' keywords cannot be combined in defaults list entry '{token}'.");
        }

        token = string.Join(' ', words[idx..]);
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
