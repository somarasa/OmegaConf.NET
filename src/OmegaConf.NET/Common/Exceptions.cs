namespace OmegaConf.NET.Common;

/// <summary>
/// Base exception for all OmegaConf.NET operations.
/// </summary>
public class OmegaConfException : Exception
{
    public SourceInfo SourceLocation { get; }

    public OmegaConfException(string message, SourceInfo? source = null, Exception? inner = null)
        : base(source != null && source != SourceInfo.Unknown ? $"{message} at {source}" : message, inner)
    {
        SourceLocation = source ?? SourceInfo.Unknown;
    }
}

/// <summary>
/// Thrown when configuration syntax is malformed.
/// </summary>
public class ConfigSyntaxException : OmegaConfException
{
    public ConfigSyntaxException(string message, SourceInfo? source = null, Exception? inner = null)
        : base($"Syntax error: {message}", source, inner)
    {
    }
}

/// <summary>
/// Thrown when an operator (+=, -=, *=, etc.) is applied to an incompatible data type.
/// </summary>
public class InvalidOperatorException : OmegaConfException
{
    public string Operator { get; }
    public string TargetType { get; }

    public InvalidOperatorException(string op, string targetType, string message, SourceInfo? source = null)
        : base($"Operator '{op}' is invalid for target type '{targetType}': {message}", source)
    {
        Operator = op;
        TargetType = targetType;
    }
}

/// <summary>
/// Thrown when a mandatory configuration property is missing.
/// </summary>
public class MissingRequiredConfigException : OmegaConfException
{
    public string ConfigPath { get; }
    public Type TargetType { get; }

    public MissingRequiredConfigException(string configPath, Type targetType, SourceInfo? source = null)
        : base($"Required configuration property '{configPath}' was not provided for type '{targetType.FullName}'.", source)
    {
        ConfigPath = configPath;
        TargetType = targetType;
    }
}

/// <summary>
/// Thrown when a configuration value is marked as '???' (mandatory) but was never overridden.
/// </summary>
public class MissingValueMarkerException : OmegaConfException
{
    public string Key { get; }

    public MissingValueMarkerException(string key, SourceInfo? source = null)
        : base($"Configuration key '{key}' is marked as mandatory ('???') but was never supplied with a concrete value.", source)
    {
        Key = key;
    }
}

/// <summary>
/// Thrown when a configuration value cannot be converted to the target C# property type.
/// </summary>
public class ConfigTypeMismatchException : OmegaConfException
{
    public string ConfigPath { get; }
    public Type ExpectedType { get; }
    public object? ActualValue { get; }

    public ConfigTypeMismatchException(string configPath, Type expectedType, object? actualValue, SourceInfo? source = null, Exception? inner = null)
        : base($"Type mismatch at '{configPath}'. Expected '{expectedType.Name}', but found value '{actualValue}' (type: {actualValue?.GetType().Name ?? "null"}).", source, inner)
    {
        ConfigPath = configPath;
        ExpectedType = expectedType;
        ActualValue = actualValue;
    }
}

/// <summary>
/// Thrown when variable interpolation fails (e.g., circular reference or missing key).
/// </summary>
public class ConfigInterpolationException : OmegaConfException
{
    public ConfigInterpolationException(string message, SourceInfo? source = null, Exception? inner = null)
        : base($"Interpolation error: {message}", source, inner)
    {
    }
}

/// <summary>
/// Thrown when inheritance resolution fails (file not found or circular inheritance).
/// </summary>
public class ConfigInheritanceException : OmegaConfException
{
    public ConfigInheritanceException(string message, SourceInfo? source = null, Exception? inner = null)
        : base($"Inheritance error: {message}", source, inner)
    {
    }
}
