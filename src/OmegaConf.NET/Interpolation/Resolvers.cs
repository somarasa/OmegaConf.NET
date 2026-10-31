using System.Globalization;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Parsers;

namespace OmegaConf.NET.Interpolation;

/// <summary>
/// Provides contextual path lookup and custom resolver invocation during variable interpolation.
/// </summary>
public interface IInterpolationContext
{
    /// <summary>
    /// Resolves a dot-separated path (e.g. "db.port") to its raw value or node.
    /// </summary>
    object? ResolvePath(string path);

    /// <summary>
    /// Executes a registered resolver (e.g. "env:DB_PORT").
    /// </summary>
    string ResolveCustom(string resolverName, string argument);
}

/// <summary>
/// Defines a named interpolation resolver invoked via <c>${name:argument}</c> syntax.
/// </summary>
public interface IResolver
{
    /// <summary>Gets the prefix name of the resolver (e.g., <c>"env"</c> or <c>"eval"</c>).</summary>
    string Name { get; }

    /// <summary>Evaluates the resolver argument and returns the resulting string representation.</summary>
    string Resolve(string argument, IInterpolationContext context);
}

/// <summary>
/// Extended resolver interface capable of returning strongly typed CLR values (<see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, <c>null</c>)
/// or structured AST nodes (<see cref="ConfigListNode"/>, <see cref="ConfigObjectNode"/>) when evaluated as a standalone <c>${...}</c> interpolation.
/// </summary>
public interface ITypedResolver : IResolver
{
    /// <summary>
    /// Evaluates the resolver argument and returns a strongly typed scalar or <see cref="ConfigNode"/>.
    /// </summary>
    object? ResolveTyped(string argument, IInterpolationContext context);
}

/// <summary>
/// Built-in environment variable resolver (<c>env</c> and <c>oc.env</c>).
/// Syntax: <c>${env:VAR_NAME}</c>, <c>${oc.env:VAR_NAME, default_val}</c>
/// </summary>
public class EnvResolver : IResolver
{
    /// <inheritdoc />
    public string Name { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="EnvResolver"/> with the specified resolver name (defaults to <c>"env"</c>).
    /// </summary>
    public EnvResolver(string name = "env")
    {
        Name = name;
    }

    /// <inheritdoc />
    public string Resolve(string argument, IInterpolationContext context)
    {
        string varName = argument;
        string? defaultValue = null;

        // Split optional fallback value separated by comma: ${env:VAR, fallback}
        int commaIdx = argument.IndexOf(',');
        if (commaIdx >= 0)
        {
            varName = argument[..commaIdx].Trim();
            defaultValue = argument[(commaIdx + 1)..].Trim();
        }

        string? envVal = Environment.GetEnvironmentVariable(varName);
        if (!string.IsNullOrEmpty(envVal))
        {
            return envVal;
        }

        if (defaultValue != null)
        {
            return defaultValue;
        }

        throw new KeyNotFoundException($"Environment variable '{varName}' is not set and no default value was provided.");
    }
}

/// <summary>
/// Built-in timestamp resolver (<c>now</c>) inspired by Hydra's <c>${now:...}</c> resolver.
/// Syntax: <c>${now:yyyy-MM-dd}</c> or <c>${now:HH-mm-ss}</c>
/// </summary>
public class NowResolver : IResolver
{
    /// <inheritdoc />
    public string Name => "now";

    /// <inheritdoc />
    public string Resolve(string argument, IInterpolationContext context)
    {
        string format = string.IsNullOrWhiteSpace(argument) ? "yyyy-MM-dd_HH-mm-ss" : argument.Trim();
        return DateTime.Now.ToString(format, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Built-in OmegaConf safe selector resolver (<c>oc.select</c>).
/// Safely selects a configuration path with an optional fallback value, or returns <c>null</c> if the path does not exist.
/// Syntax: <c>${oc.select:path.to.key}</c> or <c>${oc.select:path.to.key, default_val}</c>
/// </summary>
public class OcSelectResolver : ITypedResolver
{
    /// <inheritdoc />
    public string Name => "oc.select";

    /// <inheritdoc />
    public string Resolve(string argument, IInterpolationContext context)
    {
        return ResolveTyped(argument, context)?.ToString() ?? "";
    }

    /// <inheritdoc />
    public object? ResolveTyped(string argument, IInterpolationContext context)
    {
        string path = argument.Trim();
        string? fallbackRaw = null;

        int commaIdx = argument.IndexOf(',');
        if (commaIdx >= 0)
        {
            path = argument[..commaIdx].Trim();
            fallbackRaw = argument[(commaIdx + 1)..].Trim();
        }

        object? resolved = context.ResolvePath(path);
        if (resolved != null && !(resolved is string s && s == ConfigValueNode.MissingMarker))
        {
            return resolved;
        }

        if (fallbackRaw != null)
        {
            return OcDecodeResolver.DecodeValue(fallbackRaw);
        }

        return null;
    }
}

/// <summary>
/// Built-in OmegaConf decoder resolver (<c>oc.decode</c>).
/// Parses a raw string (e.g., from an environment variable) into a typed scalar (<see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, <c>null</c>)
/// or a structured AST node (<see cref="ConfigListNode"/> or <see cref="ConfigObjectNode"/>).
/// Syntax: <c>${oc.decode:${oc.env:PORT_STR}}</c> or <c>${oc.decode:[1, 2, 3]}</c>
/// </summary>
public class OcDecodeResolver : ITypedResolver
{
    private static readonly YamlFormatParser YamlParser = new();

    /// <inheritdoc />
    public string Name => "oc.decode";

    /// <inheritdoc />
    public string Resolve(string argument, IInterpolationContext context)
    {
        return ResolveTyped(argument, context)?.ToString() ?? "";
    }

    /// <inheritdoc />
    public object? ResolveTyped(string argument, IInterpolationContext context)
    {
        return DecodeValue(argument);
    }

    /// <summary>
    /// Decodes a string literal into a native CLR scalar or an OmegaConf <see cref="ConfigNode"/>.
    /// </summary>
    public static object? DecodeValue(string raw)
    {
        string trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) || trimmed == "~")
        {
            return null;
        }

        if (bool.TryParse(trimmed, out bool b))
        {
            return b;
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
        {
            return l;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return d;
        }

        // Decode inline YAML/JSON list or mapping into a ConfigListNode or ConfigObjectNode
        if ((trimmed.StartsWith('[') && trimmed.EndsWith(']')) ||
            (trimmed.StartsWith('{') && trimmed.EndsWith('}')))
        {
            var wrapper = YamlParser.Parse($"_decoded_: {trimmed}");
            if (wrapper.TryGetChild("_decoded_", out var decodedNode) && decodedNode != null)
            {
                return decodedNode;
            }
        }

        // Strip enclosing single or double quotes if present
        if (trimmed.Length >= 2 &&
            ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('\'') && trimmed.EndsWith('\''))))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}

/// <summary>
/// Built-in OmegaConf dictionary keys resolver (<c>oc.dict.keys</c>).
/// Returns a <see cref="ConfigListNode"/> containing the keys of the target <see cref="ConfigObjectNode"/> at the specified path.
/// Syntax: <c>${oc.dict.keys:path.to.mapping}</c>
/// </summary>
public class OcDictKeysResolver : ITypedResolver
{
    /// <inheritdoc />
    public string Name => "oc.dict.keys";

    /// <inheritdoc />
    public string Resolve(string argument, IInterpolationContext context)
    {
        var node = ResolveTyped(argument, context);
        return node?.ToString() ?? "[]";
    }

    /// <inheritdoc />
    public object? ResolveTyped(string argument, IInterpolationContext context)
    {
        string path = argument.Trim();
        var target = context.ResolvePath(path);
        if (target is ConfigObjectNode objNode)
        {
            var keyNodes = objNode.Children.Keys.Select(k => (ConfigNode)new ConfigValueNode(k));
            return new ConfigListNode(keyNodes);
        }

        throw new ConfigInterpolationException($"Resolver 'oc.dict.keys' requires path '{path}' to reference a mapping/object node.");
    }
}

/// <summary>
/// User-defined custom resolver delegate wrapper registered via <see cref="OmegaConfBuilder.RegisterResolver(string, Func{string, string})"/>.
/// </summary>
public class CustomResolver : IResolver
{
    private readonly Func<string, string> _handler;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>
    /// Initializes a new custom resolver with the given prefix name and evaluation delegate.
    /// </summary>
    public CustomResolver(string name, Func<string, string> handler)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <inheritdoc />
    public string Resolve(string argument, IInterpolationContext context)
    {
        return _handler(argument);
    }
}
