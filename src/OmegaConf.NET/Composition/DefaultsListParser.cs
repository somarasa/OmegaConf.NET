using OmegaConf.NET.Ast;

namespace OmegaConf.NET.Composition;

public sealed class DefaultsEntry
{
    public const string SelfMarker = "_self_";

    public string? Group { get; init; }
    public string? Path { get; init; }
    public bool IsSelf { get; init; }
    public bool IsOptional { get; init; }

    public static DefaultsEntry Self() => new() { IsSelf = true };

    public static DefaultsEntry GroupChoice(string group, string path, bool isOptional = false) =>
        new() { Group = group, Path = path, IsOptional = isOptional };

    public override string ToString() => IsSelf ? SelfMarker : $"{Group}: {Path}{(IsOptional ? " (optional)" : "")}";
}

public static class DefaultsListParser
{
    public const string DefaultsKey = "defaults";

    public static List<DefaultsEntry> Parse(ConfigObjectNode root)
    {
        var result = new List<DefaultsEntry>();

        if (!root.TryGetChild(DefaultsKey, out var defaultsNode) || defaultsNode is not ConfigListNode list)
        {
            return result;
        }

        foreach (var item in list.Items)
        {
            if (item is ConfigValueNode val && val.Value is string strVal)
            {
                string trimmed = strVal.Trim();
                if (string.Equals(trimmed, DefaultsEntry.SelfMarker, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(DefaultsEntry.Self());
                }
                else
                {
                    // Plain string like "db/postgres" or "optional db/postgres"
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
