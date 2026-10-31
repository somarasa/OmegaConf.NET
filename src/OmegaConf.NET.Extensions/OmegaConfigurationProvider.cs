using Microsoft.Extensions.Configuration;
using OmegaConf.NET.Ast;

namespace OmegaConf.NET.Extensions;

public class OmegaConfigurationSource : IConfigurationSource
{
    public Action<OmegaConfBuilder>? Configure { get; set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        var omegaBuilder = OmegaConfBuilder.Create();
        Configure?.Invoke(omegaBuilder);
        return new OmegaConfigurationProvider(omegaBuilder);
    }
}

public class OmegaConfigurationProvider : ConfigurationProvider
{
    private readonly OmegaConfBuilder _builder;

    public OmegaConfigurationProvider(OmegaConfBuilder builder)
    {
        _builder = builder;
    }

    public override void Load()
    {
        var rootNode = _builder.BuildNode();
        Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        FlattenNode(rootNode, "");
    }

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
