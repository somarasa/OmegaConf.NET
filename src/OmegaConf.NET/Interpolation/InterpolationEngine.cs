using System.Text.RegularExpressions;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Interpolation;

/// <summary>
/// Resolves variable references (<c>${path.to.key}</c>), list indexing (<c>${items[0]}</c>),
/// relative dot navigation (<c>${.sibling}</c>, <c>${..parent}</c>), literal escaping (<c>\${...}</c>),
/// default fallbacks (<c>${key:default}</c>), and custom resolvers (<c>${env:...}</c>, <c>${eval:...}</c>)
/// across the composed configuration tree.
/// </summary>
public class InterpolationEngine : IInterpolationContext
{
    /// <summary>
    /// Matches innermost <c>${...}</c> tokens that are NOT preceded by an escaping backslash (<c>\</c>).
    /// </summary>
    private static readonly Regex InterpolationRegex = new(@"(?<!\\)\$\{([^{}]+)\}", RegexOptions.Compiled);

    private readonly ConfigObjectNode _root;
    private readonly Dictionary<string, IResolver> _resolvers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _resolvingKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="InterpolationEngine"/> class and registers the built-in
    /// resolvers (<c>env</c>, <c>oc.env</c>, <c>eval</c>, <c>now</c>, <c>oc.select</c>, <c>oc.decode</c>, and <c>oc.dict.keys</c>).
    /// </summary>
    /// <param name="root">The root configuration object node to resolve against.</param>
    /// <param name="resolvers">Optional additional custom resolvers.</param>
    public InterpolationEngine(ConfigObjectNode root, IEnumerable<IResolver>? resolvers = null)
    {
        _root = root;
        RegisterResolver(new EnvResolver("env"));
        RegisterResolver(new EnvResolver("oc.env"));
        RegisterResolver(new EvalResolver());
        RegisterResolver(new NowResolver());
        RegisterResolver(new OcSelectResolver());
        RegisterResolver(new OcDecodeResolver());
        RegisterResolver(new OcDictKeysResolver());

        if (resolvers != null)
        {
            foreach (var r in resolvers)
            {
                RegisterResolver(r);
            }
        }
    }

    /// <summary>
    /// Registers a custom <see cref="IResolver"/> (overwriting any existing resolver with the same name).
    /// </summary>
    public void RegisterResolver(IResolver resolver)
    {
        _resolvers[resolver.Name] = resolver;
    }

    /// <summary>
    /// Traverses the entire AST from the root node and resolves all variable interpolations in-place.
    /// </summary>
    public void ResolveAll()
    {
        ResolveNode(_root, "");
    }

    /// <summary>
    /// Recursively walks a <see cref="ConfigNode"/> and resolves any string values containing <c>${...}</c> or <c>\${...}</c>.
    /// If a standalone interpolation resolves to a structured <see cref="ConfigNode"/> (e.g. via <c>${oc.decode:[1,2]}</c>,
    /// <c>${oc.dict.keys:db}</c>, or subtree reference <c>${primary_db}</c>), replaces the leaf node in its parent container.
    /// </summary>
    private void ResolveNode(ConfigNode node, string currentPath)
    {
        switch (node)
        {
            case ConfigObjectNode obj:
                foreach (var key in obj.Children.Keys.ToList())
                {
                    var child = obj.Children[key];
                    string childPath = string.IsNullOrEmpty(currentPath) ? key : $"{currentPath}.{key}";
                    if (child is ConfigValueNode childVal && childVal.Value is string rawChildStr && (rawChildStr.Contains("${") || rawChildStr.Contains(@"\${")))
                    {
                        childVal.RawTemplate ??= rawChildStr;
                        var resolved = ResolveStringValue(rawChildStr, childPath, childVal.Source);
                        if (resolved is ConfigNode replacementNode)
                        {
                            var clonedReplacement = replacementNode.Clone();
                            obj.Children[key] = clonedReplacement;
                            ResolveNode(clonedReplacement, childPath);
                        }
                        else
                        {
                            childVal.Value = resolved;
                        }
                    }
                    else
                    {
                        ResolveNode(child, childPath);
                    }
                }
                break;

            case ConfigListNode list:
                for (int i = 0; i < list.Items.Count; i++)
                {
                    string itemPath = $"{currentPath}[{i}]";
                    var item = list.Items[i];
                    if (item is ConfigValueNode itemVal && itemVal.Value is string rawItemStr && (rawItemStr.Contains("${") || rawItemStr.Contains(@"\${")))
                    {
                        itemVal.RawTemplate ??= rawItemStr;
                        var resolved = ResolveStringValue(rawItemStr, itemPath, itemVal.Source);
                        if (resolved is ConfigNode replacementNode)
                        {
                            var clonedReplacement = replacementNode.Clone();
                            list.Items[i] = clonedReplacement;
                            ResolveNode(clonedReplacement, itemPath);
                        }
                        else
                        {
                            itemVal.Value = resolved;
                        }
                    }
                    else
                    {
                        ResolveNode(item, itemPath);
                    }
                }
                break;

            case ConfigValueNode val:
                if (val.Value is string rawStr && (rawStr.Contains("${") || rawStr.Contains(@"\${")))
                {
                    // Preserve the original unresolved formula on RawTemplate before mutating Value
                    val.RawTemplate ??= rawStr;
                    val.Value = ResolveStringValue(rawStr, currentPath, val.Source);
                }
                break;
        }
    }

    /// <summary>
    /// Evaluates all interpolations within <paramref name="raw"/> while tracking <paramref name="currentPath"/>
    /// to detect and prevent circular reference cycles.
    /// </summary>
    public object? ResolveStringValue(string raw, string currentPath, SourceInfo source)
    {
        // Guard against circular references (e.g. a: "${b}", b: "${a}")
        if (!string.IsNullOrEmpty(currentPath))
        {
            if (!_resolvingKeys.Add(currentPath))
            {
                throw new ConfigInterpolationException($"Circular reference detected while resolving '{currentPath}'.", source);
            }
        }

        try
        {
            // Case 1: Pure single interpolation "${...}" (potentially containing nested "${...}" inside)
            // Preserves the native CLR type of the referenced node (int, bool, double, etc.) rather than converting to string
            if (IsSingleOuterInterpolation(raw, out string outerInner))
            {
                return ResolveExpression(outerInner, currentPath, source);
            }

            // Case 2: Embedded string interpolation(s) (e.g. "http://${host}:${port}/api") or escaped literals
            // Iteratively replaces innermost ${...} tokens until all nested levels are resolved
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

            // Unescape literal \${...} sequences into plain ${...} after evaluation
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

    /// <summary>
    /// Checks whether <paramref name="raw"/> is a single outer <c>${...}</c> token spanning the entire string
    /// (as opposed to multiple adjacent tokens like <c>"${a}:${b}"</c>) by tracking brace nesting depth.
    /// </summary>
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
            // Ignore escaped \${ sequences
            if (raw[i] == '\\' && i + 2 < raw.Length && raw[i + 1] == '$' && raw[i + 2] == '{')
            {
                i += 2;
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
                // If depth reaches 0 before the last character, there is text outside the first ${...} block
                if (depth == 0 && i < raw.Length - 1)
                {
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

    /// <summary>
    /// Resolves the inner body of a single <c>${...}</c> expression:
    /// 1. Expands any nested <c>${...}</c> tokens inside the expression first.
    /// 2. Dispatches to a registered <see cref="IResolver"/> if prefixed with <c>resolverName:</c>.
    /// 3. Otherwise resolves the config path (with optional <c>:default_value</c> fallback and relative dot navigation).
    /// </summary>
    private object? ResolveExpression(string expr, string currentPath, SourceInfo source)
    {
        // Step 1: Resolve any inner nested interpolations first (e.g. "eval: ${batch_size} * ${num_gpus}")
        // without re-adding currentPath to _resolvingKeys
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

        // Step 2: Check if the expression invokes a registered resolver (e.g. "env:PORT", "oc.decode:...", or "eval: 10 * 20")
        int colonIdx = expr.IndexOf(':');
        if (colonIdx > 0)
        {
            string potentialPrefix = expr[..colonIdx].Trim();
            if (_resolvers.TryGetValue(potentialPrefix, out var resolver))
            {
                string arg = expr[(colonIdx + 1)..].Trim();
                if (resolver is ITypedResolver typedResolver)
                {
                    return typedResolver.ResolveTyped(arg, this);
                }
                return resolver.Resolve(arg, this);
            }
        }

        // Step 3: Check for default fallback syntax: "${path.to.key:default_val}"
        string path = expr;
        string? defaultValue = null;
        if (colonIdx > 0)
        {
            path = expr[..colonIdx].Trim();
            defaultValue = expr[(colonIdx + 1)..].Trim();
        }

        // Step 4: Convert relative/sibling dot path (e.g. .port or ..host) into an absolute dot path
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

            // If the target node hasn't been resolved yet and still contains ${...}, resolve it recursively
            if (valNode.Value is string s && s.Contains("${"))
            {
                return ResolveStringValue(s, absolutePath, valNode.Source);
            }

            return valNode.Value;
        }

        return resolvedNode;
    }

    /// <summary>
    /// Resolves OmegaConf relative dot navigation syntax against <paramref name="currentPath"/>:
    /// - <c>.sibling</c> (1 leading dot): resolves in the same enclosing object container.
    /// - <c>..parent_key</c> (2 leading dots): resolves 1 container level up.
    /// - N leading dots: resolves N-1 container levels up.
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

        // Split currentPath into container segments and strip the leaf property name itself
        var segments = string.IsNullOrEmpty(currentPath)
            ? []
            : currentPath.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();

        if (segments.Count > 0)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        // 1 dot (.key) stays in the current container; each additional dot ascends one parent container
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

    /// <inheritdoc />
    public object? ResolvePath(string path)
    {
        var node = FindNodeByPath(_root, path);
        return node is ConfigValueNode val ? val.Value : node;
    }

    /// <inheritdoc />
    public string ResolveCustom(string resolverName, string argument)
    {
        if (_resolvers.TryGetValue(resolverName, out var resolver))
        {
            return resolver.Resolve(argument, this);
        }
        throw new KeyNotFoundException($"Custom resolver '{resolverName}' is not registered.");
    }

    /// <summary>
    /// Navigates the AST from <paramref name="root"/> along a dot-separated path,
    /// supporting both bracket list indexing (<c>servers[0]</c>) and dot list indexing (<c>servers.0</c>).
    /// </summary>
    private static ConfigNode? FindNodeByPath(ConfigObjectNode root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return root;

        string[] parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        ConfigNode current = root;

        foreach (var part in parts)
        {
            // Case 1: Bracket list indexing (e.g. "endpoints[0]" or multi-dimensional "matrix[1][2]")
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

                string indicesPart = part[bracketIdx..];
                while (indicesPart.StartsWith('['))
                {
                    int closeIdx = indicesPart.IndexOf(']');
                    if (closeIdx < 0) return null;

                    string idxStr = indicesPart[1..closeIdx].Trim();
                    if (int.TryParse(idxStr, out int index) && current is ConfigListNode listNode)
                    {
                        int actualIdx = index < 0 ? listNode.Items.Count + index : index;
                        if (actualIdx >= 0 && actualIdx < listNode.Items.Count)
                        {
                            current = listNode.Items[actualIdx];
                        }
                        else
                        {
                            return null;
                        }
                    }
                    else if (current is ConfigObjectNode bracketObj && bracketObj.TryGetChild(idxStr, out var dictChild))
                    {
                        current = dictChild!;
                    }
                    else
                    {
                        return null;
                    }

                    indicesPart = indicesPart[(closeIdx + 1)..];
                }
            }
            // Case 2: Dot list indexing (e.g. "servers.0" or "servers.-1")
            else if (current is ConfigListNode directList && int.TryParse(part, out int directIdx))
            {
                int actualIdx = directIdx < 0 ? directList.Items.Count + directIdx : directIdx;
                if (actualIdx >= 0 && actualIdx < directList.Items.Count)
                {
                    current = directList.Items[actualIdx];
                }
                else
                {
                    return null;
                }
            }
            // Case 3: Standard object child property lookup
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
