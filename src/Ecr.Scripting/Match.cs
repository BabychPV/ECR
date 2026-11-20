namespace Ecr.Scripting;

/// <summary>Таблиця з речення <c>rows from</c> або оголошення <c>table</c>: <c>[КодШаблону.]КодТаблиці</c>.</summary>
/// <param name="TemplateCode">Код шаблону; <c>null</c> — не вказано.</param>
/// <param name="TableCode">Код таблиці.</param>
public sealed record TableRef(string? TemplateCode, string TableCode)
{
    /// <summary>Позиція в тексті (для діагностик оточення).</summary>
    public ScriptSpan Span { get; init; }

    /// <summary>Текстове подання: <c>Шаблон.Таблиця</c> або <c>Таблиця</c>.</summary>
    public override string ToString() => TemplateCode is null ? TableCode : TemplateCode + "." + TableCode;
}

/// <summary>Речення <c>rows from … where … active when …</c>.</summary>
/// <param name="Tables">Таблиці.</param>
/// <param name="Where">Умова відбору; <c>null</c> — без умови.</param>
/// <param name="Active">Умова активності; <c>null</c> — нікого не виключає.</param>
public sealed record RowsClause(IReadOnlyList<TableRef> Tables, MatchNode? Where, MatchNode? Active);

/// <summary>Вузол умови вибору рядків (§3.3 <c>matchExpr</c>).</summary>
public abstract record MatchNode
{
    /// <summary>Позиція в тексті.</summary>
    public ScriptSpan Span { get; init; }
}

/// <summary>Кон'юнкція.</summary>
public sealed record MatchAnd(MatchNode Left, MatchNode Right) : MatchNode;

/// <summary>Диз'юнкція.</summary>
public sealed record MatchOr(MatchNode Left, MatchNode Right) : MatchNode;

/// <summary>Заперечення.</summary>
public sealed record MatchNot(MatchNode Operand) : MatchNode;

/// <summary><c>row.Колонка ==|!= значення</c>.</summary>
public sealed record MatchEquals(string Column, MatchValue Value, bool Negated) : MatchNode;

/// <summary><c>row.Колонка in (значення, …)</c>.</summary>
public sealed record MatchIn(string Column, IReadOnlyList<MatchValue> Values) : MatchNode;

/// <summary>Значення в умові вибору рядків.</summary>
public abstract record MatchValue
{
    /// <summary>Рядковий літерал.</summary>
    public sealed record Text(string Value) : MatchValue;

    /// <summary><c>entry("ДОВІДНИК","КОД")</c> — підставляється текст Id запису.</summary>
    public sealed record Entry(string RegistryCode, string EntryCode) : MatchValue;

    /// <summary><c>null</c> — «тексту немає».</summary>
    public sealed record Null : MatchValue;
}

/// <summary>Чиста функція обчислення умови вибору рядків (§3.6).</summary>
public static class MatchEvaluator
{
    /// <summary>Обчислює умову для одного рядка.</summary>
    /// <param name="node">Умова.</param>
    /// <param name="columnText">Текст збігу колонки за кодом; <c>null</c> — колонки немає або вона порожня.</param>
    /// <param name="entryIdText">Текст Id запису довідника за (код довідника, код запису); <c>null</c> — не знайдено.</param>
    /// <remarks>
    /// Рівність порядкова (регістр важливий). Літерал <c>null</c> дорівнює лише відсутньому тексту.
    /// Нерозв'язаний <c>entry</c> не дорівнює нічому (так умова не зачепить рядки з порожнім полем).
    /// </remarks>
    public static bool Evaluate(
        MatchNode node,
        Func<string, string?> columnText,
        Func<string, string, string?> entryIdText)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(columnText);
        ArgumentNullException.ThrowIfNull(entryIdText);

        return node switch
        {
            MatchAnd and => Evaluate(and.Left, columnText, entryIdText) && Evaluate(and.Right, columnText, entryIdText),
            MatchOr or => Evaluate(or.Left, columnText, entryIdText) || Evaluate(or.Right, columnText, entryIdText),
            MatchNot not => !Evaluate(not.Operand, columnText, entryIdText),
            MatchEquals eq => Equal(columnText(eq.Column), eq.Value, entryIdText) != eq.Negated,
            MatchIn @in => @in.Values.Any(v => Equal(columnText(@in.Column), v, entryIdText)),
            _ => false,
        };
    }

    private static bool Equal(string? cell, MatchValue value, Func<string, string, string?> entryIdText)
        => value switch
        {
            MatchValue.Text t => cell is not null && string.Equals(cell, t.Value, StringComparison.Ordinal),
            MatchValue.Entry e => entryIdText(e.RegistryCode, e.EntryCode) is { } id
                                  && cell is not null && string.Equals(cell, id, StringComparison.Ordinal),
            _ => cell is null,
        };
}
