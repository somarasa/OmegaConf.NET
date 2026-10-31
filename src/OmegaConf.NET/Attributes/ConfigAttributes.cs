namespace OmegaConf.NET.Attributes;

/// <summary>
/// Specifies that a configuration property is mandatory.
/// If omitted from all layers or left as '???', a <see cref="Common.MissingRequiredConfigException"/> is thrown.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ConfigRequiredAttribute : Attribute
{
    /// <summary>Gets an optional custom validation error message.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Initializes a new instance of <see cref="ConfigRequiredAttribute"/>.</summary>
    public ConfigRequiredAttribute(string? errorMessage = null)
    {
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Specifies the explicit configuration key name that maps to this C# property or constructor parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ConfigKeyAttribute : Attribute
{
    /// <summary>Gets the explicit configuration key name.</summary>
    public string Key { get; }

    /// <summary>Initializes a new instance of <see cref="ConfigKeyAttribute"/> with the specified configuration key.</summary>
    public ConfigKeyAttribute(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }
}

/// <summary>
/// Specifies that a property should be ignored during configuration binding and JSON Schema generation.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ConfigIgnoreAttribute : Attribute
{
}

/// <summary>
/// Indicates the source file or path associated with this configuration class (for diagnostics and future two-way write-back).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ConfigSourceAttribute : Attribute
{
    /// <summary>Gets the associated configuration file or relative path.</summary>
    public string SourcePath { get; }

    /// <summary>Initializes a new instance of <see cref="ConfigSourceAttribute"/>.</summary>
    public ConfigSourceAttribute(string sourcePath)
    {
        SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
    }
}
