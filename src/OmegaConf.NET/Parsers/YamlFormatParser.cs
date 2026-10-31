using System.Globalization;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OmegaConf.NET.Parsers;

public class YamlFormatParser : IConfigFormatParser
{
    public bool CanParse(string extension)
    {
        return string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase);
    }

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

    private ConfigObjectNode ConvertMapping(YamlMappingNode mapping, string? filePath, ConfigOperator op)
    {
        var objNode = new ConfigObjectNode(op, new SourceInfo(filePath, mapping.Start.Line, mapping.Start.Column));

        foreach (var entry in mapping.Children)
        {
            if (entry.Key is not YamlScalarNode keyScalar || keyScalar.Value == null)
            {
                continue;
            }

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

        if (isQuoted)
        {
            return new ConfigValueNode(raw ?? "", op, source);
        }

        // Try boolean
        if (bool.TryParse(raw, out bool boolVal))
        {
            return new ConfigValueNode(boolVal, op, source);
        }

        // Try integer
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longVal))
        {
            return new ConfigValueNode(longVal, op, source);
        }

        // Try float / double (only if has decimal point or exponential to avoid false positives)
        if ((raw!.Contains('.') || raw.Contains('e', StringComparison.OrdinalIgnoreCase)) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleVal))
        {
            return new ConfigValueNode(doubleVal, op, source);
        }

        // Default to string
        return new ConfigValueNode(raw, op, source);
    }
}
