// src/Ecr.Application/Documents/CellValueReader.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
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
    /// Масштаб сховища числа комірки: <c>doc.CellValue.ValueNumeric decimal(34,16)</c>
    /// (<c>D-148</c>; <c>NormalizedCellStore.NumericScale</c>,
    /// <c>CellValueConfiguration</c>, <c>doc.CellValueTvp</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ `U-23`. Знаків після коми, більших за цю межу, сховище не тримає:
    /// <c>SqlMetaData.Adjust</c> округлює їх на КЛІЄНТІ ще до відправки, тож
    /// СУБД чесно зберігає вже огризок, а запит закінчується «Saved». Доти
    /// межу перевіряв лише оголошений <c>ColumnDef.Scale</c> — колонка без
    /// нього (більшість звітних) приймала <c>931.9250000000000000123</c> і
    /// мовчки записувала <c>931.9250000000000000</c>.
    /// </remarks>
    public const int StorageScale = 16;

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
                => new CellValueData { ValueNumeric = Storable(Number(value, column), column) },

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
        _ => throw Mismatch(column, value, ExpectedType.Number),
    };

    /// <summary>
    /// Число, яке сховище збереже БЕЗ втрати; інакше — відмова (`U-23`).
    /// </summary>
    /// <remarks>
    /// ⛔ Відмова, а не округлення: це вже прийняте правило продукту
    /// (<c>ФВ-9.16c</c>, <c>D-116</c>, <c>features/grid/rounding.ts</c>) —
    /// «ручне введення і API зайвого знака не отримують, сервер відхиляє»;
    /// округлює лише ВСТАВКА, і робить це клієнт — видимо, з позначкою й
    /// лічильником. Округлити тут означало б тихо записати інше число, ніж
    /// бачить користувач: сітка після збереження показує ВЛАСНЕ введення
    /// оператора (<c>sliceApply.ts</c>), а не перечитане зі сховища.
    ///
    /// ⚠ Межа — масштаб СХОВИЩА, а не оголошений <c>ColumnDef.Scale</c>.
    /// Оголошений масштаб і далі перевіряє <c>ColumnDef.ValidateValue</c> (п. 7),
    /// тут він не дублюється; але він може бути й більшим за 16 — і тоді
    /// саме ця перевірка єдина стоїть між введенням і мовчазним огризком.
    ///
    /// ⚠ Нулі в хвості не рахуються: <c>1.50000000000000000000</c> —
    /// те саме число, і сховище збереже його без втрати.
    ///
    /// ⚠ Імпорт із Excel сюди з двійковим хвостом не доходить: його
    /// нормалізує <c>ImportDiffBuilder.Read</c> ДО прев'ю, бо
    /// <c>0.1 + 0.2</c> в аркуші — це <c>0.30000000000000004</c>, і відхиляти
    /// такі числа означало б зробити непридатним головний шлях введення.
    /// </remarks>
    private static decimal Storable(decimal number, ColumnDef column)
        => decimal.Round(number, StorageScale, MidpointRounding.AwayFromZero) == number
            ? number
            : throw new BusinessRuleException(
                TypeMismatch,
                $"Колонка «{column.Code}» зберігає не більше {StorageScale} знаків після коми.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CELL-0422.tooManyDecimals",
                    ["columnCode"] = column.Code,
                    ["maxScale"] = StorageScale.ToString(CultureInfo.InvariantCulture),
                });

    private static bool Boolean(object value, ColumnDef column) => value switch
    {
        bool flag => flag,
        decimal number => number != 0m,
        string text when bool.TryParse(text, out var parsed) => parsed,
        _ => throw Mismatch(column, value, ExpectedType.Boolean),
    };

    /// <summary>Дата; через JSON вона завжди приходить рядком.</summary>
    /// <remarks>
    /// ⛔ Розбір — через <see cref="CellDateParser"/>, а не голий
    /// <c>DateTime.TryParse(…, InvariantCulture, …)</c> (аудит 2026-09-16,
    /// §8.2): InvariantCulture читає <c>M.d.yyyy</c>, тож <c>"1.4.2024"</c>
    /// ставало 4 січня, а не 1 квітня — тихо переставлені день і місяць у даті,
    /// яка визначає період звітності.
    /// </remarks>
    private static DateTime Date(object value, ColumnDef column) => value switch
    {
        DateTime date => date,
        DateTimeOffset offset => offset.UtcDateTime,
        string text when CellDateParser.TryParse(text, out var parsed) => parsed,
        _ => throw Mismatch(column, value, ExpectedType.Date),
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
        _ => throw Mismatch(column, value, ExpectedType.Identifier),
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
    /// Очікуваний тип значення: ключ каталогу + запасне українське слово.
    /// </summary>
    /// <remarks>
    /// ⛔ Очікуваний тип — частина КЛЮЧА, а не підстановка в нього. Резолвер
    /// (<c>UiStringResolver.Format</c>) підставляє рядки як є і другого рівня
    /// розв'язання ключів не має: передати <c>{expected}</c> значенням
    /// означало б або лишити в англійському реченні українське «число», або
    /// завести поруч другий механізм локалізації. Чотири суфіксовані ключі
    /// дають ще й граматично правильне речення в кожній мові, чого підстановка
    /// одного слова не дає в принципі.
    ///
    /// ⚠ <see cref="Code"/> їде клієнтові полем <c>expected</c> у
    /// <c>problem+json</c> (<c>ExceptionHandlingMiddleware</c> копіює
    /// <c>Details</c> у розширення), тому це стале КОДОВЕ слово — як
    /// <c>{status}</c> і <c>{reason}</c> у сусідніх шаблонах сіду, — а не
    /// текст для показу. Раніше там лежало українське «число».
    /// </remarks>
    private sealed record ExpectedType(string Code, string MessageKey, string Fallback)
    {
        public static readonly ExpectedType Number =
            new("Number", "err.ECR-CELL-0422.expectsNumber", "число");

        public static readonly ExpectedType Boolean =
            new("Boolean", "err.ECR-CELL-0422.expectsBoolean", "булеве значення");

        public static readonly ExpectedType Date =
            new("Date", "err.ECR-CELL-0422.expectsDate", "дата");

        public static readonly ExpectedType Identifier =
            new("Identifier", "err.ECR-CELL-0422.expectsIdentifier", "ідентифікатор");
    }

    /// <summary>
    /// Відмова з назвою колонки і тим, що саме очікувалося.
    /// </summary>
    /// <remarks>
    /// ⚠ У повідомленні є <b>код колонки й очікуваний тип</b>, але немає
    /// самого значення: воно може бути персональними даними, а текст помилки
    /// іде і в лог, і клієнту (ФВ-6.11).
    ///
    /// ⛔ <c>messageKey</c> (`U-02`). Це найчастіша інтерактивна відмова
    /// продукту — її бачить кожен, хто набрав не той тип у комірку, — і доти
    /// вона їхала на екран готовим українським реченням під англійським
    /// заголовком. Речення лишається запасним: резолвер повертається до нього,
    /// коли ключа в каталозі немає.
    /// </remarks>
    private static BusinessRuleException Mismatch(ColumnDef column, object value, ExpectedType expected)
        => new(
            TypeMismatch,
            $"Колонка «{column.Code}» очікує {expected.Fallback}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = expected.MessageKey,
                ["columnCode"] = column.Code,
                ["expected"] = expected.Code,
                ["actualKind"] = value.GetType().Name,
            });
}
