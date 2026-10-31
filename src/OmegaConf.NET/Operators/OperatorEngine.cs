using System.Globalization;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Operators;

public static class OperatorEngine
{
    /// <summary>
    /// Merges an override node onto a base node using the override node's Operator.
    /// </summary>
    public static ConfigNode Apply(ConfigNode? baseNode, ConfigNode overrideNode)
    {
        if (baseNode == null)
        {
            return overrideNode.Clone();
        }

        return overrideNode.Operator switch
        {
            ConfigOperator.Assign => ApplyAssign(baseNode, overrideNode),
            ConfigOperator.Add => ApplyAdd(baseNode, overrideNode),
            ConfigOperator.Subtract => ApplySubtract(baseNode, overrideNode),
            ConfigOperator.Multiply => ApplyMultiply(baseNode, overrideNode),
            ConfigOperator.Divide => ApplyDivide(baseNode, overrideNode),
            ConfigOperator.BitwiseAnd => ApplyBitwiseAnd(baseNode, overrideNode),
            ConfigOperator.BitwiseOr => ApplyBitwiseOr(baseNode, overrideNode),
            ConfigOperator.Toggle => ApplyToggle(baseNode, overrideNode),
            _ => throw new InvalidOperatorException(overrideNode.Operator.ToString(), baseNode.GetType().Name, "Unknown operator", overrideNode.Source)
        };
    }

    private static ConfigNode ApplyAssign(ConfigNode baseNode, ConfigNode overrideNode)
    {
        // When both are objects, recursively merge their children (hierarchical merge)
        if (baseNode is ConfigObjectNode baseObj && overrideNode is ConfigObjectNode overrideObj)
        {
            var result = (ConfigObjectNode)baseObj.Clone();
            result.Source = overrideNode.Source;

            foreach (var (key, child) in overrideObj.Children)
            {
                if (result.TryGetChild(key, out var existing))
                {
                    result.SetChild(key, Apply(existing, child));
                }
                else
                {
                    result.SetChild(key, child.Clone());
                }
            }

            return result;
        }

        // Otherwise scalar or list replaces the base
        return overrideNode.Clone();
    }

    private static ConfigNode ApplyAdd(ConfigNode baseNode, ConfigNode overrideNode)
    {
        // List append
        if (baseNode is ConfigListNode baseList)
        {
            var result = (ConfigListNode)baseList.Clone();
            result.Source = overrideNode.Source;

            if (overrideNode is ConfigListNode overrideList)
            {
                foreach (var item in overrideList.Items)
                {
                    result.Items.Add(item.Clone());
                }
            }
            else
            {
                result.Items.Add(overrideNode.Clone());
            }

            return result;
        }

        // Object merge
        if (baseNode is ConfigObjectNode baseObj && overrideNode is ConfigObjectNode overrideObj)
        {
            return ApplyAssign(baseObj, overrideObj);
        }

        // Scalar add
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            if (TryPerformNumericOp(baseVal.Value, overrideVal.Value, (a, b) => a + b, (a, b) => a + b, out var numResult))
            {
                return new ConfigValueNode(numResult, ConfigOperator.Assign, overrideNode.Source);
            }

            // String concatenation
            string strA = baseVal.Value?.ToString() ?? "";
            string strB = overrideVal.Value?.ToString() ?? "";
            return new ConfigValueNode(strA + strB, ConfigOperator.Assign, overrideNode.Source);
        }

        throw new InvalidOperatorException("+=", baseNode.GetType().Name, $"Cannot add {overrideNode.GetType().Name} to {baseNode.GetType().Name}.", overrideNode.Source);
    }

    private static ConfigNode ApplySubtract(ConfigNode baseNode, ConfigNode overrideNode)
    {
        // List item removal
        if (baseNode is ConfigListNode baseList)
        {
            var result = (ConfigListNode)baseList.Clone();
            result.Source = overrideNode.Source;

            var itemsToRemove = overrideNode is ConfigListNode overrideList
                ? overrideList.Items
                : new List<ConfigNode> { overrideNode };

            foreach (var toRemove in itemsToRemove)
            {
                string removeStr = toRemove.ToString() ?? "";
                result.Items.RemoveAll(item => (item.ToString() ?? "") == removeStr);
            }

            return result;
        }

        // Numeric subtraction
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            if (TryPerformNumericOp(baseVal.Value, overrideVal.Value, (a, b) => a - b, (a, b) => a - b, out var numResult))
            {
                return new ConfigValueNode(numResult, ConfigOperator.Assign, overrideNode.Source);
            }
        }

        throw new InvalidOperatorException("-=", baseNode.GetType().Name, $"Cannot subtract {overrideNode.GetType().Name} from {baseNode.GetType().Name}.", overrideNode.Source);
    }

    private static ConfigNode ApplyMultiply(ConfigNode baseNode, ConfigNode overrideNode)
    {
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            // Numeric multiplication
            if (TryPerformNumericOp(baseVal.Value, overrideVal.Value, (a, b) => a * b, (a, b) => a * b, out var numResult))
            {
                return new ConfigValueNode(numResult, ConfigOperator.Assign, overrideNode.Source);
            }

            // String repetition: "abc" *= 3 -> "abcabcabc"
            if (baseVal.Value is string s && TryParseInt(overrideVal.Value, out int count))
            {
                string repeated = count > 0 ? string.Concat(Enumerable.Repeat(s, count)) : "";
                return new ConfigValueNode(repeated, ConfigOperator.Assign, overrideNode.Source);
            }
        }

        throw new InvalidOperatorException("*=", baseNode.GetType().Name, $"Cannot multiply {baseNode.GetType().Name} by {overrideNode.GetType().Name}.", overrideNode.Source);
    }

    private static ConfigNode ApplyDivide(ConfigNode baseNode, ConfigNode overrideNode)
    {
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            if (TryPerformNumericOp(baseVal.Value, overrideVal.Value, (a, b) => b == 0 ? throw new DivideByZeroException("Configuration division by zero.") : a / b,
                                                                   (a, b) => b == 0 ? throw new DivideByZeroException("Configuration division by zero.") : a / b, out var numResult))
            {
                return new ConfigValueNode(numResult, ConfigOperator.Assign, overrideNode.Source);
            }
        }

        throw new InvalidOperatorException("/=", baseNode.GetType().Name, $"Cannot divide {baseNode.GetType().Name} by {overrideNode.GetType().Name}.", overrideNode.Source);
    }

    private static ConfigNode ApplyBitwiseAnd(ConfigNode baseNode, ConfigNode overrideNode)
    {
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            if (TryParseBool(baseVal.Value, out bool b1) && TryParseBool(overrideVal.Value, out bool b2))
            {
                return new ConfigValueNode(b1 && b2, ConfigOperator.Assign, overrideNode.Source);
            }

            if (TryParseLong(baseVal.Value, out long l1) && TryParseLong(overrideVal.Value, out long l2))
            {
                return new ConfigValueNode(l1 & l2, ConfigOperator.Assign, overrideNode.Source);
            }
        }

        throw new InvalidOperatorException("&=", baseNode.GetType().Name, "Bitwise AND is only supported for booleans and integers.", overrideNode.Source);
    }

    private static ConfigNode ApplyBitwiseOr(ConfigNode baseNode, ConfigNode overrideNode)
    {
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            if (TryParseBool(baseVal.Value, out bool b1) && TryParseBool(overrideVal.Value, out bool b2))
            {
                return new ConfigValueNode(b1 || b2, ConfigOperator.Assign, overrideNode.Source);
            }

            if (TryParseLong(baseVal.Value, out long l1) && TryParseLong(overrideVal.Value, out long l2))
            {
                return new ConfigValueNode(l1 | l2, ConfigOperator.Assign, overrideNode.Source);
            }
        }

        throw new InvalidOperatorException("|=", baseNode.GetType().Name, "Bitwise OR is only supported for booleans and integers.", overrideNode.Source);
    }

    private static ConfigNode ApplyToggle(ConfigNode baseNode, ConfigNode overrideNode)
    {
        if (baseNode is ConfigValueNode baseVal)
        {
            if (TryParseBool(baseVal.Value, out bool currentBool))
            {
                return new ConfigValueNode(!currentBool, ConfigOperator.Assign, overrideNode.Source);
            }
        }

        throw new InvalidOperatorException("!=", baseNode.GetType().Name, "Toggle operator (!=) is only supported for booleans.", overrideNode.Source);
    }

    private static bool TryPerformNumericOp(
        object? a,
        object? b,
        Func<long, long, long> intOp,
        Func<double, double, double> doubleOp,
        out object result)
    {
        result = 0;
        if (a == null || b == null) return false;

        bool aIsDouble = a is float or double or decimal || (a is string sa && sa.Contains('.'));
        bool bIsDouble = b is float or double or decimal || (b is string sb && sb.Contains('.'));

        if (aIsDouble || bIsDouble)
        {
            if (TryParseDouble(a, out double da) && TryParseDouble(b, out double db))
            {
                result = doubleOp(da, db);
                return true;
            }
        }
        else
        {
            if (TryParseLong(a, out long la) && TryParseLong(b, out long lb))
            {
                result = intOp(la, lb);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseBool(object? val, out bool b)
    {
        b = false;
        if (val is bool bv) { b = bv; return true; }
        if (val is string s) return bool.TryParse(s, out b);
        return false;
    }

    private static bool TryParseLong(object? val, out long l)
    {
        l = 0;
        if (val is long lv) { l = lv; return true; }
        if (val is int iv) { l = iv; return true; }
        if (val is short sv) { l = sv; return true; }
        if (val is byte bv) { l = bv; return true; }
        if (val is string s) return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l);
        return false;
    }

    private static bool TryParseInt(object? val, out int i)
    {
        i = 0;
        if (val is int iv) { i = iv; return true; }
        if (val is long lv && lv is >= int.MinValue and <= int.MaxValue) { i = (int)lv; return true; }
        if (val is string s) return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i);
        return false;
    }

    private static bool TryParseDouble(object? val, out double d)
    {
        d = 0;
        if (val is double dv) { d = dv; return true; }
        if (val is float fv) { d = fv; return true; }
        if (val is decimal mv) { d = (double)mv; return true; }
        if (val is long lv) { d = lv; return true; }
        if (val is int iv) { d = iv; return true; }
        if (val is string s) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        return false;
    }
}
