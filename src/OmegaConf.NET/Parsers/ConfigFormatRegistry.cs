using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Parsers;

/// <summary>
/// Registry of <see cref="IConfigFormatParser"/> implementations keyed by file extension.
/// Pre-registered with <see cref="YamlFormatParser"/>, <see cref="JsonFormatParser"/>, and <see cref="TomlFormatParser"/>.
/// </summary>
public class ConfigFormatRegistry
{
    private readonly List<IConfigFormatParser> _parsers = [];

    /// <summary>
    /// Gets the default shared <see cref="ConfigFormatRegistry"/> instance supporting YAML, JSON/JSONC, and TOML.
    /// </summary>
    public static ConfigFormatRegistry Default { get; } = CreateDefault();

    /// <summary>
    /// Creates a new <see cref="ConfigFormatRegistry"/> populated with the built-in YAML, JSON/JSONC, and TOML parsers.
    /// </summary>
    public static ConfigFormatRegistry CreateDefault()
    {
        var registry = new ConfigFormatRegistry();
        registry.Register(new YamlFormatParser());
        registry.Register(new JsonFormatParser());
        registry.Register(new TomlFormatParser());
        return registry;
    }

    /// <summary>
    /// Registers a custom <see cref="IConfigFormatParser"/>. Newly registered parsers take priority over earlier ones.
    /// </summary>
    public void Register(IConfigFormatParser parser)
    {
        _parsers.Insert(0, parser);
    }

    /// <summary>
    /// Resolves the <see cref="IConfigFormatParser"/> capable of handling <paramref name="extension"/>.
    /// </summary>
    /// <exception cref="ConfigSyntaxException">Thrown when no parser is registered for the given file extension.</exception>
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

    /// <summary>
    /// Reads <paramref name="filePath"/> from disk and parses it using the registered parser for its file extension.
    /// </summary>
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
