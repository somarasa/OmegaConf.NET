using System.Text.Json;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;
using OmegaConf.NET.Operators;

namespace OmegaConf.NET.Parsers;

/// <summary>
/// Parses JSON (<c>.json</c>) and JSON with Comments (<c>.jsonc</c>) configuration files into an OmegaConf <see cref="ConfigObjectNode"/> AST.
/// Automatically skips single-line/multi-line comments and permits trailing commas.
/// </summary>
public class JsonFormatParser : IConfigFormatParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <inheritdoc />
    public bool CanParse(string extension)
    {
        return string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".jsonc", StringComparison.OrdinalIgnoreCase);
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
            using var doc = JsonDocument.Parse(content, Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ConfigSyntaxException("Root of JSON configuration must be an object.", new SourceInfo(filePath, 0, 0));
            }

            return ConvertObject(doc.RootElement, filePath, ConfigOperator.Assign);
        }
        catch (JsonException ex)
        {
            throw new ConfigSyntaxException(ex.Message, new SourceInfo(filePath, (int)(ex.LineNumber ?? 0), (int)(ex.BytePositionInLine ?? 0)), ex);
        }
    }

    private ConfigObjectNode ConvertObject(JsonElement element, string? filePath, ConfigOperator op)
    {
        var objNode = new ConfigObjectNode(op, new SourceInfo(filePath, 0, 0));

        foreach (var prop in element.EnumerateObject())
        {
            var (cleanKey, keyOp) = OperatorParser.ParseKey(prop.Name);
            var child = ConvertElement(prop.Value, filePath, keyOp);
            objNode.SetChild(cleanKey, child);
        }

        return objNode;
    }

    private ConfigNode ConvertElement(JsonElement element, string? filePath, ConfigOperator op)
    {
        var source = new SourceInfo(filePath, 0, 0);

        return element.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(element, filePath, op),
            JsonValueKind.Array => ConvertArray(element, filePath, op),
            JsonValueKind.String => ConvertString(element.GetString(), source, op),
            JsonValueKind.Number => ConvertNumber(element, source, op),
            JsonValueKind.True => new ConfigValueNode(true, op, source),
            JsonValueKind.False => new ConfigValueNode(false, op, source),
            JsonValueKind.Null => new ConfigValueNode(null, op, source),
            _ => new ConfigValueNode(null, op, source)
        };
    }

    private ConfigListNode ConvertArray(JsonElement element, string? filePath, ConfigOperator op)
    {
        var listNode = new ConfigListNode(op: op, source: new SourceInfo(filePath, 0, 0));
        foreach (var item in element.EnumerateArray())
        {
            listNode.Items.Add(ConvertElement(item, filePath, ConfigOperator.Assign));
        }
        return listNode;
    }

    private ConfigValueNode ConvertString(string? val, SourceInfo source, ConfigOperator op)
    {
        if (val == ConfigValueNode.MissingMarker)
        {
            return new ConfigValueNode(ConfigValueNode.MissingMarker, op, source);
        }

        return new ConfigValueNode(val, op, source);
    }

    private ConfigValueNode ConvertNumber(JsonElement element, SourceInfo source, ConfigOperator op)
    {
        if (element.TryGetInt64(out long l))
        {
            return new ConfigValueNode(l, op, source);
        }

        if (element.TryGetDouble(out double d))
        {
            return new ConfigValueNode(d, op, source);
        }

        return new ConfigValueNode(element.GetRawText(), op, source);
    }
}
