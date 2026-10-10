// src/Ecr.Domain/Services/CellDateParser.cs
using System.Globalization;

namespace Ecr.Domain.Services;

/// <summary>
/// Розбір дати з тексту — одне місце на всі шляхи введення (API, імпорт Excel,
/// довідники).
/// </summary>
/// <remarks>
/// ⛔ Існує через аудит 2026-09-16, §8.2. Шляхи розбирали дату через
/// <c>DateTime.TryParse(text, CultureInfo.InvariantCulture, …)</c>, а
/// InvariantCulture читає <c>M.d.yyyy</c> — американський порядок. Тому
/// <c>"1.4.2024"</c> ставало <b>4 січня</b>, а не 1 квітня: українець, що вводить
/// дату в природному порядку <c>d.MM.yyyy</c> (звичне явище при копіюванні або
/// ручному вводі в Date-комірку), отримував тихо неправильну дату БЕЗ жодного
/// попередження. У системі звітності це дата виміру, яка визначає, у який період
/// потрапить число.
///
/// ⛔ Тому спершу — <c>TryParseExact</c> з ЯВНИМ переліком форматів, у порядку
/// однозначності: ISO (<c>yyyy-MM-dd</c>) не переставляється взагалі, далі
/// українські <c>d.M.yyyy</c>/<c>dd.MM.yyyy</c>. Лише якщо жоден не збігся —
/// загальний <c>TryParse</c>, щоб не зламати формати, які тут уже приймалися
/// (наприклад <c>"2024-04-01T10:30:00Z"</c> з мітками часу).
///
/// ⚠ Порядок форматів має значення рівно в одному місці й саме там, де ціна
/// помилки найвища: <c>1.4.2024</c> збігається і з <c>d.M.yyyy</c>, і з
/// <c>M.d.yyyy</c>. Перемагає український — той, у якому цю дату й написали.
///
/// ⚠ 2026-09-23: клас перенесено з <c>Ecr.Application.Documents</c> у Domain,
/// щоб <c>RegistryValue.Set</c> (значення поля довідника типу <c>Date</c>) міг
/// викликати той самий розбір, а не дублювати його голим
/// <c>DateTime.TryParse</c> — Domain нічого не референсить (`LayerRulesTests`,
/// правило 1), тож спільний розбір може жити лише тут; Application лишається
/// споживачем, як і був.
///
/// ⛔ Y5-02 (аудит 7): СЛЕШ-дата, де обидві перші частини ≤ 12 і різні
/// (<c>4/1/2024</c>), — НЕОДНОЗНАЧНА, і розбір відмовляє, а не вгадує. Excel
/// пише дату в CSV коротким форматом регіону: у en-US це <c>M/d/yyyy</c>, у
/// en-GB/uk — <c>d/M/yyyy</c>, і з самого рядка порядок не відновити. Доти
/// <c>4/1/2024</c> збігався з точним <c>d/M/yyyy</c> (4 січня), а
/// <c>4/13/2024</c> провалювався у фолбек Invariant (<c>M/d</c>, 13 квітня):
/// в одному файлі частина дат тихо мінялася місцями. Те саме правило, що для
/// чисел (рішення 2026-09-29: «неоднозначне — відмова»). Фолбек для
/// слеш-дат прибрано: що не пройшло точних форматів, далі не вгадується.
/// Однозначні слеш-дати (<c>13/4/2024</c>, <c>4/4/2024</c>, <c>2024/04/01</c>)
/// читаються, як і раніше.
///
/// ⛔ Z3-01 (аудит 8): те саме правило — для ВСІХ числових дат із днем або
/// місяцем спереду, не лише слеш-дат. Доти фолбек Invariant отримував усе, чого
/// не було в точному переліку, і читав це як <c>M-d</c>: <c>01.04.2024 9:05</c>
/// (стандартна дата-час Excel у регіонах ru/kk, година без нуля) ставало
/// 4 січня, а <c>01.04.2024 10:30</c> — 1 квітня; так само <c>1.4.2024 10:30</c>,
/// <c>01.04.24</c>, <c>01-04-2024</c>. Тепер: крапкова дата з днем спереду й
/// часом — точним форматом (<c>d.M.yyyy H:mm[:ss]</c>), а все інше з 1–2
/// цифрами й роздільником <c>.</c>/<c>-</c>/<c>/</c> на початку, що не пройшло
/// точних форматів, — відмова (<see cref="IsRefusedDayMonthDate"/> дає читачам
/// окремий ключ із поясненням, Z3-02). Двозначний рік (<c>01.04.24</c>) і дефіс
/// з днем спереду (<c>01-04-2024</c>) свідомо не приймаються: порядок і століття
/// з рядка не відновити, а відмова даних не псує. Рік спереду (<c>2024-04-01</c>,
/// ISO з часом і зоною) має 4 цифри до роздільника і йде, як і раніше.
/// </remarks>
public static class CellDateParser
{
    /// <summary>
    /// Формати, які розбираються ДО загального <c>TryParse</c>, у порядку
    /// спадання однозначності.
    /// </summary>
    private static readonly string[] ExactFormats =
    [
        // Однозначні: рік спереду, переставити день і місяць неможливо.
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffffK",

        // Український порядок — саме він і переставлявся.
        "dd.MM.yyyy",
        "d.M.yyyy",
        "dd.MM.yyyy HH:mm",
        "dd.MM.yyyy HH:mm:ss",

        // Z3-01: дата-час Excel у регіонах ru/kk — `01.04.2024 9:05`, година без нуля.
        // `HH` вимагає двох цифр, тож без цих форматів такий рядок падав у фолбек M-d.
        "d.M.yyyy H:mm",
        "d.M.yyyy H:mm:ss",
        "dd'/'MM'/'yyyy",
        "d'/'M'/'yyyy",

        // Рік спереду через слеш — однозначний; доти його читав фолбек, якого для
        // слеш-дат більше немає (Y5-02).
        "yyyy'/'MM'/'dd",
        "yyyy'/'M'/'d",
    ];

    /// <summary>
    /// Чи слеш-дата неоднозначна: обидві перші частини — числа від 1 до 12 і різні
    /// (<c>4/1/2024</c> — 1 квітня в en-US і 4 січня в en-GB/uk).
    /// </summary>
    /// <param name="text">Текст дати.</param>
    public static bool IsAmbiguousSlashDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('/');

        return parts.Length == 3
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var second)
               && first is >= 1 and <= 12
               && second is >= 1 and <= 12
               && first != second;
    }

    /// <summary>
    /// Чи текст — числова дата з днем або місяцем спереду (<c>4/1/2024</c>,
    /// <c>01-04-2024</c>, <c>01.04.24</c>), яку розбір свідомо відхилив, бо
    /// порядок дня й місяця з рядка не відновити (Y5-02, Z3-01).
    /// </summary>
    /// <remarks>
    /// Для читачів (комірка, шапка, імпорт, довідник), щоб відмова пояснювала, як
    /// записати дату, а не казала «не є датою» на значенні, яке на вигляд — дата (Z3-02).
    /// </remarks>
    /// <param name="text">Текст дати.</param>
    public static bool IsRefusedDayMonthDate(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && ClosedToFallback(text.Trim())
           && !TryParse(text, out _);

    /// <summary>Розбирає дату з тексту; <c>false</c> — формат не розпізнано.</summary>
    /// <param name="text">Текст дати.</param>
    /// <param name="value">Розібрана дата в UTC.</param>
    public static bool TryParse(string? text, out DateTime value)
    {
        value = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        const DateTimeStyles Styles =
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;

        // ⛔ Y5-02: неоднозначна слеш-дата — відмова, а не вгадування порядку.
        if (IsAmbiguousSlashDate(trimmed))
        {
            return false;
        }

        if (DateTime.TryParseExact(trimmed, ExactFormats, CultureInfo.InvariantCulture, Styles, out value))
        {
            return true;
        }

        // ⚠ Фолбек лишається для форматів із роком спереду, мітками часу й
        // зонами, які `ExactFormats` не перелічує (`2024-04-01T10:30:00Z`).
        // ⛔ Y5-02: і НЕ бачить слеш-дат — Invariant читає їх як `M/d`, тобто
        // `4/13/2024` ставало 13 квітня поруч із `4/1/2024` = 4 січня з точного розбору.
        // ⛔ Z3-01: і НЕ бачить жодної числової дати з днем або місяцем спереду —
        // `01.04.2024 9:05`, `01.04.24`, `01-04-2024` Invariant так само читав як M-d.
        if (ClosedToFallback(trimmed))
        {
            value = default;
            return false;
        }

        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, Styles, out value);
    }

    /// <summary>
    /// Чи текст не можна віддавати фолбеку Invariant: є слеш, або на початку 1–2 цифри
    /// і роздільник <c>.</c>/<c>-</c> — тобто день чи місяць спереду, які Invariant
    /// прочитав би в порядку M-d. Рік спереду (4 цифри) сюди не потрапляє.
    /// </summary>
    private static bool ClosedToFallback(string trimmed)
    {
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            return true;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        return digits is 1 or 2
               && digits < trimmed.Length
               && trimmed[digits] is '.' or '-';
    }
}
