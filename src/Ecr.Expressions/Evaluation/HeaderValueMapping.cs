using Ecr.Domain.ValueObjects;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Переклад між збереженим значенням поля шапки документа і значенням виразу
/// — за зразком <see cref="CellValueMapping"/>, але без гілки помилки: шапка
/// не матеріалізує обчислені значення (<c>#DIV/0</c> у ній не буває).
/// </summary>
public static class HeaderValueMapping
{
    /// <summary>Збережене значення поля шапки як значення виразу.</summary>
    /// <param name="header">Значення поля; <c>null</c> — рядка немає.</param>
    public static ExpressionValue ToExpressionValue(DocumentHeaderValueData? header)
    {
        if (header is null || header.IsEmpty)
        {
            return ExpressionValue.Null;
        }

        if (header.ValueNumeric is { } number)
        {
            return ExpressionValue.Number(number);
        }

        if (header.ValueDate is { } date)
        {
            return ExpressionValue.Date(date);
        }

        if (header.ValueBool is { } flag)
        {
            return ExpressionValue.Boolean(flag);
        }

        if (header.ValueString is { } text)
        {
            return ExpressionValue.Text(text);
        }

        return ExpressionValue.Null;
    }

    /// <summary>
    /// Збережене значення поля шапки як «сире» значення для клієнта — той
    /// самий принцип одного розгортання, що <see cref="CellValueMapping.ToRuleValue"/>.
    /// </summary>
    /// <param name="header">Значення поля; <c>null</c> — рядка немає.</param>
    public static object? ToRuleValue(DocumentHeaderValueData? header)
    {
        if (header is null || header.IsEmpty)
        {
            return null;
        }

        if (header.ValueNumeric is { } number)
        {
            return number;
        }

        if (header.ValueDate is { } date)
        {
            return date;
        }

        if (header.ValueBool is { } flag)
        {
            return flag;
        }

        if (header.ValueString is { } text)
        {
            return text;
        }

        if (header.ValueRegistryEntryId is { } entry)
        {
            return entry;
        }

        return header.ValueUnitId;
    }
}
