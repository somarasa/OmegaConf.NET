namespace OmegaConf.NET.Ast;

/// <summary>
/// Represents the structural category of a <see cref="ConfigNode"/> in the configuration Abstract Syntax Tree (AST).
/// </summary>
public enum ConfigNodeType
{
    /// <summary>A leaf scalar value node (string, integer, double, boolean, or null).</summary>
    Value,

    /// <summary>An ordered sequence / list of child <see cref="ConfigNode"/> elements.</summary>
    List,

    /// <summary>A key-value dictionary / mapping of child <see cref="ConfigNode"/> elements.</summary>
    Object
}

/// <summary>
/// Defines the mutation operator associated with a configuration key or CLI override expression.
/// </summary>
public enum ConfigOperator
{
    /// <summary> = direct assignment / override </summary>
    Assign,

    /// <summary> += addition, concatenation, or list append </summary>
    Add,

    /// <summary> -= subtraction or list item removal </summary>
    Subtract,

    /// <summary> *= multiplication or string repetition </summary>
    Multiply,

    /// <summary> /= division </summary>
    Divide,

    /// <summary> &amp;= logical AND </summary>
    BitwiseAnd,

    /// <summary> |= logical OR </summary>
    BitwiseOr,

    /// <summary> != logical toggle / invert </summary>
    Toggle
}
