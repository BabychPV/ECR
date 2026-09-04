// src/Ecr.Expressions/Ast/AstNode.cs
namespace Ecr.Expressions.Ast;

/// <summary>Вузол синтаксичного дерева виразу.</summary>
public abstract record AstNode
{
    /// <summary>Позиція в тексті виразу — для повідомлень про помилки.</summary>
    public int Position { get; init; }
}

/// <summary>Літерал: число, текст, булеве значення або NULL.</summary>
public sealed record LiteralNode(object? Value, ExpressionValueType Type) : AstNode;

/// <summary>Бінарна операція.</summary>
public sealed record BinaryNode(BinaryOperator Operator, AstNode Left, AstNode Right) : AstNode;

/// <summary>Унарна операція.</summary>
public sealed record UnaryNode(UnaryOperator Operator, AstNode Operand) : AstNode;

/// <summary>Тернарний оператор <c>? :</c>.</summary>
public sealed record ConditionalNode(AstNode Condition, AstNode WhenTrue, AstNode WhenFalse) : AstNode;

/// <summary>Виклик функції.</summary>
public sealed record FunctionNode(string Name, IReadOnlyList<AstNode> Arguments) : AstNode;

/// <summary>Посилання на комірку або діапазон.</summary>
public sealed record CellReferenceNode(
    string? SheetCode,
    string? TableCode,
    RowSelector Row,
    string ColumnSelector,
    int PeriodOffset) : AstNode;

/// <summary>Посилання діалекту Methodology: <c>@Arg</c>, <c>CST.X</c>, <c>!Formula</c>, <c>HDR.Y</c>.</summary>
public sealed record SymbolReferenceNode(SymbolKind Kind, string Name) : AstNode;

/// <summary>Календарний контекст: <c>[Period].Days</c> тощо.</summary>
public sealed record PeriodPropertyNode(string Property, int PeriodOffset) : AstNode;

/// <summary>Селектор рядків: конкретний ключ, діапазон або предикат.</summary>
public abstract record RowSelector
{
    /// <summary>Конкретний рядок.</summary>
    public sealed record Single(string RowKey) : RowSelector;

    /// <summary>Діапазон; розкривається в список RowKey при Publish.</summary>
    public sealed record Range(string FromRowKey, string ToRowKey) : RowSelector;

    /// <summary>Предикат для RowMode = Dynamic; обчислюється в рантаймі.</summary>
    public sealed record Predicate(AstNode Condition) : RowSelector;

    /// <summary>Той самий рядок, що й у формули (для Scope = Row).</summary>
    public sealed record Current : RowSelector;
}

public enum ExpressionValueType : byte { Null = 0, Number = 1, Text = 2, Boolean = 3, Date = 4, Error = 5 }
public enum SymbolKind : byte { Argument = 0, Constant = 1, Formula = 2, Header = 3 }
public enum UnaryOperator : byte { Negate = 0, Plus = 1, Not = 2 }

public enum BinaryOperator : byte
{
    Add = 0, Subtract = 1, Multiply = 2, Divide = 3, Modulo = 4, Power = 5,
    Concat = 6,
    Equal = 7, NotEqual = 8, Less = 9, LessOrEqual = 10, Greater = 11, GreaterOrEqual = 12,
    And = 13, Or = 14
}
