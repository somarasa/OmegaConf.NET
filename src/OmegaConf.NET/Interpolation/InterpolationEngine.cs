using System.Text.RegularExpressions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Interpolation;

public class InterpolationEngine : IInterpolationContext
{
    private static readonly Regex InterpolationRegex = new(@"\$\{([^{}]+)\}", RegexOptions.Compiled);

    private readonly ConfigObjectNode _root;
    private readonly Dictionary<string, IResolver> _resolvers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _resolvingKeys = new(StringComparer.OrdinalIgnoreCase);

    public InterpolationEngine(ConfigObjectNode root, IEnumerable<IResolver>? resolvers = null)
    {
        _root = root;
        RegisterResolver(new EnvResolver());

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
                if (val.Value is string rawStr && rawStr.Contains("${"))
                {
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
            // This preserves the native type (e.g. returns int, bool, double rather than string)
            var match = InterpolationRegex.Match(raw);
            if (match.Success && match.Index == 0 && match.Length == raw.Length)
            {
                string inner = match.Groups[1].Value.Trim();
                return ResolveExpression(inner, source);
            }

            // Case 2: Embedded interpolation(s) inside a string: e.g. "http://${db.host}:${db.port}/api"
            string result = raw;
            int maxIterations = 20; // Guard against deep nested expansion
            int iteration = 0;

            while (InterpolationRegex.IsMatch(result) && iteration++ < maxIterations)
            {
                result = InterpolationRegex.Replace(result, m =>
                {
                    string inner = m.Groups[1].Value.Trim();
                    var resolved = ResolveExpression(inner, source);
                    return resolved?.ToString() ?? "";
                });
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

    private object? ResolveExpression(string expr, SourceInfo source)
    {
        // Check if it's a resolver: e.g. "env:PORT" or "env:PORT, 8080"
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
                    arg = ResolveStringValue(arg, "", source)?.ToString() ?? "";
                }
                return resolver.Resolve(arg, this);
            }
        }

        // Check for default fallback syntax: "${path.to.key:default_val}"
        string path = expr;
        string? defaultValue = null;
        if (colonIdx > 0)
        {
            path = expr[..colonIdx].Trim();
            defaultValue = expr[(colonIdx + 1)..].Trim();
        }

        var resolvedNode = FindNodeByPath(_root, path);
        if (resolvedNode == null)
        {
            if (defaultValue != null)
            {
                return defaultValue;
            }
            throw new ConfigInterpolationException($"Cannot resolve reference '${{{expr}}}': path '{path}' does not exist.", source);
        }

        if (resolvedNode is ConfigValueNode valNode)
        {
            if (valNode.IsMissingMarker)
            {
                throw new MissingValueMarkerException(path, valNode.Source);
            }

            if (valNode.Value is string s && s.Contains("${"))
            {
                // Recursively resolve
                return ResolveStringValue(s, path, valNode.Source);
            }

            return valNode.Value;
        }

        return resolvedNode;
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
