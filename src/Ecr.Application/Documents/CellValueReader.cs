// src/Ecr.Application/Documents/CellValueReader.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Приводить значення комірки з запиту до <see cref="CellValueData"/> за
/// **оголошеним типом колонки**.
/// </summary>
/// <remarks>
/// ⛔ Уведений після аудиту (`A7-01`). До нього значення розбиралося
/// <b>за типом CLR</b>: <c>value switch { decimal d => …, int i => …, bool b => … }</c>.
/// Через HTTP такого типу не буває взагалі. <c>PatchCell.Value</c> оголошено
/// як <c>object?</c>, і <c>System.Text.Json</c> віддає в нього
/// <see cref="JsonElement"/> — не число, не рядок, не булеве. Тому кожна гілка
/// промахувалася, спрацьовував запасний варіант, і **будь-яке число, дата й
/// булеве значення, надіслані через API, лягали в базу текстом**.
/// <para>
/// Тестами це не ловилося: тести застосунку конструюють <c>PatchCell</c>
/// напряму, кладучи справжній <c>decimal</c>. Дефект існує рівно на межі
/// «HTTP → обробник», якої жоден із них не переходить.
/// </para>
/// <para>
/// ⚠ Тип диктує <b>колонка</b>, а не вміст значення. Колонка <c>Lookup</c>
/// зберігає <c>ValueRegistryEntryId</c>, колонка <c>Unit</c> —
/// <c>ValueUnitId</c>, і обидві приходять числом: розрізнити їх за виглядом
/// значення неможливо, а покласти ідентифікатор запису довідника в
/// <c>ValueNumeric</c> означає втратити зв'язок із довідником (R-A4).
/// </para>
/// </remarks>
public static class CellValueReader
{
    /// <summary>Код помилки невідповідності типу (`02-contracts.md` §7).</summary>
    public const string TypeMismatch = "ECR-CELL-0422";

    /// <summary>
    /// Розгортає значення до примітиву CLR.
    /// </summary>
    /// <param name="raw">Значення з запиту: <see cref="JsonElement"/> або тип CLR.</param>
    /// <remarks>
    /// ⚠ Потрібне валідації: правила порівнюють значення з числами й рядками,
    /// а <see cref="JsonElement"/> не дорівнює нічому з них. Без розгортання
    /// правило «обсяг більший за нуль» мовчки не спрацьовувало б — не
    /// падало б, а саме <b>не знаходило порушень</b>.
    /// </remarks>
    public static object? Normalize(object? raw)
    {
        if (raw is not JsonElement element)
        {
            return raw;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetDecimal(out var number)
                ? number

                // Число, що не вміщується в decimal, — не число вимірювання.
                // Округлити його означало б записати вигадку (D-30).
                : element.GetRawText(),
            _ => element.GetRawText(),
        };
    }

    /// <summary>
    /// Будує значення комірки за описом колонки.
    /// </summary>
    /// <param name="raw">Значення з запиту.</param>
    /// <param name="column">Опис колонки; його <c>DataType</c> і вирішує.</param>
    /// <returns>Значення для запису; <c>null</c> — комірку треба стерти (R-B4).</returns>
    /// <exception cref="BusinessRuleException">
    /// Значення не відповідає типу колонки — <c>ECR-CELL-0422</c>.
    /// </exception>
    public static CellValueData? Read(object? raw, ColumnDef column)
    {
        ArgumentNullException.ThrowIfNull(column);

        var value = Normalize(raw);

        if (value is null)
        {
            return null;
        }

        return column.DataType switch
        {
            CellDataType.Int or CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated
                => new CellValueData { ValueNumeric = Number(value, column) },

            CellDataType.Bool => new CellValueData { ValueBool = Boolean(value, column) },
            CellDataType.Date => new CellValueData { ValueDate = Date(value, column) },

            // ⚠ Довідникова комірка тримає ІДЕНТИФІКАТОР запису, а не число
            // (R-A4). Код запису в неї не кладеться: його резолвить той, хто
            // будує запит, — імпорт робить це явно, і саме тому там є доступ
            // до довідника, а тут його немає.
            CellDataType.Lookup => new CellValueData { ValueRegistryEntryId = Identifier(value, column) },
            CellDataType.Unit => new CellValueData { ValueUnitId = Identifier(value, column) },

            _ => new CellValueData { ValueString = Text(value) },
        };
    }

    /// <summary>Число з урахуванням того, що JSON міг привезти його рядком.</summary>
    /// <remarks>
    /// ⛔ Нерозпізнане число — <b>відмова</b>, а не нуль і не текст у числовій
    /// колонці. Нуль у звіті читається як вимірювання, якого не робили.
    /// </remarks>
    private static decimal Number(object value, ColumnDef column) => value switch
    {
        decimal number => number,
        int number => number,
        long number => number,
        short number => number,
        double number => (decimal)number,
        float number => (decimal)number,
        bool flag => flag ? 1m : 0m,
        string text when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => throw Mismatch(column, value, "число"),
    };

    private static bool Boolean(object value, ColumnDef column) => value switch
    {
        bool flag => flag,
        decimal number => number != 0m,
        string text when bool.TryParse(text, out var parsed) => parsed,
        _ => throw Mismatch(column, value, "булеве значення"),
    };

    /// <summary>Дата; через JSON вона завжди приходить рядком.</summary>
    private static DateTime Date(object value, ColumnDef column) => value switch
    {
        DateTime date => date,
        DateTimeOffset offset => offset.UtcDateTime,
        string text when DateTime.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed) => parsed,
        _ => throw Mismatch(column, value, "дата"),
    };

    /// <summary>Ідентифікатор запису довідника або одиниці.</summary>
    private static int Identifier(object value, ColumnDef column) => value switch
    {
        int identifier => identifier,
        long identifier when identifier is >= int.MinValue and <= int.MaxValue => (int)identifier,
        decimal identifier when decimal.Truncate(identifier) == identifier
                                && identifier is >= int.MinValue and <= int.MaxValue
            => (int)identifier,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => throw Mismatch(column, value, "ідентифікатор"),
    };

    private static string Text(object value) => value switch
    {
        string text => text,
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Відмова з назвою колонки і тим, що саме очікувалося.
    /// </summary>
    /// <remarks>
    /// ⚠ У повідомленні є <b>код колонки й очікуваний тип</b>, але немає
    /// самого значення: воно може бути персональними даними, а текст помилки
    /// іде і в лог, і клієнту (ФВ-6.11).
    /// </remarks>
    private static BusinessRuleException Mismatch(ColumnDef column, object value, string expected)
        => new(
            TypeMismatch,
            $"Колонка «{column.Code}» очікує {expected}.",
            new Dictionary<string, object?>
            {
                ["columnCode"] = column.Code,
                ["expected"] = expected,
                ["actualKind"] = value.GetType().Name,
            });
}
