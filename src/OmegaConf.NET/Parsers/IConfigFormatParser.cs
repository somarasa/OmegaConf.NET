using OmegaConf.NET.Ast;

namespace OmegaConf.NET.Parsers;

/// <summary>
/// Defines a parser capable of translating a text-based configuration format (YAML, JSON/JSONC, TOML, etc.)
/// into an OmegaConf <see cref="ConfigObjectNode"/> Abstract Syntax Tree.
/// </summary>
public interface IConfigFormatParser
{
    /// <summary>
    /// Checks if this parser handles the given file extension (e.g. ".yaml", ".json").
    /// </summary>
    bool CanParse(string extension);

    /// <summary>
    /// Parses the text content into a root <see cref="ConfigObjectNode"/>.
    /// </summary>
    ConfigObjectNode Parse(string content, string? filePath = null);
}
