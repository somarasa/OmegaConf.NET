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
/// Built-in environment variable resolver.
/// Syntax: <c>${env:VAR_NAME}</c> or <c>${env:VAR_NAME, default_val}</c>
/// </summary>
public class EnvResolver : IResolver
{
    /// <inheritdoc />
    public string Name => "env";

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
