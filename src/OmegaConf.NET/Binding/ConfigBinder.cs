using System.Collections;
using System.Globalization;
using System.Reflection;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Attributes;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Binding;

/// <summary>
/// Binds a resolved <see cref="ConfigNode"/> AST into strongly typed C# POCOs, positional <c>record</c> types,
/// collections, dictionaries, enums, and rich scalar types (<see cref="TimeSpan"/>, <see cref="DateTimeOffset"/>,
/// <see cref="DateOnly"/>, <see cref="TimeOnly"/>, <see cref="Guid"/>, <see cref="Uri"/>).
/// </summary>
public static class ConfigBinder
{
    /// <summary>
    /// Hydra-style key (<c>"_target_"</c>) used in configuration objects to specify an assembly-qualified runtime type for polymorphic instantiation.
    /// </summary>
    public const string TargetKey = "_target_";

    /// <summary>
    /// Hydra-style key (<c>"_args_"</c>) used in configuration objects to pass positional constructor arguments during instantiation.
    /// </summary>
    public const string ArgsKey = "_args_";

    /// <summary>
    /// Hydra-style key (<c>"_partial_"</c>) used in configuration objects to indicate lazy/partial factory instantiation (<see cref="Func{TResult}"/>).
    /// </summary>
    public const string PartialKey = "_partial_";

    /// <summary>
    /// Hydra-style key (<c>"_recursive_"</c>) used in configuration objects to control whether nested <c>_target_</c> objects are recursively instantiated.
    /// </summary>
    public const string RecursiveKey = "_recursive_";

    private static readonly MethodInfo CreateFactoryMethod =
        typeof(ConfigBinder).GetMethod(nameof(CreateTypedFactory), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Dynamically instantiates an object (or <see cref="Func{TResult}"/> factory when <c>_partial_: true</c>) from a configuration node containing <c>_target_</c>.
    /// Equivalent to Python's <c>hydra.utils.instantiate(cfg, **overrides)</c>.
    /// </summary>
    /// <typeparam name="T">The expected return type (or <see cref="object"/>).</typeparam>
    /// <param name="node">The configuration node containing <c>_target_</c>.</param>
    /// <param name="overrides">Optional call-site parameter overrides dictionary.</param>
    public static T Instantiate<T>(ConfigNode node, IDictionary<string, object?>? overrides = null)
    {
        if (overrides == null || overrides.Count == 0 || node is not ConfigObjectNode objNode)
        {
            return Bind<T>(node);
        }

        var merged = (ConfigObjectNode)objNode.Clone();
        foreach (var (k, v) in overrides)
        {
            merged.SetChild(k, ConvertOverrideToNode(v), allowNewKeyInStruct: true);
        }
        return Bind<T>(merged);
    }

    /// <summary>
    /// Dynamically instantiates an object from a configuration node containing <c>_target_</c>,
    /// applying call-site parameter overrides from an anonymous object (e.g. <c>new { Port = 9090 }</c>).
    /// </summary>
    /// <typeparam name="T">The expected return type (or <see cref="object"/>).</typeparam>
    /// <param name="node">The configuration node containing <c>_target_</c>.</param>
    /// <param name="anonymousOverrides">An object whose public readable properties override keys in <paramref name="node"/>.</param>
    public static T Instantiate<T>(ConfigNode node, object anonymousOverrides)
    {
        if (anonymousOverrides is IDictionary<string, object?> dict)
        {
            return Instantiate<T>(node, dict);
        }

        var overrideDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (anonymousOverrides != null)
        {
            foreach (var prop in anonymousOverrides.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.CanRead)
                {
                    overrideDict[prop.Name] = prop.GetValue(anonymousOverrides);
                }
            }
        }
        return Instantiate<T>(node, overrideDict);
    }

    private static ConfigNode ConvertOverrideToNode(object? value)
    {
        if (value is ConfigNode cn) return cn.Clone();
        if (value is int i) return new ConfigValueNode((long)i);
        return new ConfigValueNode(value);
    }

    /// <summary>
    /// Binds a <see cref="ConfigNode"/> to a strongly typed C# instance of <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Target C# class, struct, positional record, or <see cref="Func{TResult}"/> factory type.</typeparam>
    /// <param name="node">The resolved AST node to bind from.</param>
    public static T Bind<T>(ConfigNode node)
    {
        return (T)Bind(node, typeof(T), "");
    }

    /// <summary>
    /// Binds a <see cref="ConfigNode"/> to the specified runtime <paramref name="targetType"/>.
    /// </summary>
    /// <param name="node">The resolved AST node to bind from.</param>
    /// <param name="targetType">The target CLR <see cref="Type"/>.</param>
    /// <param name="currentPath">Dot-separated configuration path used for diagnostic error reporting.</param>
    public static object Bind(ConfigNode node, Type targetType, string currentPath = "")
    {
        // 0. Handle raw ConfigNode / ConfigObjectNode / ConfigListNode passthrough
        if (typeof(ConfigNode).IsAssignableFrom(targetType))
        {
            return node.Clone();
        }

        // 0a. Handle Func<TResult> factory delegate (Hydra _partial_ / lazy factory instantiation)
        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Func<>))
        {
            Type resultType = targetType.GetGenericArguments()[0];
            return CreateFactoryMethod.MakeGenericMethod(resultType).Invoke(null, [node.Clone(), currentPath])!;
        }

        // 0b. Handle System.Object targetType (untyped dynamic values, lists, dictionaries, or _target_ objects)
        if (targetType == typeof(object))
        {
            if (node is ConfigValueNode objVn)
            {
                if (objVn.IsMissingMarker)
                {
                    throw new MissingValueMarkerException(currentPath, objVn.Source);
                }
                return objVn.Value!;
            }

            if (node is ConfigListNode)
            {
                return BindList(node, typeof(List<object>), typeof(object), currentPath);
            }

            if (node is ConfigObjectNode dynObj)
            {
                if (dynObj.TryGetChild(PartialKey, out var partialVal) &&
                    partialVal is ConfigValueNode { Value: true })
                {
                    var clonedWithoutPartial = (ConfigObjectNode)dynObj.Clone();
                    clonedWithoutPartial.RemoveChild(PartialKey);
                    return new Func<object>(() => Bind(clonedWithoutPartial, typeof(object), currentPath));
                }

                if (dynObj.TryGetChild(TargetKey, out _))
                {
                    return BindObject(dynObj, typeof(object), currentPath);
                }

                return BindDictionary(dynObj, typeof(Dictionary<string, object>), typeof(string), typeof(object), currentPath);
            }
        }

        // 1. Handle Nullable<T> value types
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

        // 2. Handle string
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

        // 3. Handle Primitives, Enums, and Rich Built-In Scalar Types
        if (targetType.IsPrimitive || targetType.IsEnum || targetType == typeof(decimal) ||
            targetType == typeof(DateTime) || targetType == typeof(DateTimeOffset) ||
            targetType == typeof(DateOnly) || targetType == typeof(TimeOnly) ||
            targetType == typeof(TimeSpan) || targetType == typeof(Guid) || targetType == typeof(Uri))
        {
            return BindScalar(node, targetType, currentPath);
        }

        // 4. Handle Arrays (T[])
        if (targetType.IsArray)
        {
            return BindArray(node, targetType, currentPath);
        }

        // 5. Handle Generic Lists (List<T>, IList<T>, IReadOnlyList<T>, IEnumerable<T>)
        if (IsGenericList(targetType, out var itemType))
        {
            return BindList(node, targetType, itemType!, currentPath);
        }

        // 6. Handle Generic Dictionaries (Dictionary<string, T>, IDictionary<string, T>, IReadOnlyDictionary<string, T>)
        if (IsGenericDictionary(targetType, out var dictKeyType, out var dictValType))
        {
            return BindDictionary(node, targetType, dictKeyType!, dictValType!, currentPath);
        }

        // 7. Handle Complex Object / POCO / Positional Record
        return BindObject(node, targetType, currentPath);
    }

    /// <summary>
    /// Converts a <see cref="ConfigValueNode"/> scalar value to <paramref name="targetType"/> using invariant culture rules.
    /// </summary>
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

    /// <summary>
    /// Instantiates a POCO or positional <c>record</c> (respecting polymorphic <c>_target_</c> if specified)
    /// and binds all matching configuration keys onto its constructor parameters and writable properties.
    /// </summary>
    private static object BindObject(ConfigNode node, Type targetType, string currentPath)
    {
        if (node is not ConfigObjectNode objNode)
        {
            throw new ConfigTypeMismatchException(currentPath, targetType, node.ToString(), node.Source);
        }

        // Check for dynamic polymorphic _target_ type override
        Type concreteType = targetType;
        if (objNode.TryGetChild(TargetKey, out var targetValNode) && targetValNode is ConfigValueNode tvn && tvn.Value is string targetTypeName)
        {
            var resolvedType = ResolveTargetType(targetTypeName);
            if (resolvedType != null && targetType.IsAssignableFrom(resolvedType))
            {
                concreteType = resolvedType;
            }
        }

        // Check for Hydra _recursive_: false directive
        bool isRecursive = true;
        if (objNode.TryGetChild(RecursiveKey, out var recNode) && recNode is ConfigValueNode { Value: false })
        {
            isRecursive = false;
        }

        ConfigObjectNode effectiveObjNode = isRecursive ? objNode : CreateNonRecursiveCopy(objNode);

        // Instantiate via parameterless constructor (POCO) or parameterized primary constructor (positional record)
        var boundParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object instance = CreateInstanceOrRecord(concreteType, effectiveObjNode, currentPath, boundParameterNames);

        // Bind remaining writable properties that were not already populated by a primary constructor
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
            ConfigNode? childNode = FindMatchingChild(effectiveObjNode, prop.Name, explicitKey);

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

    private static Func<TResult> CreateTypedFactory<TResult>(ConfigNode clonedNode, string currentPath)
    {
        return () => (TResult)Bind(clonedNode.Clone(), typeof(TResult), currentPath);
    }

    private static Type? ResolveTargetType(string targetTypeName)
    {
        var type = Type.GetType(targetTypeName, throwOnError: false);
        if (type != null) return type;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = asm.GetType(targetTypeName, throwOnError: false, ignoreCase: false);
            if (type != null) return type;
        }

        return null;
    }

    private static ConfigObjectNode CreateNonRecursiveCopy(ConfigObjectNode source)
    {
        var copy = (ConfigObjectNode)source.Clone();
        foreach (var (_, child) in copy.Children)
        {
            StripRecursiveTargets(child);
        }
        return copy;
    }

    private static void StripRecursiveTargets(ConfigNode node)
    {
        if (node is ConfigObjectNode obj)
        {
            obj.RemoveChild(TargetKey);
            foreach (var (_, child) in obj.Children)
            {
                StripRecursiveTargets(child);
            }
        }
        else if (node is ConfigListNode list)
        {
            foreach (var item in list.Items)
            {
                StripRecursiveTargets(item);
            }
        }
    }

    /// <summary>
    /// Creates an instance of <paramref name="concreteType"/>:
    /// - If <c>_args_</c> is present in <paramref name="objNode"/>, binds positional constructor arguments from <c>_args_</c> (and any remaining parameters by name).
    /// - Otherwise uses the parameterless constructor if available;
    /// - Otherwise binds arguments to the public constructor with the most parameters (supporting C# positional records).
    /// </summary>
    private static object CreateInstanceOrRecord(
        Type concreteType,
        ConfigObjectNode objNode,
        string currentPath,
        HashSet<string> boundParameterNames)
    {
        var constructors = concreteType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        // Check for Hydra _args_: [arg0, arg1, ...] positional constructor arguments
        ConfigListNode? positionalArgsList = null;
        if (objNode.TryGetChild(ArgsKey, out var rawArgsNode) && rawArgsNode is ConfigListNode listNode && listNode.Items.Count > 0)
        {
            positionalArgsList = listNode;
        }

        if (positionalArgsList == null)
        {
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
        }

        // Select best matching parameterized constructor
        ConstructorInfo? targetCtor;
        if (positionalArgsList != null)
        {
            int posCount = positionalArgsList.Items.Count;
            targetCtor = constructors
                .Where(c => c.GetParameters().Length >= posCount)
                .OrderBy(c => c.GetParameters().Length == posCount ? 0 : 1)
                .ThenByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();
        }
        else
        {
            targetCtor = constructors.OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        }

        if (targetCtor == null)
        {
            throw new InvalidOperationException($"No matching public constructor found for type '{concreteType.FullName}'.");
        }

        var parameters = targetCtor.GetParameters();
        var args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            string paramName = param.Name ?? $"arg{i}";
            string paramPath = string.IsNullOrEmpty(currentPath) ? paramName : $"{currentPath}.{paramName}";

            // 1. If covered by positional _args_, bind from _args_[i]
            if (positionalArgsList != null && i < positionalArgsList.Items.Count)
            {
                args[i] = Bind(positionalArgsList.Items[i], param.ParameterType, $"{currentPath}._args_[{i}]");
                // Only mark as bound if not explicitly overridden by a named property in objNode
                if (FindMatchingChild(objNode, paramName) == null)
                {
                    boundParameterNames.Add(paramName);
                }
                continue;
            }

            // 2. Otherwise bind constructor parameter by name/attribute from objNode
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

        return targetCtor.Invoke(args);
    }

    /// <summary>
    /// Searches <paramref name="objNode"/> for a child matching a C# property/parameter name using the following precedence:
    /// 1. Explicit <see cref="ConfigKeyAttribute"/> key
    /// 2. Exact C# member name (case-insensitive)
    /// 3. <c>snake_case</c> conversion (e.g. <c>AppName</c> -> <c>app_name</c>, <c>SSLPort</c> -> <c>ssl_port</c>)
    /// 4. <c>kebab-case</c> conversion (e.g. <c>AppName</c> -> <c>app-name</c>)
    /// 5. <c>camelCase</c> conversion (e.g. <c>AppName</c> -> <c>appName</c>)
    /// </summary>
    private static ConfigNode? FindMatchingChild(ConfigObjectNode objNode, string memberName, string? explicitKey = null)
    {
        // 1. Explicit [ConfigKey("...")]
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

    /// <summary>
    /// Converts a <c>PascalCase</c> or <c>camelCase</c> identifier into <c>snake_case</c>,
    /// keeping multi-letter uppercase acronyms grouped together (e.g. <c>"SSLPort"</c> -> <c>"ssl_port"</c>,
    /// <c>"HTTPSEndpoint"</c> -> <c>"https_endpoint"</c>, <c>"MaxDBConnections"</c> -> <c>"max_db_connections"</c>).
    /// </summary>
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
                    // Insert an underscore before an uppercase letter if:
                    // - the previous character was lowercase or a digit (e.g. "App|Name", "OAuth2|Client"), OR
                    // - the next character is lowercase, marking the end of an acronym prefix (e.g. "SSL|Port")
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
