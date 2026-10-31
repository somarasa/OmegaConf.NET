namespace OmegaConf.NET.Common;

/// <summary>
/// Base exception for all OmegaConf.NET operations, carrying source file and line/column location diagnostics.
/// </summary>
public class OmegaConfException : Exception
{
    /// <summary>Gets the file, line, and column location where the configuration error originated.</summary>
    public SourceInfo SourceLocation { get; }

    /// <summary>Initializes a new instance of <see cref="OmegaConfException"/>.</summary>
    public OmegaConfException(string message, SourceInfo? source = null, Exception? inner = null)
        : base(source != null && source != SourceInfo.Unknown ? $"{message} at {source}" : message, inner)
    {
        SourceLocation = source ?? SourceInfo.Unknown;
    }
}

/// <summary>
/// Thrown when configuration syntax (YAML, JSON/JSONC, TOML, or CLI expression) is malformed.
/// </summary>
public class ConfigSyntaxException : OmegaConfException
{
    /// <summary>Initializes a new instance of <see cref="ConfigSyntaxException"/>.</summary>
    public ConfigSyntaxException(string message, SourceInfo? source = null, Exception? inner = null)
        : base($"Syntax error: {message}", source, inner)
    {
    }
}

/// <summary>
/// Thrown when an operator (<c>+=</c>, <c>-=</c>, <c>*=</c>, etc.) is applied to an incompatible data type or division by zero occurs.
/// </summary>
public class InvalidOperatorException : OmegaConfException
{
    /// <summary>Gets the operator symbol or name that failed.</summary>
    public string Operator { get; }

    /// <summary>Gets the target node or value type description.</summary>
    public string TargetType { get; }

    /// <summary>Initializes a new instance of <see cref="InvalidOperatorException"/>.</summary>
    public InvalidOperatorException(string op, string targetType, string message, SourceInfo? source = null)
        : base($"Operator '{op}' is invalid for target type '{targetType}': {message}", source)
    {
        Operator = op;
        TargetType = targetType;
    }
}

/// <summary>
/// Thrown when a mandatory configuration property marked with <see cref="Attributes.ConfigRequiredAttribute"/> is missing.
/// </summary>
public class MissingRequiredConfigException : OmegaConfException
{
    /// <summary>Gets the dot-separated configuration path of the missing property.</summary>
    public string ConfigPath { get; }

    /// <summary>Gets the enclosing C# target type being bound.</summary>
    public Type TargetType { get; }

    /// <summary>Initializes a new instance of <see cref="MissingRequiredConfigException"/>.</summary>
    public MissingRequiredConfigException(string configPath, Type targetType, SourceInfo? source = null)
        : base($"Required configuration property '{configPath}' was not provided for type '{targetType.FullName}'.", source)
    {
        ConfigPath = configPath;
        TargetType = targetType;
    }
}

/// <summary>
/// Thrown when a configuration value is marked as <c>???</c> (mandatory placeholder) in a base config but was never overridden.
/// </summary>
public class MissingValueMarkerException : OmegaConfException
{
    /// <summary>Gets the dot-separated key path whose value remained <c>???</c>.</summary>
    public string Key { get; }

    /// <summary>Initializes a new instance of <see cref="MissingValueMarkerException"/>.</summary>
    public MissingValueMarkerException(string key, SourceInfo? source = null)
        : base($"Configuration key '{key}' is marked as mandatory ('???') but was never supplied with a concrete value.", source)
    {
        Key = key;
    }
}

/// <summary>
/// Thrown when a configuration value cannot be converted to the target C# property type during binding.
/// </summary>
public class ConfigTypeMismatchException : OmegaConfException
{
    /// <summary>Gets the dot-separated configuration path where the conversion failed.</summary>
    public string ConfigPath { get; }

    /// <summary>Gets the expected C# target type.</summary>
    public Type ExpectedType { get; }

    /// <summary>Gets the actual raw configuration value encountered.</summary>
    public object? ActualValue { get; }

    /// <summary>Initializes a new instance of <see cref="ConfigTypeMismatchException"/>.</summary>
    public ConfigTypeMismatchException(string configPath, Type expectedType, object? actualValue, SourceInfo? source = null, Exception? inner = null)
        : base($"Type mismatch at '{configPath}'. Expected '{expectedType.Name}', but found value '{actualValue}' (type: {actualValue?.GetType().Name ?? "null"}).", source, inner)
    {
        ConfigPath = configPath;
        ExpectedType = expectedType;
        ActualValue = actualValue;
    }
}

/// <summary>
/// Thrown when variable interpolation fails (e.g., circular reference, missing referenced key, or invalid eval expression).
/// </summary>
public class ConfigInterpolationException : OmegaConfException
{
    /// <summary>Initializes a new instance of <see cref="ConfigInterpolationException"/>.</summary>
    public ConfigInterpolationException(string message, SourceInfo? source = null, Exception? inner = null)
        : base($"Interpolation error: {message}", source, inner)
    {
    }
}

/// <summary>
/// Thrown when inheritance or composition resolution fails (file not found or circular inheritance cycle).
/// </summary>
public class ConfigInheritanceException : OmegaConfException
{
    /// <summary>Initializes a new instance of <see cref="ConfigInheritanceException"/>.</summary>
    public ConfigInheritanceException(string message, SourceInfo? source = null, Exception? inner = null)
        : base($"Inheritance error: {message}", source, inner)
    {
    }
}

/// <summary>
/// Thrown in Struct Mode when an override attempts to set or delete a key that does not exist in the schema,
/// or when a <c>+key</c> append operator targets a key that already exists.
/// </summary>
public class ConfigStructException : OmegaConfException
{
    /// <summary>Gets the dot-separated configuration key path that violated struct mode rules.</summary>
    public string ConfigPath { get; }

    /// <summary>Initializes a new instance of <see cref="ConfigStructException"/>.</summary>
    public ConfigStructException(string configPath, string message, SourceInfo? source = null)
        : base($"Struct mode error at '{configPath}': {message}", source)
    {
        ConfigPath = configPath;
    }
}

/// <summary>
/// Thrown when attempting to mutate a <see cref="Ast.ConfigNode"/> that has been locked as read-only (<see cref="Ast.ConfigNode.IsReadOnly"/>).
/// </summary>
public class ConfigReadOnlyException : OmegaConfException
{
    /// <summary>Initializes a new instance of <see cref="ConfigReadOnlyException"/>.</summary>
    public ConfigReadOnlyException(string message, SourceInfo? source = null)
        : base($"Read-only configuration error: {message}", source)
    {
    }
}
