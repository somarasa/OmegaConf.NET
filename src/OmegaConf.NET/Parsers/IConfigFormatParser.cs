using OmegaConf.NET.Ast;

namespace OmegaConf.NET.Parsers;

public interface IConfigFormatParser
{
    /// <summary>
    /// Checks if this parser handles the given file extension (e.g. ".yaml", ".json").
    /// </summary>
    bool CanParse(string extension);

    /// <summary>
    /// Parses the text content into a root ConfigObjectNode.
    /// </summary>
    ConfigObjectNode Parse(string content, string? filePath = null);
}
