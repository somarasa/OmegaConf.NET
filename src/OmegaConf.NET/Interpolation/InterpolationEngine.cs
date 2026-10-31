using System.Text.RegularExpressions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Interpolation;

public class InterpolationEngine : IInterpolationContext
{
    // Matches ${...} that is NOT preceded by an unescaped backslash
    private static readonly Regex InterpolationRegex = new(@"(?<!\\)\$\{([^{}]+)\}", RegexOptions.Compiled);

    private readonly ConfigObjectNode _root;
    private readonly Dictionary<string, IResolver> _resolvers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _resolvingKeys = new(StringComparer.OrdinalIgnoreCase);

    public InterpolationEngine(ConfigObjectNode root, IEnumerable<IResolver>? resolvers = null)
    {
        _root = root;
        RegisterResolver(new EnvResolver());
        RegisterResolver(new EvalResolver());

        if (resolvers != null)
        {
            foreach (var r in resolvers)
            {
                RegisterResolver(r);
            }
        }
    }

    public void RegisterResolver(IResolver resolver)
    {
        _resolvers[resolver.Name] = resolver;
    }

    public void ResolveAll()
    {
        ResolveNode(_root, "");
    }

    private void ResolveNode(ConfigNode node, string currentPath)
    {
        switch (node)
        {
            case ConfigObjectNode obj:
                foreach (var (key, child) in obj.Children)
                {
                    string childPath = string.IsNullOrEmpty(currentPath) ? key : $"{currentPath}.{key}";
                    ResolveNode(child, childPath);
                }
                break;

            case ConfigListNode list:
                for (int i = 0; i < list.Items.Count; i++)
                {
                    string itemPath = $"{currentPath}[{i}]";
                    ResolveNode(list.Items[i], itemPath);
                }
                break;

            case ConfigValueNode val:
                if (val.Value is string rawStr && (rawStr.Contains("${") || rawStr.Contains(@"\${")))
                {
                    // Ensure RawTemplate is preserved
                    val.RawTemplate ??= rawStr;
                    val.Value = ResolveStringValue(rawStr, currentPath, val.Source);
                }
                break;
        }
    }

    public object? ResolveStringValue(string raw, string currentPath, SourceInfo source)
    {
        // Check for cycle
        if (!string.IsNullOrEmpty(currentPath))
        {
            if (!_resolvingKeys.Add(currentPath))
            {
                throw new ConfigInterpolationException($"Circular reference detected while resolving '{currentPath}'.", source);
            }
        }

        try
        {
            // Case 1: Exact match - the whole string is a single interpolation "${...}" (potentially with nested "${...}" inside)
            // Preserves the native type (returns int, bool, double, etc. rather than string)
            if (IsSingleOuterInterpolation(raw, out string outerInner))
            {
                return ResolveExpression(outerInner, currentPath, source);
            }

            // Case 2: Embedded interpolation(s) or escaped interpolations
            string result = raw;
            int maxIterations = 25;
            int iteration = 0;

            while (InterpolationRegex.IsMatch(result) && iteration++ < maxIterations)
            {
                result = InterpolationRegex.Replace(result, m =>
                {
                    string inner = m.Groups[1].Value.Trim();
                    var resolved = ResolveExpression(inner, currentPath, source);
                    return resolved?.ToString() ?? "";
                });
            }

            // Unescape \${...} to ${...}
            if (result.Contains(@"\${"))
            {
                result = result.Replace(@"\${", "${");
            }

            return result;
        }
        finally
        {
            if (!string.IsNullOrEmpty(currentPath))
            {
                _resolvingKeys.Remove(currentPath);
            }
        }
    }

    private static bool IsSingleOuterInterpolation(string raw, out string inner)
    {
        inner = "";
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("${", StringComparison.Ordinal) || !raw.EndsWith('}'))
        {
            return false;
        }

        int depth = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 2 < raw.Length && raw[i + 1] == '$' && raw[i + 2] == '{')
            {
                i += 2; // skip escaped \${
                continue;
            }

            if (raw[i] == '$' && i + 1 < raw.Length && raw[i + 1] == '{')
            {
                depth++;
                i++; // skip '{'
                continue;
            }

            if (raw[i] == '{')
            {
                depth++;
            }
            else if (raw[i] == '}')
            {
                depth--;
                if (depth == 0 && i < raw.Length - 1)
                {
                    // Closed before the end of the string (e.g. "${a}...${b}")
                    return false;
                }
            }
        }

        if (depth == 0)
        {
            inner = raw[2..^1].Trim();
            return true;
        }

        return false;
    }

    private object? ResolveExpression(string expr, string currentPath, SourceInfo source)
    {
        // Resolve any inner nested interpolations first (e.g. "eval: ${batch_size} * ${num_gpus}")
        if (expr.Contains("${"))
        {
            int maxIterations = 25;
            int iteration = 0;
            while (InterpolationRegex.IsMatch(expr) && iteration++ < maxIterations)
            {
                expr = InterpolationRegex.Replace(expr, m =>
                {
                    string inner = m.Groups[1].Value.Trim();
                    var resolved = ResolveExpression(inner, currentPath, source);
                    return resolved?.ToString() ?? "";
                });
            }
        }

        // 1. Check if it's a resolver: e.g. "env:PORT" or "eval: 10 * 20"
        int colonIdx = expr.IndexOf(':');
        if (colonIdx > 0)
        {
            string potentialPrefix = expr[..colonIdx].Trim();
            if (_resolvers.TryGetValue(potentialPrefix, out var resolver))
            {
                string arg = expr[(colonIdx + 1)..].Trim();
                return resolver.Resolve(arg, this);
            }
        }

        // 2. Check for default fallback syntax: "${path.to.key:default_val}"
        string path = expr;
        string? defaultValue = null;
        if (colonIdx > 0)
        {
            path = expr[..colonIdx].Trim();
            defaultValue = expr[(colonIdx + 1)..].Trim();
        }

        // 3. Resolve relative / sibling path (e.g. .port or ..host)
        string absolutePath = ResolveAbsolutePath(path, currentPath);

        var resolvedNode = FindNodeByPath(_root, absolutePath);
        if (resolvedNode == null)
        {
            if (defaultValue != null)
            {
                return defaultValue;
            }
            throw new ConfigInterpolationException($"Cannot resolve reference '${{{expr}}}': path '{absolutePath}' does not exist.", source);
        }

        if (resolvedNode is ConfigValueNode valNode)
        {
            if (valNode.IsMissingMarker)
            {
                throw new MissingValueMarkerException(absolutePath, valNode.Source);
            }

            if (valNode.Value is string s && s.Contains("${"))
            {
                // Recursively resolve
                return ResolveStringValue(s, absolutePath, valNode.Source);
            }

            return valNode.Value;
        }

        return resolvedNode;
    }

    /// <summary>
    /// Resolves OmegaConf dot syntax:
    /// .sibling -> looks in parent container of currentPath
    /// ..parent_sibling -> looks one container up
    /// </summary>
    private static string ResolveAbsolutePath(string path, string currentPath)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith('.'))
        {
            return path;
        }

        int dotCount = 0;
        while (dotCount < path.Length && path[dotCount] == '.')
        {
            dotCount++;
        }

        string relativeKey = path[dotCount..].TrimStart('.');

        // Get container parts of currentPath (strip current property name)
        var segments = string.IsNullOrEmpty(currentPath)
            ? []
            : currentPath.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();

        if (segments.Count > 0)
        {
            segments.RemoveAt(segments.Count - 1); // remove current property itself
        }

        // 1 dot (.key): stay in enclosing container
        // 2 dots (..key): go up 1 parent container
        // N dots: go up N-1 parent containers
        int levelsUp = dotCount - 1;
        while (levelsUp > 0 && segments.Count > 0)
        {
            segments.RemoveAt(segments.Count - 1);
            levelsUp--;
        }

        if (string.IsNullOrEmpty(relativeKey))
        {
            return string.Join('.', segments);
        }

        if (segments.Count == 0)
        {
            return relativeKey;
        }

        return $"{string.Join('.', segments)}.{relativeKey}";
    }

    public object? ResolvePath(string path)
    {
        var node = FindNodeByPath(_root, path);
        return node is ConfigValueNode val ? val.Value : node;
    }

    public string ResolveCustom(string resolverName, string argument)
    {
        if (_resolvers.TryGetValue(resolverName, out var resolver))
        {
            return resolver.Resolve(argument, this);
        }
        throw new KeyNotFoundException($"Custom resolver '{resolverName}' is not registered.");
    }

    private static ConfigNode? FindNodeByPath(ConfigObjectNode root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return root;

        string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        ConfigNode current = root;

        foreach (var part in parts)
        {
            // Check for list index syntax: e.g. "endpoints[0]" or "matrix[1][2]"
            int bracketIdx = part.IndexOf('[');
            if (bracketIdx >= 0 && part.EndsWith(']'))
            {
                string propName = part[..bracketIdx];
                if (!string.IsNullOrEmpty(propName))
                {
                    if (current is ConfigObjectNode obj && obj.TryGetChild(propName, out var listCandidate))
                    {
                        current = listCandidate!;
                    }
                    else
                    {
                        return null;
                    }
                }

                // Parse one or more [index] segments
                string indicesPart = part[bracketIdx..];
                while (indicesPart.StartsWith('['))
                {
                    int closeIdx = indicesPart.IndexOf(']');
                    if (closeIdx < 0) return null;

                    string idxStr = indicesPart[1..closeIdx].Trim();
                    if (!int.TryParse(idxStr, out int index)) return null;

                    if (current is ConfigListNode listNode && index >= 0 && index < listNode.Items.Count)
                    {
                        current = listNode.Items[index];
                    }
                    else
                    {
                        return null;
                    }

                    indicesPart = indicesPart[(closeIdx + 1)..];
                }
            }
            else if (current is ConfigListNode directList && int.TryParse(part, out int directIdx))
            {
                if (directIdx >= 0 && directIdx < directList.Items.Count)
                {
                    current = directList.Items[directIdx];
                }
                else
                {
                    return null;
                }
            }
            else if (current is ConfigObjectNode obj && obj.TryGetChild(part, out var next))
            {
                current = next!;
            }
            else
            {
                return null;
            }
        }

        return current;
    }
}
