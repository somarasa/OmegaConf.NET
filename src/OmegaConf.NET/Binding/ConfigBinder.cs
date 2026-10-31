using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Attributes;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Binding;

public static class ConfigBinder
{
    public const string TargetKey = "_target_";

    /// <summary>
    /// Binds a ConfigNode to a strongly typed C# object.
    /// </summary>
    public static T Bind<T>(ConfigNode node)
    {
        return (T)Bind(node, typeof(T), "");
    }

    /// <summary>
    /// Binds a ConfigNode to a specific Type.
    /// </summary>
    public static object Bind(ConfigNode node, Type targetType, string currentPath = "")
    {
        // Handle Nullable<T>
        var underlyingType = Nullable.GetUnderlyingType(targetType);
        if (underlyingType != null)
        {
            if (node is ConfigValueNode vn && (vn.Value == null || vn.IsMissingMarker))
            {
                if (vn.IsMissingMarker)
                {
                    throw new MissingValueMarkerException(currentPath, vn.Source);
                }
                return null!;
            }
            return Bind(node, underlyingType, currentPath);
        }

        // Handle string
        if (targetType == typeof(string))
        {
            if (node is ConfigValueNode vn)
            {
                if (vn.IsMissingMarker)
                {
                    throw new MissingValueMarkerException(currentPath, vn.Source);
                }
                return vn.Value?.ToString() ?? "";
            }
            return node.ToString() ?? "";
        }

        // Handle Primitives and Enums
        if (targetType.IsPrimitive || targetType.IsEnum || targetType == typeof(decimal) ||
            targetType == typeof(DateTime) || targetType == typeof(DateTimeOffset) ||
            targetType == typeof(DateOnly) || targetType == typeof(TimeOnly) ||
            targetType == typeof(TimeSpan) || targetType == typeof(Guid) || targetType == typeof(Uri))
        {
            return BindScalar(node, targetType, currentPath);
        }

        // Handle Arrays
        if (targetType.IsArray)
        {
            return BindArray(node, targetType, currentPath);
        }

        // Handle Generic Lists (List<T>, IList<T>, IReadOnlyList<T>, IEnumerable<T>)
        if (IsGenericList(targetType, out var itemType))
        {
            return BindList(node, targetType, itemType!, currentPath);
        }

        // Handle Dictionaries (Dictionary<string, T>, IDictionary<string, T>)
        if (IsGenericDictionary(targetType, out var dictKeyType, out var dictValType))
        {
            return BindDictionary(node, targetType, dictKeyType!, dictValType!, currentPath);
        }

        // Handle Complex Object / POCO / Record
        return BindObject(node, targetType, currentPath);
    }

    private static object BindScalar(ConfigNode node, Type targetType, string currentPath)
    {
        if (node is not ConfigValueNode vn)
        {
            throw new ConfigTypeMismatchException(currentPath, targetType, node.ToString(), node.Source);
        }

        if (vn.IsMissingMarker)
        {
            throw new MissingValueMarkerException(currentPath, vn.Source);
        }

        object? val = vn.Value;
        if (val == null)
        {
            if (targetType.IsValueType)
            {
                throw new ConfigTypeMismatchException(currentPath, targetType, "null", vn.Source);
            }
            return null!;
        }

        try
        {
            if (targetType.IsEnum)
            {
                string strVal = val.ToString()!;
                return Enum.Parse(targetType, strVal, ignoreCase: true);
            }

            if (targetType == typeof(TimeSpan) && val is string sTime)
            {
                return TimeSpan.Parse(sTime, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(Guid) && val is string sGuid)
            {
                return Guid.Parse(sGuid);
            }

            if (targetType == typeof(DateTimeOffset) && val is string sDto)
            {
                return DateTimeOffset.Parse(sDto, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(DateOnly) && val is string sDate)
            {
                return DateOnly.Parse(sDate, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(TimeOnly) && val is string sTimeOnly)
            {
                return TimeOnly.Parse(sTimeOnly, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(Uri) && val is string sUri)
            {
                return new Uri(sUri, UriKind.RelativeOrAbsolute);
            }

            return Convert.ChangeType(val, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            throw new ConfigTypeMismatchException(currentPath, targetType, val, vn.Source, ex);
        }
    }

    private static object BindObject(ConfigNode node, Type targetType, string currentPath)
    {
        if (node is not ConfigObjectNode objNode)
        {
            throw new ConfigTypeMismatchException(currentPath, targetType, node.ToString(), node.Source);
        }

        // Check for dynamic _target_ type
        Type concreteType = targetType;
        if (objNode.TryGetChild(TargetKey, out var targetValNode) && targetValNode is ConfigValueNode tvn && tvn.Value is string targetTypeName)
        {
            var resolvedType = Type.GetType(targetTypeName, throwOnError: false);
            if (resolvedType != null && targetType.IsAssignableFrom(resolvedType))
            {
                concreteType = resolvedType;
            }
        }

        var boundParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object instance = CreateInstanceOrRecord(concreteType, objNode, currentPath, boundParameterNames);

        var properties = concreteType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                     .Where(p => p.CanWrite && p.GetCustomAttribute<ConfigIgnoreAttribute>() == null);

        foreach (var prop in properties)
        {
            if (boundParameterNames.Contains(prop.Name))
            {
                continue;
            }

            string propPath = string.IsNullOrEmpty(currentPath) ? prop.Name : $"{currentPath}.{prop.Name}";
            bool isRequired = prop.GetCustomAttribute<ConfigRequiredAttribute>() != null;

            var explicitKey = prop.GetCustomAttribute<ConfigKeyAttribute>()?.Key;
            ConfigNode? childNode = FindMatchingChild(objNode, prop.Name, explicitKey);

            if (childNode != null)
            {
                if (childNode is ConfigValueNode cvn && cvn.IsMissingMarker)
                {
                    if (isRequired)
                    {
                        throw new MissingRequiredConfigException(propPath, concreteType, cvn.Source);
                    }
                    throw new MissingValueMarkerException(propPath, cvn.Source);
                }

                object propValue = Bind(childNode, prop.PropertyType, propPath);
                prop.SetValue(instance, propValue);
            }
            else
            {
                if (isRequired)
                {
                    throw new MissingRequiredConfigException(propPath, concreteType, objNode.Source);
                }
            }
        }

        return instance;
    }

    private static object CreateInstanceOrRecord(
        Type concreteType,
        ConfigObjectNode objNode,
        string currentPath,
        HashSet<string> boundParameterNames)
    {
        var constructors = concreteType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var defaultCtor = constructors.FirstOrDefault(c => c.GetParameters().Length == 0);
        if (defaultCtor != null)
        {
            try
            {
                return Activator.CreateInstance(concreteType)
                    ?? throw new InvalidOperationException($"Could not instantiate type '{concreteType.FullName}'.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to create instance of type '{concreteType.FullName}': {ex.Message}", ex);
            }
        }

        // Support positional records / primary constructors
        var primaryCtor = constructors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (primaryCtor == null)
        {
            throw new InvalidOperationException($"No public constructor found for type '{concreteType.FullName}'.");
        }

        var parameters = primaryCtor.GetParameters();
        var args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            string paramName = param.Name ?? $"arg{i}";
            string paramPath = string.IsNullOrEmpty(currentPath) ? paramName : $"{currentPath}.{paramName}";

            var matchingProp = concreteType.GetProperty(paramName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var explicitKey = param.GetCustomAttribute<ConfigKeyAttribute>()?.Key
                              ?? matchingProp?.GetCustomAttribute<ConfigKeyAttribute>()?.Key;
            bool isRequired = param.GetCustomAttribute<ConfigRequiredAttribute>() != null
                              || matchingProp?.GetCustomAttribute<ConfigRequiredAttribute>() != null;

            ConfigNode? childNode = FindMatchingChild(objNode, paramName, explicitKey);

            if (childNode != null)
            {
                if (childNode is ConfigValueNode cvn && cvn.IsMissingMarker)
                {
                    if (isRequired)
                    {
                        throw new MissingRequiredConfigException(paramPath, concreteType, cvn.Source);
                    }
                    throw new MissingValueMarkerException(paramPath, cvn.Source);
                }

                args[i] = Bind(childNode, param.ParameterType, paramPath);
                boundParameterNames.Add(paramName);
            }
            else if (isRequired)
            {
                throw new MissingRequiredConfigException(paramPath, concreteType, objNode.Source);
            }
            else if (param.HasDefaultValue)
            {
                args[i] = param.DefaultValue;
                boundParameterNames.Add(paramName);
            }
            else
            {
                args[i] = param.ParameterType.IsValueType ? Activator.CreateInstance(param.ParameterType) : null;
                boundParameterNames.Add(paramName);
            }
        }

        return primaryCtor.Invoke(args);
    }

    private static ConfigNode? FindMatchingChild(ConfigObjectNode objNode, string memberName, string? explicitKey = null)
    {
        // 1. Explicit ConfigKeyAttribute
        if (!string.IsNullOrEmpty(explicitKey))
        {
            return objNode.GetChild(explicitKey);
        }

        // 2. Direct property name
        if (objNode.TryGetChild(memberName, out var exact))
        {
            return exact;
        }

        // 3. snake_case (e.g. AppName -> app_name, SSLPort -> ssl_port)
        string snake = ToSnakeCase(memberName);
        if (objNode.TryGetChild(snake, out var snakeNode))
        {
            return snakeNode;
        }

        // 4. kebab-case (e.g. AppName -> app-name)
        string kebab = ToKebabCase(memberName);
        if (objNode.TryGetChild(kebab, out var kebabNode))
        {
            return kebabNode;
        }

        // 5. camelCase (e.g. AppName -> appName)
        string camel = char.ToLowerInvariant(memberName[0]) + memberName[1..];
        if (objNode.TryGetChild(camel, out var camelNode))
        {
            return camelNode;
        }

        return null;
    }

    public static string ToSnakeCase(string str)
    {
        if (string.IsNullOrEmpty(str)) return str;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && str[i - 1] != '_')
                {
                    bool prevIsLowerOrDigit = char.IsLower(str[i - 1]) || char.IsDigit(str[i - 1]);
                    bool nextIsLower = i + 1 < str.Length && char.IsLower(str[i + 1]);
                    if (prevIsLowerOrDigit || nextIsLower)
                    {
                        sb.Append('_');
                    }
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string ToKebabCase(string str)
    {
        return ToSnakeCase(str).Replace('_', '-');
    }

    private static object BindArray(ConfigNode node, Type arrayType, string currentPath)
    {
        if (node is not ConfigListNode listNode)
        {
            throw new ConfigTypeMismatchException(currentPath, arrayType, node.ToString(), node.Source);
        }

        var elemType = arrayType.GetElementType()!;
        var array = Array.CreateInstance(elemType, listNode.Items.Count);

        for (int i = 0; i < listNode.Items.Count; i++)
        {
            string itemPath = $"{currentPath}[{i}]";
            object elemValue = Bind(listNode.Items[i], elemType, itemPath);
            array.SetValue(elemValue, i);
        }

        return array;
    }

    private static object BindList(ConfigNode node, Type targetType, Type itemType, string currentPath)
    {
        if (node is not ConfigListNode listNode)
        {
            throw new ConfigTypeMismatchException(currentPath, targetType, node.ToString(), node.Source);
        }

        var concreteListType = typeof(List<>).MakeGenericType(itemType);
        var list = (IList)Activator.CreateInstance(concreteListType)!;

        for (int i = 0; i < listNode.Items.Count; i++)
        {
            string itemPath = $"{currentPath}[{i}]";
            object itemVal = Bind(listNode.Items[i], itemType, itemPath);
            list.Add(itemVal);
        }

        return list;
    }

    private static object BindDictionary(ConfigNode node, Type targetType, Type keyType, Type valType, string currentPath)
    {
        if (node is not ConfigObjectNode objNode)
        {
            throw new ConfigTypeMismatchException(currentPath, targetType, node.ToString(), node.Source);
        }

        if (keyType != typeof(string))
        {
            throw new NotSupportedException($"Only string dictionary keys are supported, got '{keyType.Name}'.");
        }

        var concreteDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valType);
        var dict = (IDictionary)Activator.CreateInstance(concreteDictType)!;

        foreach (var (k, childNode) in objNode.Children)
        {
            string childPath = string.IsNullOrEmpty(currentPath) ? k : $"{currentPath}.{k}";
            object childVal = Bind(childNode, valType, childPath);
            dict.Add(k, childVal);
        }

        return dict;
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
