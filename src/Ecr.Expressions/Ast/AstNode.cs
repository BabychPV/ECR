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

/// <summary>
/// Поле рядка довідника: <c>ROW.COMPONENT.MW</c> (FEATURE-REGISTRY-TABLES §5.2).
/// </summary>
/// <remarks>
/// <see cref="Path"/> — коди полів від рядка області: <c>[COMPONENT, MW]</c>.
/// Усі сегменти, крім останнього, мусять бути <c>Lookup</c>-полями, але це
/// перевіряє публікація (перевірка 16 §5.5), а не парсер: форми довідника
/// парсер не бачить.
///
/// ⛔ Шлях заморожується ТУТ, у конструкторі, а не лише в парсері: вузол іде в
/// кеш розбору (`CAL-05`) і звідти — до всіх наступних прогонів. Копія в
/// <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/> означає,
/// що ні <c>(IList&lt;string&gt;)Path</c>, ні зміна списку, з якого вузол
/// створено, не зіпсують чужий вираз. Властивість лише для читання, без
/// <c>init</c>: <c>with { Path = … }</c> теж не пройде.
/// </remarks>
public sealed record RowFieldNode : AstNode
{
    /// <summary>Створює вузол із копії шляху.</summary>
    /// <param name="path">Коди полів; принаймні один.</param>
    /// <exception cref="ArgumentException">Шлях порожній.</exception>
    public RowFieldNode(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0)
        {
            throw new ArgumentException("Шлях ROW. мусить містити принаймні одне поле.", nameof(path));
        }

        Path = new System.Collections.ObjectModel.ReadOnlyCollection<string>([.. path]);
    }

    /// <summary>Коди полів від рядка області.</summary>
    public IReadOnlyList<string> Path { get; }
}

/// <summary>
/// Запис, який перевіряє правило довідника: <c>THIS</c> (FEATURE-REGISTRY-TABLES §5.2).
/// </summary>
/// <remarks>
/// Статично — <c>EntryRef</c> довідника правила, у рантаймі — число (id
/// запису), як <c>Lookup</c>-комірка (§5.3). Поза правилом довідника парсер
/// дає діагностику <c>expr.thisOutsideRule</c>.
/// </remarks>
public sealed record ThisNode : AstNode;

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
