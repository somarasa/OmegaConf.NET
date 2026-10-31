using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;
using Tomlyn;
using Tomlyn.Model;

namespace OmegaConf.NET.Parsers;

/// <summary>
/// Parses TOML (<c>.toml</c>) configuration files into an OmegaConf <see cref="ConfigObjectNode"/> AST.
/// Supports tables, nested table arrays, primitives, and key-suffix mutation operators.
/// </summary>
public class TomlFormatParser : IConfigFormatParser
{
    /// <inheritdoc />
    public bool CanParse(string extension)
    {
        return string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public ConfigObjectNode Parse(string content, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ConfigObjectNode(source: new SourceInfo(filePath, 0, 0));
        }

        try
        {
            var model = Toml.ToModel(content, filePath);
            return ConvertTable(model, filePath, ConfigOperator.Assign);
        }
        catch (TomlException ex)
        {
            var firstErr = ex.Diagnostics.FirstOrDefault();
            int line = firstErr?.Span.Start.Line ?? 0;
            int col = firstErr?.Span.Start.Column ?? 0;
            throw new ConfigSyntaxException(ex.Message, new SourceInfo(filePath, line, col), ex);
        }
    }

    private ConfigObjectNode ConvertTable(TomlTable table, string? filePath, ConfigOperator op)
    {
        var objNode = new ConfigObjectNode(op, new SourceInfo(filePath, 0, 0));

        foreach (var (key, value) in table)
        {
            var (cleanKey, keyOp) = OperatorParser.ParseKey(key);
            var child = ConvertValue(value, filePath, keyOp);
            objNode.SetChild(cleanKey, child);
        }

        return objNode;
    }

    private ConfigNode ConvertValue(object? val, string? filePath, ConfigOperator op)
    {
        var source = new SourceInfo(filePath, 0, 0);

        if (val == null)
        {
            return new ConfigValueNode(null, op, source);
        }

        if (val is TomlTable table)
        {
            return ConvertTable(table, filePath, op);
        }

        if (val is TomlTableArray tableArray)
        {
            var listNode = new ConfigListNode(op: op, source: source);
            foreach (var item in tableArray)
            {
                listNode.Items.Add(ConvertTable(item, filePath, ConfigOperator.Assign));
            }
            return listNode;
        }

        if (val is TomlArray array)
        {
            var listNode = new ConfigListNode(op: op, source: source);
            foreach (var item in array)
            {
                listNode.Items.Add(ConvertValue(item, filePath, ConfigOperator.Assign));
            }
            return listNode;
        }

        if (val is string s && s == ConfigValueNode.MissingMarker)
        {
            return new ConfigValueNode(ConfigValueNode.MissingMarker, op, source);
        }

        return new ConfigValueNode(val, op, source);
    }
}
