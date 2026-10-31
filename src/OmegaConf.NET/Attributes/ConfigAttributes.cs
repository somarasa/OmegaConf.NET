namespace OmegaConf.NET.Attributes;

/// <summary>
/// Specifies that a configuration property is mandatory.
/// If omitted from all layers or left as '???', a MissingRequiredConfigException is thrown.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ConfigRequiredAttribute : Attribute
{
    public string? ErrorMessage { get; }

    public ConfigRequiredAttribute(string? errorMessage = null)
    {
        ErrorMessage = errorMessage;
    }
}

/// <summary>
/// Specifies the configuration key name that maps to this property.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class ConfigKeyAttribute : Attribute
{
    public string Key { get; }

    public ConfigKeyAttribute(string key)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
    }
}

/// <summary>
/// Specifies that a property should be ignored during configuration binding.
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
    public string SourcePath { get; }

    public ConfigSourceAttribute(string sourcePath)
    {
        SourcePath = sourcePath ?? throw new ArgumentNullException(nameof(sourcePath));
    }
}
