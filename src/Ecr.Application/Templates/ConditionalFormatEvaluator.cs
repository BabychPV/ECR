using System.Globalization;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Templates;

/// <summary>
/// Результат спрацювання правила умовного форматування (ФВ-2.6/2.7): колір
/// <c>#rrggbb</c> (або <c>null</c> — колір теми) і жирність.
/// </summary>
public sealed record CellFormatDto(string? BackgroundHex, string? ForegroundHex, bool IsBold);

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

    /// <summary>Формат комірки зі значенням <paramref name="value"/>; <c>null</c> — жодне правило не спрацювало.</summary>
    /// <param name="columnRules">Правила ОДНІЄЇ колонки в порядку застосування (<see cref="ByColumn"/>).</param>
    /// <param name="value">Значення як у сітці (<c>CellValueMapping.ToRuleValue</c>).</param>
    public static CellFormatDto? Evaluate(IReadOnlyList<ConditionalFormatRule> columnRules, object? value)
    {
        ArgumentNullException.ThrowIfNull(columnRules);

        foreach (var rule in columnRules)
        {
            if (Matches(rule, value))
            {
                return new CellFormatDto(rule.BackgroundHex, rule.ForegroundHex, rule.IsBold);
            }
        }

        return null;
    }

    private static bool Matches(ConditionalFormatRule rule, object? value)
    {
        var isEmpty = value is null || (value is string text && string.IsNullOrWhiteSpace(text));

        switch (rule.Operator)
        {
            case "empty":
                return isEmpty;
            case "notEmpty":
                return !isEmpty;
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
