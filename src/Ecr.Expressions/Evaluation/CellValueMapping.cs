using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Переклад між значенням виразу і збереженим значенням комірки.
/// </summary>
/// <remarks>
/// ⚠ Найважливіше правило тут — доля ПОМИЛКИ. Вона зберігається як
/// <c>IsEmpty = 0</c>, <c>ValueString = '#DIV/0'</c>, <c>IsCalculated = 1</c>
/// (02b §6.4), а не як порожня комірка. Різниця не косметична: порожня комірка
/// в звіті виглядає як «ще не заповнили», і зіпсоване число знайшли б аж на
/// звірці. Видимий <c>#DIV/0</c> знаходять одразу.
/// </remarks>
public static class CellValueMapping
{
    /// <summary>Значення виразу як значення комірки.</summary>
    public static CellValueData ToCellValue(ExpressionValue value)
        => value.Type switch
        {
            ExpressionValueType.Error => new CellValueData
            {
                ValueString = value.ErrorCode,
                IsCalculated = true,
            },
            ExpressionValueType.Number => new CellValueData
            {
                ValueNumeric = (decimal)value.Value!,
                IsCalculated = true,
            },
            ExpressionValueType.Text => new CellValueData
            {
                ValueString = (string)value.Value!,
                IsCalculated = true,
            },
            ExpressionValueType.Boolean => new CellValueData
            {
                ValueBool = (bool)value.Value!,
                IsCalculated = true,
            },
            ExpressionValueType.Date => new CellValueData
            {
                ValueDate = (DateTime)value.Value!,
                IsCalculated = true,
            },

            // Обчислена порожнеча — саме ЯВНА порожнеча: формула відпрацювала
            // і дала «нічого». Відсутність рядка означала б «ще не рахували».
            _ => new CellValueData { IsEmpty = true, IsCalculated = true },
        };

    /// <summary>Збережене значення комірки як значення виразу.</summary>
    /// <param name="cell">Значення комірки; <c>null</c> — рядка немає.</param>
    /// <param name="defaultValue">
    /// <c>DefaultValue</c> колонки; застосовується ЛИШЕ коли комірки немає
    /// зовсім. Явна порожнеча його не бере (02b §6.3).
    /// </param>
    public static ExpressionValue ToExpressionValue(CellValueData? cell, ExpressionValue defaultValue)
    {
        if (cell is null)
        {
            return defaultValue;
        }

        if (cell.IsEmpty)
        {
            return ExpressionValue.Null;
        }

        if (cell.ValueNumeric is { } number)
        {
            return ExpressionValue.Number(number);
        }

        if (cell.ValueDate is { } date)
        {
            return ExpressionValue.Date(date);
        }

        if (cell.ValueBool is { } flag)
        {
            return ExpressionValue.Boolean(flag);
        }

        if (cell.ValueString is { } text)
        {
            // Помилка, збережена як текст, повертається помилкою — інакше
            // #DIV/0 брав би участь у наступному обчисленні як рядок.
            return text.StartsWith('#')
                ? ExpressionValue.Error(text)
                : ExpressionValue.Text(text);
        }

        return ExpressionValue.Null;
    }
}
