using System.Text;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Cli;

/// <summary>
/// Expands command-line override aliases declared under <c>hydra.aliases</c> (or <c>omegaconf.aliases</c>)
/// in the primary configuration file or registered programmatically via <see cref="OmegaConfBuilder.AddAlias(string, string)"/>.
/// Modeled directly on Python Hydra's <c>hydra._internal.override_aliases.OverrideAliases</c>.
/// </summary>
public sealed class OverrideAliases
{
    /// <summary>
    /// Gets a shared empty <see cref="OverrideAliases"/> instance.
    /// </summary>
    public static OverrideAliases Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Gets the resolved mapping from individual alias names to their expansion targets.
    /// </summary>
    public IReadOnlyDictionary<string, string> Targets { get; }

    /// <summary>
    /// Gets a value indicating whether any override aliases are defined.
    /// </summary>
    public bool IsEmpty => Targets.Count == 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="OverrideAliases"/> class.
    /// </summary>
    /// <param name="targets">Validated mapping of alias names to expansion targets.</param>
    public OverrideAliases(IReadOnlyDictionary<string, string> targets)
    {
        Targets = targets;
    }

    /// <summary>
    /// Extracts and validates <see cref="OverrideAliases"/> from the primary configuration node (<c>hydra.aliases</c> or <c>omegaconf.aliases</c>)
    /// and any additional programmatic aliases supplied on the builder.
    /// </summary>
    /// <param name="primaryConfig">The parsed primary entrypoint configuration object, if available.</param>
    /// <param name="builderAliases">Optional programmatic aliases registered via <see cref="OmegaConfBuilder.AddAlias(string, string)"/>.</param>
    public static OverrideAliases FromConfig(
        ConfigObjectNode? primaryConfig,
        IDictionary<string, string>? builderAliases = null)
    {
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);

        if (primaryConfig != null)
        {
            foreach (var rootKey in new[] { "hydra", "omegaconf" })
            {
                if (!primaryConfig.TryGetChild(rootKey, out var sectionNode) || sectionNode is not ConfigObjectNode sectionObj)
                {
                    continue;
                }

                if (!sectionObj.TryGetChild("aliases", out var aliasesNode) || aliasesNode == null)
                {
                    continue;
                }

                if (aliasesNode is not ConfigObjectNode aliasesObj)
                {
                    throw new ConfigSyntaxException(
                        $"'{rootKey}.aliases' must be a mapping of alias names to target overrides, but got {aliasesNode.NodeType}.",
                        aliasesNode.Source);
                }

                foreach (var (rawNames, targetNode) in aliasesObj.Children)
                {
                    if (targetNode is not ConfigValueNode tvn ||
                        tvn.Value is not string rawTargetStr ||
                        tvn.IsMissingMarker ||
                        ContainsUnescapedInterpolation(rawTargetStr))
                    {
                        throw new ConfigSyntaxException(
                            $"Invalid target for alias '{rawNames}' in '{rootKey}.aliases': target must be a non-empty literal string without interpolations.",
                            targetNode.Source);
                    }

                    RegisterAliasEntry(targets, rawNames, rawTargetStr, targetNode.Source);
                }
            }
        }

        if (builderAliases != null)
        {
            foreach (var (rawNames, rawTarget) in builderAliases)
            {
                RegisterAliasEntry(targets, rawNames, rawTarget, SourceInfo.Unknown);
            }
        }

        return targets.Count == 0 ? Empty : new OverrideAliases(targets);
    }

    /// <summary>
    /// Validates that a non-primary (included or config group) configuration file does not declare <c>hydra.aliases</c> or <c>omegaconf.aliases</c>.
    /// </summary>
    public static void ValidateNoAliasesInIncludedConfig(ConfigObjectNode node, string filePath)
    {
        foreach (var rootKey in new[] { "hydra", "omegaconf" })
        {
            if (node.TryGetChild(rootKey, out var secNode) &&
                secNode is ConfigObjectNode secObj &&
                secObj.TryGetChild("aliases", out var aliasesNode))
            {
                throw new ConfigSyntaxException(
                    $"'{rootKey}.aliases' can only be defined in the primary configuration file, not in included config '{filePath}'.",
                    aliasesNode?.Source ?? node.Source);
            }
        }
    }

    /// <summary>
    /// Validates that command-line arguments do not attempt to modify or delete <c>hydra.aliases</c> or <c>omegaconf.aliases</c>.
    /// </summary>
    public static void ValidateCliDoesNotModifyAliases(IEnumerable<string> cliArgs)
    {
        foreach (var arg in cliArgs)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            var (_, key, _) = SplitOverride(arg.Trim());
            if (key.Equals("hydra.aliases", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("hydra.aliases.", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("omegaconf.aliases", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("omegaconf.aliases.", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigSyntaxException("hydra.aliases cannot be modified from the command line.");
            }
        }
    }

    private static bool ContainsUnescapedInterpolation(string str)
    {
        for (int i = 0; i < str.Length - 1; i++)
        {
            if (str[i] == '$' && str[i + 1] == '{' && (i == 0 || str[i - 1] != '\\'))
            {
                return true;
            }
        }
        return false;
    }

    private static void RegisterAliasEntry(
        Dictionary<string, string> targets,
        string rawNames,
        string rawTarget,
        SourceInfo source)
    {
        if (string.IsNullOrWhiteSpace(rawNames) || ContainsUnescapedInterpolation(rawNames))
        {
            throw new ConfigSyntaxException($"Invalid alias name '{rawNames}': alias names must be non-empty literal keys.", source);
        }

        if (string.IsNullOrWhiteSpace(rawTarget) || ContainsUnescapedInterpolation(rawTarget))
        {
            throw new ConfigSyntaxException($"Invalid target '{rawTarget}' for alias '{rawNames}': target must be a non-empty literal string.", source);
        }

        string target = rawTarget.Trim();
        var (targetPrefix, targetKey, targetValue) = SplitOverride(target);
        if (string.IsNullOrEmpty(targetKey))
        {
            throw new ConfigSyntaxException($"Invalid target '{rawTarget}' for alias '{rawNames}'.", source);
        }

        // Key-only aliases cannot have a prefix (+, ++, ~) on the target
        if (string.IsNullOrEmpty(targetValue) && !string.IsNullOrEmpty(targetPrefix))
        {
            throw new ConfigSyntaxException(
                $"Invalid target '{rawTarget}' for alias '{rawNames}': key-only alias targets cannot include a '{targetPrefix}' prefix.",
                source);
        }

        string[] parts = rawNames.Split(',');
        foreach (var part in parts)
        {
            string name = part.Trim();
            if (string.IsNullOrEmpty(name))
            {
                throw new ConfigSyntaxException($"Empty alias name in '{rawNames}'.", source);
            }

            var (namePrefix, nameKey, nameValue) = SplitOverride(name);
            if (!string.IsNullOrEmpty(namePrefix) || !string.IsNullOrEmpty(nameValue) || string.IsNullOrEmpty(nameKey))
            {
                throw new ConfigSyntaxException(
                    $"Invalid alias name '{name}': alias names cannot contain prefixes ('+', '++', '~') or '=value' assignments.",
                    source);
            }

            if (targets.ContainsKey(name))
            {
                throw new ConfigSyntaxException($"Duplicate override alias '{name}'.", source);
            }

            targets[name] = target;
        }
    }

    /// <summary>
    /// Expands a single CLI override string if its key matches a registered alias.
    /// </summary>
    /// <param name="overrideArg">The raw CLI argument string (e.g. <c>"bs=32"</c>, <c>"+bs=32"</c>, <c>"~bs"</c>, <c>"v"</c>).</param>
    /// <returns>The expanded CLI argument string, or <paramref name="overrideArg"/> unchanged if no alias applies.</returns>
    public string Expand(string overrideArg)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(overrideArg))
        {
            return overrideArg;
        }

        string trimmed = overrideArg.Trim();
        var (prefix, key, value) = SplitOverride(trimmed);

        if (!Targets.TryGetValue(key, out string? target))
        {
            return overrideArg;
        }

        var (targetPrefix, targetKey, targetValue) = SplitOverride(target);

        // Case 1: Value alias (e.g. "verbose -> verbose=true" or "seed -> +trainer.seed=42")
        if (!string.IsNullOrEmpty(targetValue))
        {
            if (!string.IsNullOrEmpty(value))
            {
                // Explicit value provided on CLI (e.g. "verbose=false") -> leave override unchanged
                return overrideArg;
            }

            string effectivePrefix = !string.IsNullOrEmpty(prefix) ? prefix : targetPrefix;
            return $"{effectivePrefix}{targetKey}{targetValue}";
        }

        // Case 2: Key alias (e.g. "bs -> trainer.batch_size" or "config -> config@_global_")
        if (string.IsNullOrEmpty(value) && prefix != "~")
        {
            throw new ConfigSyntaxException($"Override alias '{key}' requires a value for '{trimmed}'.");
        }

        return $"{prefix}{target}{value}";
    }

    /// <summary>
    /// Formats a human-readable summary of all configured override aliases and any expanded CLI arguments
    /// (matching Hydra's <c>--info aliases</c> and <c>--help</c> output).
    /// </summary>
    /// <param name="cliArgs">Optional command-line arguments to preview expansions for.</param>
    public string Format(IEnumerable<string>? cliArgs = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== Override aliases ==");
        sb.AppendLine();

        // Group alias names that map to the same target in insertion order
        var grouped = new List<(List<string> Names, string Target)>();
        foreach (var (name, target) in Targets)
        {
            int idx = grouped.FindIndex(g => string.Equals(g.Target, target, StringComparison.Ordinal));
            if (idx >= 0)
            {
                grouped[idx].Names.Add(name);
            }
            else
            {
                grouped.Add(([name], target));
            }
        }

        foreach (var (names, target) in grouped)
        {
            sb.AppendLine($"{string.Join(", ", names)} -> {target}");
        }

        if (cliArgs != null)
        {
            var expansions = new List<(string Original, string Expanded)>();
            foreach (var arg in cliArgs)
            {
                if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-')) continue;
                try
                {
                    string expanded = Expand(arg);
                    if (!string.Equals(expanded, arg, StringComparison.Ordinal))
                    {
                        expansions.Add((arg, expanded));
                    }
                }
                catch
                {
                    // Ignore unexpanded syntax errors during --info aliases formatting
                }
            }

            if (expansions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Expanded overrides:");
                foreach (var (orig, exp) in expansions)
                {
                    sb.AppendLine($"  {orig} -> {exp}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Splits a raw override expression into <c>(Prefix, Key, Value)</c>, where <c>Prefix</c> is <c>"++"</c>, <c>"+"</c>, <c>"~"</c>, or <c>""</c>,
    /// <c>Key</c> is the key path before <c>=</c> (including any mutation operator character like <c>+</c> in <c>+=</c> only when attached to <c>=</c>),
    /// and <c>Value</c> is the <c>=...</c> suffix (or <c>""</c> if no <c>=</c> is present).
    /// </summary>
    public static (string Prefix, string Key, string Value) SplitOverride(string raw)
    {
        string prefix = "";
        string rest = raw;

        if (rest.StartsWith("++", StringComparison.Ordinal))
        {
            prefix = "++";
            rest = rest[2..];
        }
        else if (rest.StartsWith('+'))
        {
            prefix = "+";
            rest = rest[1..];
        }
        else if (rest.StartsWith('~'))
        {
            prefix = "~";
            rest = rest[1..];
        }

        int eqIdx = rest.IndexOf('=');
        if (eqIdx < 0)
        {
            return (prefix, rest, "");
        }

        // Handle mutation operators (+=, -=, *=, /=, &=, |=, !=) so key is clean and operator stays with value
        if (eqIdx > 0 && rest[eqIdx - 1] is '+' or '-' or '*' or '/' or '&' or '|' or '!')
        {
            return (prefix, rest[..(eqIdx - 1)], rest[(eqIdx - 1)..]);
        }

        return (prefix, rest[..eqIdx], rest[eqIdx..]);
    }
}
