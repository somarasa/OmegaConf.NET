using System.Globalization;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OmegaConf.NET.Parsers;

/// <summary>
/// Parses YAML (<c>.yaml</c>, <c>.yml</c>) configuration files into an OmegaConf <see cref="ConfigObjectNode"/> AST,
/// preserving exact line and column numbers on every node and extracting key-suffix mutation operators (<c>+=</c>, <c>*=</c>, etc.).
/// </summary>
public class YamlFormatParser : IConfigFormatParser
{
    /// <inheritdoc />
    public bool CanParse(string extension)
    {
        return string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase);
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
            using var reader = new StringReader(content);
            var yamlStream = new YamlStream();
            yamlStream.Load(reader);

            if (yamlStream.Documents.Count == 0 || yamlStream.Documents[0].RootNode == null)
            {
                return new ConfigObjectNode(source: new SourceInfo(filePath, 0, 0));
            }

            var rootNode = yamlStream.Documents[0].RootNode;
            if (rootNode is not YamlMappingNode mapping)
            {
                throw new ConfigSyntaxException("Root of YAML configuration must be a mapping / object.", new SourceInfo(filePath, rootNode.Start.Line, rootNode.Start.Column));
            }

            return ConvertMapping(mapping, filePath, ConfigOperator.Assign);
        }
        catch (YamlException ex)
        {
            throw new ConfigSyntaxException(ex.Message, new SourceInfo(filePath, ex.Start.Line, ex.Start.Column), ex);
        }
    }

    /// <summary>
    /// Converts a <see cref="YamlMappingNode"/> into a <see cref="ConfigObjectNode"/>, extracting any operator suffix from each key.
    /// </summary>
    private ConfigObjectNode ConvertMapping(YamlMappingNode mapping, string? filePath, ConfigOperator op)
    {
        var objNode = new ConfigObjectNode(op, new SourceInfo(filePath, mapping.Start.Line, mapping.Start.Column));

        foreach (var entry in mapping.Children)
        {
            if (entry.Key is not YamlScalarNode keyScalar || keyScalar.Value == null)
            {
                continue;
            }

            // Extract operator suffix (e.g. "max_connections*=" -> cleanKey: "max_connections", keyOp: Multiply)
            var (cleanKey, keyOp) = OperatorParser.ParseKey(keyScalar.Value);
            var childNode = ConvertYamlNode(entry.Value, filePath, keyOp);
            objNode.SetChild(cleanKey, childNode);
        }

        return objNode;
    }

    private ConfigNode ConvertYamlNode(YamlNode node, string? filePath, ConfigOperator op)
    {
        var source = new SourceInfo(filePath, node.Start.Line, node.Start.Column);

        switch (node)
        {
            case YamlScalarNode scalar:
                return ConvertScalar(scalar, source, op);

            case YamlSequenceNode sequence:
                var listNode = new ConfigListNode(op: op, source: source);
                foreach (var item in sequence.Children)
                {
                    listNode.Items.Add(ConvertYamlNode(item, filePath, ConfigOperator.Assign));
                }
                return listNode;

            case YamlMappingNode mapping:
                return ConvertMapping(mapping, filePath, op);

            default:
                return new ConfigValueNode(null, op, source);
        }
    }

    /// <summary>
    /// Converts a <see cref="YamlScalarNode"/> into a typed <see cref="ConfigValueNode"/>.
    /// Single- or double-quoted scalars are preserved as strings; unquoted scalars are inferred as null, boolean, 64-bit integer, or double.
    /// </summary>
    private ConfigValueNode ConvertScalar(YamlScalarNode scalar, SourceInfo source, ConfigOperator op)
    {
        string? raw = scalar.Value;
        bool isQuoted = scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted;

        if (!isQuoted && (raw == null || raw == "null" || raw == "~"))
        {
            return new ConfigValueNode(null, op, source);
        }

        if (raw == ConfigValueNode.MissingMarker)
        {
            return new ConfigValueNode(ConfigValueNode.MissingMarker, op, source);
        }

        // Preserve explicit string type when the YAML value was enclosed in quotes (e.g. "12345" or 'true')
        if (isQuoted)
        {
            return new ConfigValueNode(raw ?? "", op, source);
        }

        // Try boolean
        if (bool.TryParse(raw, out bool boolVal))
        {
            return new ConfigValueNode(boolVal, op, source);
        }

        // Try 64-bit integer
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longVal))
        {
            return new ConfigValueNode(longVal, op, source);
        }

        // Try floating-point (when decimal point or scientific notation 'e'/'E' is present)
        if ((raw!.Contains('.') || raw.Contains('e', StringComparison.OrdinalIgnoreCase)) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleVal))
        {
            return new ConfigValueNode(doubleVal, op, source);
        }

        // Default to string
        return new ConfigValueNode(raw, op, source);
    }
}
