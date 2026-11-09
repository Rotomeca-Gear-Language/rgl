namespace Rgl.IR.Expressions;

/// <summary>Opérateurs à un opérande, indépendants de leur écriture dans le source.</summary>
internal enum IrUnaryOperator
{
    /// <summary>Opposé d'un nombre : <c>-x</c>.</summary>
    Negate,

    /// <summary>Négation logique : <c>not ok</c>, ou <c>invert</c> sur un bool.</summary>
    Not,

    /// <summary>Inversion de tous les bits d'un entier : <c>invert x</c>.</summary>
    BitwiseNot,
}

/// <summary>Opérateurs à deux opérandes, indépendants de leur écriture dans le source.</summary>
internal enum IrBinaryOperator
{
    /// <summary><c>+</c> sur des nombres.</summary>
    Add,

    /// <summary><c>+</c> sur des chaînes.</summary>
    Concatenate,

    /// <summary><c>-</c>.</summary>
    Subtract,

    /// <summary><c>*</c>.</summary>
    Multiply,

    /// <summary><c>/</c>.</summary>
    Divide,

    /// <summary><c>%</c>.</summary>
    Remainder,

    /// <summary><c>&lt;</c>.</summary>
    Less,

    /// <summary><c>&gt;</c>.</summary>
    Greater,

    /// <summary><c>&lt;=</c>.</summary>
    LessOrEqual,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterOrEqual,

    /// <summary><c>eq</c>.</summary>
    Equal,

    /// <summary><c>and</c>.</summary>
    And,

    /// <summary><c>or</c>.</summary>
    Or,

    /// <summary><c>xor</c> logique.</summary>
    Xor,

    /// <summary><c>bitand</c>.</summary>
    BitAnd,

    /// <summary><c>bitor</c>.</summary>
    BitOr,

    /// <summary><c>bitxor</c>.</summary>
    BitXor,

    /// <summary><c>shl</c>.</summary>
    ShiftLeft,

    /// <summary><c>shr</c>.</summary>
    ShiftRight,
}
