using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmegaConf.NET.Attributes;
using OmegaConf.NET.Binding;

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
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            seenKeys.Add(key);
        }

        // Also support positional record / constructor parameters when properties are get-only
        var primaryCtor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                              .OrderByDescending(c => c.GetParameters().Length)
                              .FirstOrDefault();

        if (primaryCtor != null && primaryCtor.GetParameters().Length > 0)
        {
            foreach (var param in primaryCtor.GetParameters())
            {
                string paramName = param.Name ?? "";
                if (string.IsNullOrEmpty(paramName)) continue;

                var matchingProp = type.GetProperty(paramName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (matchingProp?.GetCustomAttribute<ConfigIgnoreAttribute>() != null) continue;

                string key = param.GetCustomAttribute<ConfigKeyAttribute>()?.Key
                             ?? matchingProp?.GetCustomAttribute<ConfigKeyAttribute>()?.Key
                             ?? ToSnakeCase(paramName);

                if (seenKeys.Contains(key)) continue;

                bool isRequired = param.GetCustomAttribute<ConfigRequiredAttribute>() != null
                                  || matchingProp?.GetCustomAttribute<ConfigRequiredAttribute>() != null;

                if (isRequired)
                {
                    requiredNode.Add(key);
                }

                propertiesNode[key] = GeneratePropertySchema(param.ParameterType);
                seenKeys.Add(key);
            }
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

        if (propType == typeof(string) || propType == typeof(Guid) || propType == typeof(TimeSpan) ||
            propType == typeof(DateTime) || propType == typeof(DateTimeOffset) ||
            propType == typeof(DateOnly) || propType == typeof(TimeOnly) || propType == typeof(Uri))
        {
            schema["type"] = "string";
            return schema;
        }

        if (propType == typeof(bool))
        {
            schema["type"] = "boolean";
            return schema;
        }

        if (propType == typeof(int) || propType == typeof(long) || propType == typeof(short) || propType == typeof(byte) ||
            propType == typeof(uint) || propType == typeof(ulong) || propType == typeof(ushort) || propType == typeof(sbyte))
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

    private static string ToSnakeCase(string str) => ConfigBinder.ToSnakeCase(str);

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
            if (def == typeof(Dictionary<,>) || def == typeof(IDictionary<,>) || def == typeof(IReadOnlyDictionary<,>))
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
