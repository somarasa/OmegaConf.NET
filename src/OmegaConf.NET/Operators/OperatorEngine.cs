using System.Globalization;
using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Operators;

/// <summary>
/// Executes in-config and CLI mutation operators (<c>=</c>, <c>+=</c>, <c>-=</c>, <c>*=</c>, <c>/=</c>, <c>&amp;=</c>, <c>|=</c>, <c>!=</c>)
/// when merging an override <see cref="ConfigNode"/> onto an inherited base <see cref="ConfigNode"/>.
/// All operations return newly cloned nodes and never mutate the input AST nodes in-place.
/// </summary>
public static class OperatorEngine
{
    /// <summary>
    /// Merges an <paramref name="overrideNode"/> onto a <paramref name="baseNode"/> using the <see cref="ConfigNode.Operator"/>
    /// declared on <paramref name="overrideNode"/>.
    /// </summary>
    /// <param name="baseNode">The inherited base node, or <c>null</c> if the key did not exist in the base layer.</param>
    /// <param name="overrideNode">The child or CLI override node carrying the mutation operator and operand value.</param>
    /// <returns>A new <see cref="ConfigNode"/> representing the merged result.</returns>
    /// <exception cref="InvalidOperatorException">Thrown when the operator is incompatible with the node types.</exception>
    /// <exception cref="DivideByZeroException">Thrown when dividing a number by zero via <c>/=</c>.</exception>
    public static ConfigNode Apply(ConfigNode? baseNode, ConfigNode overrideNode)
    {
        // If no base node exists yet, simply clone the override node as the initial value
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

    /// <summary>
    /// Applies standard assignment (<c>=</c>). Objects are merged recursively by key; scalars and lists replace the base.
    /// </summary>
    private static ConfigNode ApplyAssign(ConfigNode baseNode, ConfigNode overrideNode)
    {
        // When both base and override are objects, recursively merge their children (deep dictionary merge)
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

        // Otherwise scalar or list replaces the base node completely
        // (Unless baseNode is a composed ConfigObjectNode and overrideNode is a null/missing uninitialized schema placeholder)
        if (baseNode is ConfigObjectNode &&
            overrideNode is ConfigValueNode { Value: null } or ConfigValueNode { IsMissingMarker: true })
        {
            return baseNode.Clone();
        }

        return overrideNode.Clone();
    }

    /// <summary>
    /// Applies the addition operator (<c>+=</c>):
    /// - Lists: appends items to the end of the base list.
    /// - Objects: merges keys recursively.
    /// - Numbers: performs arithmetic addition (preserving integer vs. floating-point type).
    /// - Strings: concatenates strings.
    /// </summary>
    private static ConfigNode ApplyAdd(ConfigNode baseNode, ConfigNode overrideNode)
    {
        // Case 1: List append
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

        // Case 2: Object merge
        if (baseNode is ConfigObjectNode baseObj && overrideNode is ConfigObjectNode overrideObj)
        {
            return ApplyAssign(baseObj, overrideObj);
        }

        // Case 3: Scalar numeric addition or string concatenation
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            if (TryPerformNumericOp(baseVal.Value, overrideVal.Value, NumericBinaryOp.Add, out var numResult))
            {
                return new ConfigValueNode(numResult, ConfigOperator.Assign, overrideNode.Source);
            }

            // Fallback to string concatenation
            string strA = baseVal.Value?.ToString() ?? "";
            string strB = overrideVal.Value?.ToString() ?? "";
            return new ConfigValueNode(strA + strB, ConfigOperator.Assign, overrideNode.Source);
        }

        throw new InvalidOperatorException("+=", baseNode.GetType().Name, $"Cannot add {overrideNode.GetType().Name} to {baseNode.GetType().Name}.", overrideNode.Source);
    }

    /// <summary>
    /// Applies the subtraction operator (<c>-=</c>):
    /// - Lists: removes all items matching the override element(s).
    /// - Numbers: performs arithmetic subtraction.
    /// </summary>
    private static ConfigNode ApplySubtract(ConfigNode baseNode, ConfigNode overrideNode)
    {
        // Case 1: List item removal by value match
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

        // Case 2: Numeric subtraction
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            if (TryPerformNumericOp(baseVal.Value, overrideVal.Value, NumericBinaryOp.Subtract, out var numResult))
            {
                return new ConfigValueNode(numResult, ConfigOperator.Assign, overrideNode.Source);
            }
        }

        throw new InvalidOperatorException("-=", baseNode.GetType().Name, $"Cannot subtract {overrideNode.GetType().Name} from {baseNode.GetType().Name}.", overrideNode.Source);
    }

    /// <summary>
    /// Applies the multiplication operator (<c>*=</c>):
    /// - Numbers: performs arithmetic multiplication.
    /// - Strings: repeats the base string N times (e.g. <c>"ha" *= 3</c> -> <c>"hahaha"</c>).
    /// </summary>
    private static ConfigNode ApplyMultiply(ConfigNode baseNode, ConfigNode overrideNode)
    {
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            // Numeric multiplication
            if (TryPerformNumericOp(baseVal.Value, overrideVal.Value, NumericBinaryOp.Multiply, out var numResult))
            {
                return new ConfigValueNode(numResult, ConfigOperator.Assign, overrideNode.Source);
            }

            // Python-style string repetition: "abc" *= 3 -> "abcabcabc"
            if (baseVal.Value is string s && TryParseInt(overrideVal.Value, out int count))
            {
                string repeated = count > 0 ? string.Concat(Enumerable.Repeat(s, count)) : "";
                return new ConfigValueNode(repeated, ConfigOperator.Assign, overrideNode.Source);
            }
        }

        throw new InvalidOperatorException("*=", baseNode.GetType().Name, $"Cannot multiply {baseNode.GetType().Name} by {overrideNode.GetType().Name}.", overrideNode.Source);
    }

    /// <summary>
    /// Applies the division operator (<c>/=</c>) on numeric values, throwing <see cref="DivideByZeroException"/> when divisor is zero.
    /// </summary>
    private static ConfigNode ApplyDivide(ConfigNode baseNode, ConfigNode overrideNode)
    {
        if (baseNode is ConfigValueNode baseVal && overrideNode is ConfigValueNode overrideVal)
        {
            if (TryPerformNumericOp(baseVal.Value, overrideVal.Value, NumericBinaryOp.Divide, out var numResult))
            {
                return new ConfigValueNode(numResult, ConfigOperator.Assign, overrideNode.Source);
            }
        }

        throw new InvalidOperatorException("/=", baseNode.GetType().Name, $"Cannot divide {baseNode.GetType().Name} by {overrideNode.GetType().Name}.", overrideNode.Source);
    }

    /// <summary>
    /// Applies logical AND (<c>&amp;=</c>) on booleans or bitwise AND on 64-bit integers.
    /// </summary>
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

    /// <summary>
    /// Applies logical OR (<c>|=</c>) on booleans or bitwise OR on 64-bit integers.
    /// </summary>
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

    /// <summary>
    /// Applies boolean toggle / negation (<c>!=</c>), flipping <c>true</c> to <c>false</c> and <c>false</c> to <c>true</c>.
    /// </summary>
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

    /// <summary>
    /// Attempts to execute a binary numeric operation. If either operand is floating-point, promotes both to <see cref="double"/>;
    /// otherwise executes 64-bit integer arithmetic (<see cref="long"/>), detecting overflow explicitly so results never
    /// silently wrap around.
    /// </summary>
    private static bool TryPerformNumericOp(
        object? a,
        object? b,
        NumericBinaryOp op,
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
                result = ApplyDoubleOp(da, db, op);
                return true;
            }
        }
        else
        {
            if (TryParseLong(a, out long la) && TryParseLong(b, out long lb))
            {
                result = ApplyLongOp(la, lb, op);
                return true;
            }
        }

        return false;
    }

    private enum NumericBinaryOp
    {
        Add,
        Subtract,
        Multiply,
        Divide,
    }

    private static long ApplyLongOp(long a, long b, NumericBinaryOp op)
    {
        switch (op)
        {
            case NumericBinaryOp.Add:
            {
                long result = a + b;
                if (((a > 0 && b > 0 && result < 0) || (a < 0 && b < 0 && result > 0)))
                {
                    throw new OverflowException("Integer overflow in configuration arithmetic.");
                }
                return result;
            }

            case NumericBinaryOp.Subtract:
            {
                long result = a - b;
                if (((a > 0 && b < 0 && result < 0) || (a < 0 && b > 0 && result > 0)))
                {
                    throw new OverflowException("Integer overflow in configuration arithmetic.");
                }
                return result;
            }

            case NumericBinaryOp.Multiply:
            {
                if (a != 0 && b != 0 && (a * b) / a != b)
                {
                    throw new OverflowException("Integer overflow in configuration arithmetic.");
                }
                return a * b;
            }

            case NumericBinaryOp.Divide:
            {
                if (b == 0)
                {
                    throw new DivideByZeroException("Configuration division by zero.");
                }
                if (a == long.MinValue && b == -1)
                {
                    throw new OverflowException("Integer overflow in configuration arithmetic.");
                }
                return a / b;
            }

            default:
                throw new InvalidOperationException("Unsupported numeric operation.");
        }
    }

    private static double ApplyDoubleOp(double a, double b, NumericBinaryOp op)
    {
        switch (op)
        {
            case NumericBinaryOp.Add:
                return a + b;
            case NumericBinaryOp.Subtract:
                return a - b;
            case NumericBinaryOp.Multiply:
                return a * b;
            case NumericBinaryOp.Divide:
                if (b == 0.0)
                {
                    throw new DivideByZeroException("Configuration division by zero.");
                }
                return a / b;
            default:
                throw new InvalidOperationException("Unsupported numeric operation.");
        }
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
