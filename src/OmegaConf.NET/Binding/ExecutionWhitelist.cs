using OmegaConf.NET.Common;

namespace OmegaConf.NET.Binding;

/// <summary>
/// Enforces security policy and target whitelisting during dynamic <c>_target_</c> object instantiation.
/// Modeled on Python Hydra's <c>hydra._internal.execution_policy</c> and <c>ExecutionWhitelist</c>.
/// </summary>
public sealed class ExecutionWhitelist
{
    private static readonly HashSet<string> DefaultBlockedTypePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
        "System.IO.File",
        "System.IO.Directory",
        "System.Reflection.Assembly",
        "System.AppDomain"
    };

    private readonly List<string> _allowedPatterns;

    /// <summary>
    /// Gets the configured list of allowed target type names or namespace wildcard patterns (e.g. <c>"MyApp.Plugins.*"</c>).
    /// </summary>
    public IReadOnlyList<string> AllowedPatterns => _allowedPatterns;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionWhitelist"/> class.
    /// </summary>
    /// <param name="allowedPatterns">Allowed full type names or <c>Namespace.*</c> wildcard patterns.</param>
    public ExecutionWhitelist(IEnumerable<string> allowedPatterns)
    {
        _allowedPatterns = allowedPatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizeTypeIdentifier)
            .ToList();
    }

    /// <summary>
    /// Determines whether the specified target type name (and optional resolved CLR <see cref="Type"/>) is permitted by this whitelist.
    /// </summary>
    public bool IsAllowed(string rawTargetName, Type? resolvedType = null)
    {
        string normalizedTarget = NormalizeTypeIdentifier(rawTargetName);
        string? resolvedFullName = resolvedType?.FullName?.Replace('+', '.');

        foreach (var pattern in _allowedPatterns)
        {
            if (MatchesPattern(normalizedTarget, pattern) ||
                (resolvedFullName != null && MatchesPattern(resolvedFullName, pattern)) ||
                (resolvedType?.FullName != null && MatchesPattern(resolvedType.FullName, pattern)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates a <c>_target_</c> string and resolved CLR <see cref="Type"/> against default blocked system targets
    /// and an optional <paramref name="whitelist"/>. Throws <see cref="ConfigSecurityException"/> if blocked.
    /// </summary>
    public static void ValidateTarget(
        string rawTargetName,
        Type? resolvedType,
        ExecutionWhitelist? whitelist,
        SourceInfo? source = null)
    {
        string normalizedTarget = NormalizeTypeIdentifier(rawTargetName);
        string effectiveName = resolvedType?.FullName ?? normalizedTarget;

        // 1. If an explicit whitelist is provided, the target MUST match one of its patterns
        if (whitelist != null)
        {
            if (!whitelist.IsAllowed(rawTargetName, resolvedType))
            {
                throw new ConfigSecurityException(
                    rawTargetName,
                    $"Target '{rawTargetName}' is not permitted by the configured ExecutionWhitelist.",
                    source);
            }
            return;
        }

        // 2. Even without an explicit whitelist, block dangerous built-in system execution targets by default
        foreach (var blocked in DefaultBlockedTypePrefixes)
        {
            if (normalizedTarget.Equals(blocked, StringComparison.OrdinalIgnoreCase) ||
                normalizedTarget.StartsWith(blocked + ".", StringComparison.OrdinalIgnoreCase) ||
                effectiveName.Equals(blocked, StringComparison.OrdinalIgnoreCase) ||
                effectiveName.StartsWith(blocked + ".", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConfigSecurityException(
                    rawTargetName,
                    $"Target '{rawTargetName}' is a blocked system execution type and cannot be instantiated without an explicit ExecutionWhitelist.",
                    source);
            }
        }
    }

    private static bool MatchesPattern(string targetTypeName, string pattern)
    {
        if (pattern == "*") return true;

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            string prefix = pattern[..^2];
            return targetTypeName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                   targetTypeName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase) ||
                   targetTypeName.StartsWith(prefix + "+", StringComparison.OrdinalIgnoreCase);
        }

        string normalizedCandidate = targetTypeName.Replace('+', '.');
        string normalizedPattern = pattern.Replace('+', '.');
        return string.Equals(normalizedCandidate, normalizedPattern, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTypeIdentifier(string raw)
    {
        string trimmed = raw.Trim();
        int commaIdx = trimmed.IndexOf(',');
        return commaIdx > 0 ? trimmed[..commaIdx].Trim() : trimmed;
    }
}
