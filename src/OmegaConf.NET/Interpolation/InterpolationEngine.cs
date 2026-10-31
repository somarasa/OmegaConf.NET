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
            // Case 1: Exact match - the whole string is a single interpolation "${path.to.val}"
            // Preserves the native type (returns int, bool, double, etc. rather than string)
            var match = InterpolationRegex.Match(raw);
            if (match.Success && match.Index == 0 && match.Length == raw.Length)
            {
                string inner = match.Groups[1].Value.Trim();
                var resolved = ResolveExpression(inner, currentPath, source);
                return resolved;
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

    private object? ResolveExpression(string expr, string currentPath, SourceInfo source)
    {
        // 1. Check if it's a resolver: e.g. "env:PORT" or "eval: 10 * 20"
        int colonIdx = expr.IndexOf(':');
        if (colonIdx > 0)
        {
            string potentialPrefix = expr[..colonIdx].Trim();
            if (_resolvers.TryGetValue(potentialPrefix, out var resolver))
            {
                string arg = expr[(colonIdx + 1)..].Trim();
                // Resolve any nested interpolations inside the argument first
                if (arg.Contains("${"))
                {
                    arg = ResolveStringValue(arg, currentPath, source)?.ToString() ?? "";
                }
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
            if (current is ConfigObjectNode obj && obj.TryGetChild(part, out var next))
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
