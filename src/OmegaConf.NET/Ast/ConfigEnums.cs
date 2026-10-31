namespace OmegaConf.NET.Ast;

public enum ConfigNodeType
{
    Value,
    List,
    Object
}

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
