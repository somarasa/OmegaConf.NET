using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmegaConf.NET.Attributes;

namespace OmegaConf.NET.Schema;

public static class JsonSchemaGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    /// <summary>
    /// Generates a JSON Schema (Draft-07) for a strongly typed configuration class.
    /// Used by VS Code, Visual Studio, and Rider for YAML/JSON auto-complete and intellisense.
    /// </summary>
    public static string GenerateSchema<T>() => GenerateSchema(typeof(T));

    public static string GenerateSchema(Type type)
    {
        var root = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = type.Name,
            ["type"] = "object"
        };

        var properties = new JsonObject();
        var requiredList = new JsonArray();

        PopulateProperties(type, properties, requiredList);

        root["properties"] = properties;
        if (requiredList.Count > 0)
        {
            root["required"] = requiredList;
        }

        return root.ToJsonString(JsonOptions);
    }

    public static void GenerateSchemaFile<T>(string filePath)
    {
        string schema = GenerateSchema<T>();
        File.WriteAllText(filePath, schema);
    }

    private static void PopulateProperties(Type type, JsonObject propertiesNode, JsonArray requiredNode)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.CanWrite && p.GetCustomAttribute<ConfigIgnoreAttribute>() == null);

        foreach (var prop in props)
        {
            string key = prop.GetCustomAttribute<ConfigKeyAttribute>()?.Key ?? ToSnakeCase(prop.Name);
            bool isRequired = prop.GetCustomAttribute<ConfigRequiredAttribute>() != null;

            if (isRequired)
            {
                requiredNode.Add(key);
            }

            var propSchema = GeneratePropertySchema(prop.PropertyType);
            propertiesNode[key] = propSchema;
        }
    }

    private static JsonObject GeneratePropertySchema(Type propType)
    {
        var schema = new JsonObject();

        var underlying = Nullable.GetUnderlyingType(propType);
        if (underlying != null)
        {
            return GeneratePropertySchema(underlying);
        }

        if (propType == typeof(string) || propType == typeof(Guid) || propType == typeof(TimeSpan))
        {
            schema["type"] = "string";
            return schema;
        }

        if (propType == typeof(bool))
        {
            schema["type"] = "boolean";
            return schema;
        }

        if (propType == typeof(int) || propType == typeof(long) || propType == typeof(short) || propType == typeof(byte))
        {
            schema["type"] = "integer";
            return schema;
        }

        if (propType == typeof(double) || propType == typeof(float) || propType == typeof(decimal))
        {
            schema["type"] = "number";
            return schema;
        }

        if (propType.IsEnum)
        {
            schema["type"] = "string";
            var enumArray = new JsonArray();
            foreach (var name in Enum.GetNames(propType))
            {
                enumArray.Add(name);
            }
            schema["enum"] = enumArray;
            return schema;
        }

        if (propType.IsArray)
        {
            schema["type"] = "array";
            schema["items"] = GeneratePropertySchema(propType.GetElementType()!);
            return schema;
        }

        if (IsGenericList(propType, out var itemType))
        {
            schema["type"] = "array";
            schema["items"] = GeneratePropertySchema(itemType!);
            return schema;
        }

        if (IsGenericDictionary(propType, out _, out var valType))
        {
            schema["type"] = "object";
            schema["additionalProperties"] = GeneratePropertySchema(valType!);
            return schema;
        }

        // Complex nested object
        schema["type"] = "object";
        var nestedProps = new JsonObject();
        var nestedReq = new JsonArray();
        PopulateProperties(propType, nestedProps, nestedReq);
        schema["properties"] = nestedProps;
        if (nestedReq.Count > 0)
        {
            schema["required"] = nestedReq;
        }

        return schema;
    }

    private static string ToSnakeCase(string str)
    {
        if (string.IsNullOrEmpty(str)) return str;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && str[i - 1] != '_') sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static bool IsGenericList(Type type, out Type? itemType)
    {
        itemType = null;
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>) || def == typeof(IEnumerable<>))
            {
                itemType = type.GetGenericArguments()[0];
                return true;
            }
        }
        return false;
    }

    private static bool IsGenericDictionary(Type type, out Type? keyType, out Type? valType)
    {
        keyType = null;
        valType = null;
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>))
            {
                var args = type.GetGenericArguments();
                keyType = args[0];
                valType = args[1];
                return true;
            }
        }
        return false;
    }
}
