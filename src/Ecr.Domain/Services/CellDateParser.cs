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
    ];

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

        if (DateTime.TryParseExact(trimmed, ExactFormats, CultureInfo.InvariantCulture, Styles, out value))
        {
            return true;
        }

        // ⚠ Фолбек лишається, але вже НЕ бачить неоднозначного `d.M.yyyy`:
        // його перехопив точний розбір вище. Тут доїжджають формати з мітками
        // часу й зонами, які `ExactFormats` не перелічує.
        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, Styles, out value);
    }
}
