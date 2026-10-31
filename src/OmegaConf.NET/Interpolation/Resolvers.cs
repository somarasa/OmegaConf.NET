namespace OmegaConf.NET.Interpolation;

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

public interface IResolver
{
    string Name { get; }
    string Resolve(string argument, IInterpolationContext context);
}

/// <summary>
/// Built-in environment variable resolver.
/// Syntax: ${env:VAR_NAME} or ${env:VAR_NAME, default_val}
/// </summary>
public class EnvResolver : IResolver
{
    public string Name => "env";

    public string Resolve(string argument, IInterpolationContext context)
    {
        string varName = argument;
        string? defaultValue = null;

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
/// User-defined custom resolver delegate wrapper.
/// </summary>
public class CustomResolver : IResolver
{
    private readonly Func<string, string> _handler;

    public string Name { get; }

    public CustomResolver(string name, Func<string, string> handler)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public string Resolve(string argument, IInterpolationContext context)
    {
        return _handler(argument);
    }
}
