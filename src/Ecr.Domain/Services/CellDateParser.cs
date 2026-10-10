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

        // ⚠ Фолбек лишається, але вже НЕ бачить неоднозначного `d.M.yyyy`:
        // його перехопив точний розбір вище. Тут доїжджають формати з мітками
        // часу й зонами, які `ExactFormats` не перелічує.
        // ⛔ Y5-02: і НЕ бачить слеш-дат — Invariant читає їх як `M/d`, тобто
        // `4/13/2024` ставало 13 квітня поруч із `4/1/2024` = 4 січня з точного розбору.
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            value = default;
            return false;
        }

        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, Styles, out value);
    }
}
