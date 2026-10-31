using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Parsers;

public class ConfigFormatRegistry
{
    private readonly List<IConfigFormatParser> _parsers = [];

    public static ConfigFormatRegistry Default { get; } = CreateDefault();

    public static ConfigFormatRegistry CreateDefault()
    {
        var registry = new ConfigFormatRegistry();
        registry.Register(new YamlFormatParser());
        registry.Register(new JsonFormatParser());
        registry.Register(new TomlFormatParser());
        return registry;
    }

    public void Register(IConfigFormatParser parser)
    {
        _parsers.Insert(0, parser); // Newer registered parsers take priority
    }

    public IConfigFormatParser GetParserForExtension(string extension)
    {
        foreach (var parser in _parsers)
        {
            if (parser.CanParse(extension))
            {
                return parser;
            }
        }

        throw new ConfigSyntaxException($"No configuration parser registered for extension '{extension}'.");
    }

    public ConfigObjectNode ParseFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Configuration file not found: {filePath}", filePath);
        }

        string ext = Path.GetExtension(filePath);
        var parser = GetParserForExtension(ext);
        string content = File.ReadAllText(filePath);
        return parser.Parse(content, filePath);
    }
}
