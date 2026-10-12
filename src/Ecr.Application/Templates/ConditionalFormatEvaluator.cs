using System.Globalization;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Templates;

/// <summary>
/// Результат спрацювання правила умовного форматування (ФВ-2.6/2.7): колір
/// <c>#rrggbb</c> (або <c>null</c> — колір теми) і жирність.
/// </summary>
/// <param name="BackgroundHex"><c>#rrggbb</c> або <c>null</c> — колір теми.</param>
/// <param name="ForegroundHex"><c>#rrggbb</c> або <c>null</c>.</param>
/// <param name="IsBold">Жирний.</param>
/// <param name="Pattern">
/// Штрих (<c>hatch</c> — «неактуальна комірка»); лише від умовного форматування, <c>null</c> — немає.
/// ⚠ Додано в L6 останнім необов'язковим полем: клієнт, що його не знає, малює решту як раніше.
/// </param>
public sealed record CellFormatDto(string? BackgroundHex, string? ForegroundHex, bool IsBold, string? Pattern = null);

/// <summary>
/// Значення колонки ТОГО Ж рядка для правила з <see cref="ConditionalFormatRule.RefColumnCode"/>.
/// Повертає <c>false</c>, якщо колонка комірці-читачеві НЕ видна (прихована, заборонена доступом,
/// відсутня): правило тоді не застосовується — значення такої колонки не повинно впливати на вигляд
/// (нерозкриття). Значення вже зведене: елемент довідника — його <b>код</b> (рядок), решта — як
/// <c>CellValueMapping.ToRuleValue</c>.
/// </summary>
public delegate bool RefValueResolver(string columnCode, out object? value);

/// <summary>Де комірка: код таблиці, ключ рядка й доступ до інших колонок того ж рядка.</summary>
/// <param name="TableCode">Код таблиці комірки.</param>
/// <param name="RowKey">Ключ рядка комірки.</param>
/// <param name="Resolve">Значення інших колонок того ж рядка (<see cref="RefValueResolver"/>).</param>
public sealed record RuleCellContext(string TableCode, string RowKey, RefValueResolver Resolve);

/// <summary>
/// ЄДИНА функція «правило → стиль комірки»: її викликають і сітка документа
/// (<c>GetTableSliceHandler</c>), і Excel-експорт, тож розбіжності між екраном
/// і книгою немає за побудовою.
/// </summary>
/// <remarks>
/// Чиста: без портів і стану. Порівняння — числове, кома прирівняна до крапки
/// (як <c>ConditionalFormatRule</c> валідує операнди). Значення, що не є числом
/// (текст — навіть «5» —, дата, булеве), числових операторів не задовольняє — включно з
/// <c>ne</c>: «не дорівнює 5» для тексту «abc» — не привід фарбувати. Порожнім
/// вважається <c>null</c> або рядок із самих пробілів. Перше правило за
/// <see cref="ConditionalFormatRule.Ordinal"/>, що спрацювало, виграє.
/// </remarks>
public static class ConditionalFormatEvaluator
{
    /// <summary>Групує правила за кодом колонки; кожна група — у порядку застосування.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ConditionalFormatRule>> ByColumn(
        IEnumerable<ConditionalFormatRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        return rules
            .GroupBy(r => r.ColumnCode, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ConditionalFormatRule>)[.. g.OrderBy(r => r.Ordinal)],
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Правила, що діють на колонку <paramref name="columnCode"/>: спершу власні (за порядком), потім правила
    /// «усі колонки таблиці» (<see cref="ConditionalFormatRule.AnyColumn"/>). Перше спрацьоване виграє.
    /// </summary>
    public static IReadOnlyList<ConditionalFormatRule> RulesFor(
        IReadOnlyDictionary<string, IReadOnlyList<ConditionalFormatRule>> byColumn, string columnCode)
    {
        ArgumentNullException.ThrowIfNull(byColumn);

        var own = byColumn.TryGetValue(columnCode, out var rules) ? rules : [];
        var any = byColumn.TryGetValue(ConditionalFormatRule.AnyColumn, out var wide) ? wide : [];

        return any.Count == 0 ? own : own.Count == 0 ? any : [.. own, .. any];
    }

    /// <summary>
    /// Формат комірки зі значенням <paramref name="value"/> БЕЗ контексту рядка; <c>null</c> — жодне правило не спрацювало.
    /// </summary>
    /// <remarks>
    /// ⚠ Правила, що потребують контексту (таблиця, рядки, інша колонка, «усі колонки»), тут ПРОПУСКАЮТЬСЯ, а не
    /// застосовуються наосліп: виклик без контексту не вміє їх оцінити, і хибне фарбування гірше за відсутнє.
    /// </remarks>
    /// <param name="columnRules">Правила ОДНІЄЇ колонки в порядку застосування (<see cref="ByColumn"/>).</param>
    /// <param name="value">Значення як у сітці (<c>CellValueMapping.ToRuleValue</c>).</param>
    public static CellFormatDto? Evaluate(IReadOnlyList<ConditionalFormatRule> columnRules, object? value)
    {
        ArgumentNullException.ThrowIfNull(columnRules);

        foreach (var rule in columnRules)
        {
            if (!rule.IsScoped && Matches(rule, value))
            {
                return Format(rule);
            }
        }

        return null;
    }

    /// <summary>
    /// Формат комірки в КОНТЕКСТІ рядка: правила з таблицею/рядками/посиланням на колонку застосовуються лише
    /// там, де умова області виконана.
    /// </summary>
    /// <param name="rules">Правила колонки (<see cref="RulesFor"/>), у порядку застосування.</param>
    /// <param name="value">Значення самої комірки.</param>
    /// <param name="context">Таблиця, рядок і значення інших колонок рядка.</param>
    public static CellFormatDto? Evaluate(
        IReadOnlyList<ConditionalFormatRule> rules, object? value, RuleCellContext context)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var rule in rules)
        {
            if (rule.TableCode is not null
                && !string.Equals(rule.TableCode, context.TableCode, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.Scope == ConditionalFormatRule.ScopeRow
                && (rule.RowKeys is null || !rule.RowKeys.Contains(context.RowKey, StringComparer.Ordinal)))
            {
                continue;
            }

            var tested = value;
            if (rule.RefColumnCode is { } reference && !context.Resolve(reference, out tested))
            {
                // Колонка-джерело комірці-читачеві не видна: правило не застосовується, а не «порожнє».
                continue;
            }

            if (Matches(rule, tested))
            {
                return Format(rule);
            }
        }

        return null;
    }

    private static CellFormatDto Format(ConditionalFormatRule rule)
        => new(rule.BackgroundHex, rule.ForegroundHex, rule.IsBold, rule.Pattern);

    private static bool Matches(ConditionalFormatRule rule, object? value)
    {
        var isEmpty = value is null || (value is string text && string.IsNullOrWhiteSpace(text));

        switch (rule.Operator)
        {
            case "empty":
                return isEmpty;
            case "notEmpty":
                return !isEmpty;
            case "textEq" or "textNe":
                return MatchesText(rule, value);
            default:
                break;
        }

        if (!TryNumber(value, out var number) || !TryOperand(rule.Value, out var operand))
        {
            return false;
        }

        return rule.Operator switch
        {
            "gt" => number > operand,
            "ge" => number >= operand,
            "lt" => number < operand,
            "le" => number <= operand,
            "eq" => number == operand,
            "ne" => number != operand,
            // Межі — включно, і порядок операндів не важливий (автор міг ввести 10…1).
            "between" => TryOperand(rule.ValueTo, out var upper)
                         && number >= Math.Min(operand, upper) && number <= Math.Max(operand, upper),
            _ => false,
        };
    }

    // Текст комірки: рядок (із довідника — його код, розв'язав виклик) без крайніх пробілів; порожнє — "".
    // Число/дата/булеве/ідентифікатор — НЕ текст: жоден текстовий оператор їх не задовольняє (в т.ч. textNe),
    // як числові оператори не задовольняє текст. Регістр ігнорується (в Excel «=» нечутливий до регістру).
    private static bool MatchesText(ConditionalFormatRule rule, object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            string s => s.Trim(),
            _ => null,
        };

        if (text is null || rule.Value is null)
        {
            return false;
        }

        var same = string.Equals(text, rule.Value.Trim(), StringComparison.OrdinalIgnoreCase);

        return rule.Operator == "textEq" ? same : !same;
    }

    private static bool TryNumber(object? value, out decimal number)
    {
        switch (value)
        {
            case decimal d:
                number = d;
                return true;
            // ⛔ Цілих `ToRuleValue` числом не віддає: `long` — елемент довідника,
            // `int` — одиниця виміру. Це ідентифікатори, а людина в комірці бачить
            // назву, тож «не дорівнює 100» для них — не про число (паритет із
            // клієнтом, `conditional-format-parity.json`).
            case double or float:
                try
                {
                    number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (OverflowException)
                {
                    number = 0;
                    return false;
                }
            default:
                number = 0;
                return false;
        }
    }

    private static bool TryOperand(string? text, out decimal number)
    {
        number = 0;

        return !string.IsNullOrWhiteSpace(text)
               && decimal.TryParse(
                   text.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out number);
    }
}
