using System.Globalization;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Interpolation;

/// <summary>
/// Built-in math and logic expression evaluator resolver.
/// Syntax: ${eval: 10 * 20} or ${eval: ${batch_size} * 4}
/// </summary>
public class EvalResolver : IResolver
{
    public string Name => "eval";

    public string Resolve(string argument, IInterpolationContext context)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return "";
        }

        var evaluator = new SimpleExpressionParser(argument);
        var result = evaluator.Evaluate();
        return result?.ToString() ?? "";
    }

    private sealed class SimpleExpressionParser
    {
        private readonly string _expr;
        private int _pos;

        public SimpleExpressionParser(string expr)
        {
            _expr = expr;
            _pos = 0;
        }

        public object? Evaluate()
        {
            var res = ParseLogicalOr();
            SkipWhitespace();
            if (_pos < _expr.Length)
            {
                throw new ConfigSyntaxException($"Unexpected character in eval expression at position {_pos}: '{_expr[_pos]}'");
            }
            return res;
        }

        private object? ParseLogicalOr()
        {
            var left = ParseLogicalAnd();

            while (Match("||"))
            {
                var right = ParseLogicalAnd();
                left = ToBool(left) || ToBool(right);
            }

            return left;
        }

        private object? ParseLogicalAnd()
        {
            var left = ParseEquality();

            while (Match("&&"))
            {
                var right = ParseEquality();
                left = ToBool(left) && ToBool(right);
            }

            return left;
        }

        private object? ParseEquality()
        {
            var left = ParseRelational();

            while (true)
            {
                if (Match("=="))
                {
                    var right = ParseRelational();
                    left = AreEqual(left, right);
                }
                else if (Match("!="))
                {
                    var right = ParseRelational();
                    left = !AreEqual(left, right);
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private object? ParseRelational()
        {
            var left = ParseAdditive();

            while (true)
            {
                if (Match("<="))
                {
                    var right = ParseAdditive();
                    left = ToDouble(left) <= ToDouble(right);
                }
                else if (Match(">="))
                {
                    var right = ParseAdditive();
                    left = ToDouble(left) >= ToDouble(right);
                }
                else if (Match("<"))
                {
                    var right = ParseAdditive();
                    left = ToDouble(left) < ToDouble(right);
                }
                else if (Match(">"))
                {
                    var right = ParseAdditive();
                    left = ToDouble(left) > ToDouble(right);
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private object? ParseAdditive()
        {
            var left = ParseMultiplicative();

            while (true)
            {
                if (Match("+"))
                {
                    var right = ParseMultiplicative();
                    if (left is string || right is string)
                    {
                        left = $"{left}{right}";
                    }
                    else if (IsInteger(left) && IsInteger(right))
                    {
                        left = ToLong(left) + ToLong(right);
                    }
                    else
                    {
                        left = ToDouble(left) + ToDouble(right);
                    }
                }
                else if (Match("-"))
                {
                    var right = ParseMultiplicative();
                    if (IsInteger(left) && IsInteger(right))
                    {
                        left = ToLong(left) - ToLong(right);
                    }
                    else
                    {
                        left = ToDouble(left) - ToDouble(right);
                    }
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private object? ParseMultiplicative()
        {
            var left = ParseUnary();

            while (true)
            {
                if (Match("*"))
                {
                    var right = ParseUnary();
                    if (IsInteger(left) && IsInteger(right))
                    {
                        left = ToLong(left) * ToLong(right);
                    }
                    else
                    {
                        left = ToDouble(left) * ToDouble(right);
                    }
                }
                else if (Match("/"))
                {
                    var right = ParseUnary();
                    double dRight = ToDouble(right);
                    if (dRight == 0.0) throw new DivideByZeroException("Division by zero in eval expression.");

                    if (IsInteger(left) && IsInteger(right) && (ToLong(left) % ToLong(right) == 0))
                    {
                        left = ToLong(left) / ToLong(right);
                    }
                    else
                    {
                        left = ToDouble(left) / dRight;
                    }
                }
                else if (Match("%"))
                {
                    var right = ParseUnary();
                    left = ToLong(left) % ToLong(right);
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private object? ParseUnary()
        {
            SkipWhitespace();

            if (Match("!"))
            {
                return !ToBool(ParseUnary());
            }

            if (Match("-"))
            {
                var val = ParseUnary();
                if (IsInteger(val)) return -ToLong(val);
                return -ToDouble(val);
            }

            if (Match("+"))
            {
                return ParseUnary();
            }

            return ParsePrimary();
        }

        private object? ParsePrimary()
        {
            SkipWhitespace();

            if (_pos >= _expr.Length)
            {
                throw new ConfigSyntaxException("Unexpected end of eval expression.");
            }

            // Parentheses
            if (Match("("))
            {
                var inner = ParseLogicalOr();
                if (!Match(")"))
                {
                    throw new ConfigSyntaxException("Expected closing ')' in eval expression.");
                }
                return inner;
            }

            // String literals: "..." or '...'
            if (_expr[_pos] is '"' or '\'')
            {
                char quote = _expr[_pos++];
                int start = _pos;
                while (_pos < _expr.Length && _expr[_pos] != quote)
                {
                    _pos++;
                }
                string strVal = _expr[start.._pos];
                if (_pos < _expr.Length) _pos++; // skip quote
                return strVal;
            }

            // Numbers
            int numStart = _pos;
            bool hasDot = false;
            while (_pos < _expr.Length && (char.IsDigit(_expr[_pos]) || (!hasDot && _expr[_pos] == '.')))
            {
                if (_expr[_pos] == '.') hasDot = true;
                _pos++;
            }

            if (_pos > numStart)
            {
                string numText = _expr[numStart.._pos];
                if (hasDot && double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    return d;
                }
                if (long.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                {
                    return l;
                }
            }

            // Identifiers / booleans
            int idStart = _pos;
            while (_pos < _expr.Length && (char.IsLetterOrDigit(_expr[_pos]) || _expr[_pos] == '_'))
            {
                _pos++;
            }

            if (_pos > idStart)
            {
                string id = _expr[idStart.._pos];
                if (string.Equals(id, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(id, "false", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(id, "null", StringComparison.OrdinalIgnoreCase)) return null;
                return id;
            }

            throw new ConfigSyntaxException($"Unexpected character '{_expr[_pos]}' at position {_pos} in eval expression.");
        }

        private bool Match(string op)
        {
            SkipWhitespace();
            if (_pos + op.Length <= _expr.Length && _expr.Substring(_pos, op.Length) == op)
            {
                _pos += op.Length;
                return true;
            }
            return false;
        }

        private void SkipWhitespace()
        {
            while (_pos < _expr.Length && char.IsWhiteSpace(_expr[_pos]))
            {
                _pos++;
            }
        }

        private static bool IsInteger(object? val) => val is byte or sbyte or short or ushort or int or uint or long or ulong;

        private static long ToLong(object? val)
        {
            if (val == null) return 0;
            return Convert.ToInt64(val, CultureInfo.InvariantCulture);
        }

        private static double ToDouble(object? val)
        {
            if (val == null) return 0.0;
            return Convert.ToDouble(val, CultureInfo.InvariantCulture);
        }

        private static bool ToBool(object? val)
        {
            if (val is bool b) return b;
            if (val is string s && bool.TryParse(s, out bool sb)) return sb;
            if (val is long l) return l != 0;
            if (val is double d) return d != 0.0;
            return val != null;
        }

        private static bool AreEqual(object? left, object? right)
        {
            if (left == null && right == null) return true;
            if (left == null || right == null) return false;

            if (IsInteger(left) && IsInteger(right))
            {
                return ToLong(left) == ToLong(right);
            }

            if ((left is double or float || right is double or float))
            {
                return Math.Abs(ToDouble(left) - ToDouble(right)) < 1e-9;
            }

            return left.Equals(right) || string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
