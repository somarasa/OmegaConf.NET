using OmegaConf.NET.Ast;
using OmegaConf.NET.Common;

namespace OmegaConf.NET.Operators;

public static class OperatorParser
{
    private static readonly (string Symbol, ConfigOperator Operator)[] SuffixOperators =
    [
        ("+=", ConfigOperator.Add),
        ("-=", ConfigOperator.Subtract),
        ("*=", ConfigOperator.Multiply),
        ("/=", ConfigOperator.Divide),
        ("&=", ConfigOperator.BitwiseAnd),
        ("|=", ConfigOperator.BitwiseOr),
        ("!=", ConfigOperator.Toggle),
        ("=", ConfigOperator.Assign),
    ];

    /// <summary>
    /// Parses a key name and checks if it contains an operator suffix (e.g. "port+=" -> ("port", ConfigOperator.Add)).
    /// </summary>
    public static (string CleanKey, ConfigOperator Operator) ParseKey(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return (rawKey, ConfigOperator.Assign);
        }

        string trimmed = rawKey.Trim();

        foreach (var (symbol, op) in SuffixOperators)
        {
            if (trimmed.EndsWith(symbol, StringComparison.Ordinal))
            {
                string clean = trimmed[..^symbol.Length].TrimEnd();
                if (!string.IsNullOrEmpty(clean))
                {
                    return (clean, op);
                }
            }
        }

        return (trimmed, ConfigOperator.Assign);
    }

    /// <summary>
    /// Parses an expression string like "app.timeout+=10" or "db=postgres" from CLI args.
    /// </summary>
    public static (string Path, ConfigOperator Operator, string Value) ParseAssignment(string rawExpr)
    {
        if (string.IsNullOrWhiteSpace(rawExpr))
        {
            throw new ConfigSyntaxException("CLI expression cannot be empty.");
        }

        string trimmed = rawExpr.Trim();

        foreach (var (symbol, op) in SuffixOperators)
        {
            int idx = trimmed.IndexOf(symbol, StringComparison.Ordinal);
            if (idx > 0)
            {
                string path = trimmed[..idx].Trim();
                string val = trimmed[(idx + symbol.Length)..].Trim();
                return (path, op, val);
            }
        }

        throw new ConfigSyntaxException($"Expression '{rawExpr}' does not contain a valid operator (=, +=, -=, *=, /=, &=, |=, !=).");
    }
}
