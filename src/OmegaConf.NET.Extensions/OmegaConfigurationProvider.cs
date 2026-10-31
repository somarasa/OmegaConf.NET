using Microsoft.Extensions.Configuration;
using OmegaConf.NET.Ast;

namespace OmegaConf.NET.Extensions;

/// <summary>
/// Represents an OmegaConf.NET configuration source for <see cref="IConfigurationBuilder"/>.
/// </summary>
public class OmegaConfigurationSource : IConfigurationSource
{
    /// <summary>
    /// Gets or sets the delegate used to configure the underlying <see cref="OmegaConfBuilder"/>.
    /// </summary>
    public Action<OmegaConfBuilder>? Configure { get; set; }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        var omegaBuilder = OmegaConfBuilder.Create();
        Configure?.Invoke(omegaBuilder);
        return new OmegaConfigurationProvider(omegaBuilder);
    }
}

/// <summary>
/// Adapts a composed and interpolated <see cref="OmegaConfBuilder"/> tree into Microsoft's
/// colon-delimited key-value <see cref="ConfigurationProvider"/> dictionary (e.g. <c>db:host</c>, <c>endpoints:0</c>).
/// </summary>
public class OmegaConfigurationProvider : ConfigurationProvider
{
    private readonly OmegaConfBuilder _builder;

    /// <summary>
    /// Initializes a new instance of the <see cref="OmegaConfigurationProvider"/> class.
    /// </summary>
    public OmegaConfigurationProvider(OmegaConfBuilder builder)
    {
        _builder = builder;
    }

    /// <summary>
    /// Builds the OmegaConf configuration tree and flattens all objects, lists, and scalars into <see cref="ConfigurationProvider.Data"/>.
    /// </summary>
    public override void Load()
    {
        var rootNode = _builder.BuildNode();
        Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FlattenNode(rootNode, "");
    }

    /// <summary>
    /// Recursively flattens AST nodes using standard .NET configuration colon separators (<c>section:subsection:key</c> and <c>list:0</c>).
    /// </summary>
    private void FlattenNode(ConfigNode node, string currentKey)
    {
        switch (node)
        {
            case ConfigObjectNode obj:
                foreach (var (key, child) in obj.Children)
                {
                    string childKey = string.IsNullOrEmpty(currentKey) ? key : $"{currentKey}:{key}";
                    FlattenNode(child, childKey);
                }
                break;

            case ConfigListNode list:
                for (int i = 0; i < list.Items.Count; i++)
                {
                    string itemKey = $"{currentKey}:{i}";
                    FlattenNode(list.Items[i], itemKey);
                }
                break;

            case ConfigValueNode val:
                if (!string.IsNullOrEmpty(currentKey))
                {
                    Data[currentKey] = val.IsMissingMarker ? null : val.Value?.ToString();
                }
                break;
        }
    }
}
