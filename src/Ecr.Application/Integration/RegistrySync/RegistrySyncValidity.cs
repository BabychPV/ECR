// src/Ecr.Application/Integration/RegistrySync/RegistrySyncValidity.cs
using System.Globalization;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Integration.RegistrySync;

/// <summary>
/// Звідки синк бере вікно дії запису (<c>D-212</c> (8), PR-7): атрибути елемента AF,
/// межа кінця й часовий пояс AF. Задається лише для ТЕМПОРАЛЬНОГО довідника.
/// </summary>
/// <param name="FromAttribute">Атрибут початку (<c>ext.SourceEntity.ValidFromAttribute</c>); <c>null</c> — початок не синхронізується.</param>
/// <param name="ToAttribute">Атрибут кінця (<c>ValidToAttribute</c>); <c>null</c> — кінець не синхронізується.</param>
/// <param name="ToInclusive">
/// Значення кінця — ОСТАННІЙ чинний день (<c>ValidToInclusive</c>); синк додає день, бо
/// <c>RegistryEntry.ValidTo</c> — перший НЕчинний.
/// </param>
/// <param name="TimeZone">Пояс, у якому дата AF стає календарним днем (<see cref="RegistrySyncValidity.TimeZoneKey"/>).</param>
public sealed record RegistrySyncValiditySource(
    string? FromAttribute,
    string? ToAttribute,
    bool ToInclusive,
    TimeZoneInfo TimeZone);

/// <summary>Зміна вікна дії наявного запису: <c>RegistryEntryUpdate.Validity</c>.</summary>
/// <param name="RegistryEntryId">Запис.</param>
/// <param name="ExternalId">Елемент джерела — для подій.</param>
/// <param name="Old">Поточне вікно.</param>
/// <param name="New">Вікно з джерела.</param>
public sealed record RegistrySyncValidityChange(
    long RegistryEntryId,
    string ExternalId,
    ValidityWindow Old,
    ValidityWindow New);

/// <summary>
/// Дати AF → вікно дії запису <c>[ValidFrom, ValidTo)</c> (<c>D-212</c> (8), PR-7).
/// </summary>
/// <remarks>
/// ⚠ Формат дати — ISO 8601, як його дає <c>PiSqlClientDataSource.Value()</c> (<c>"O"</c>, D-212 PR-1):
/// <c>yyyy-MM-ddTHH:mm:ss[.fffffff][Z|±hh:mm]</c> або лише <c>yyyy-MM-dd</c>. Будь-що інше
/// (число, дата за культурою) — відмова <see cref="DateInvalidKey"/>, межа не змінюється:
/// «вгадана» дата зсунула б вікно, і помітити це було б нікому. Які формати віддає AF
/// замовника — факт PI-адміна (FEATURE-REGISTRY-SYNC §2.1); ISO — дефолт до відповіді.
/// <para>
/// ⚠ Час із поясом (<c>Z</c> чи зсув) — момент: переводиться в <see cref="RegistrySyncValiditySource.TimeZone"/>
/// і лише тоді стає днем. Без поясу — це вже місцевий час AF. Північ Києва, записана як
/// <c>2024-12-31T22:00:00Z</c>, — це 1 січня, а не 31 грудня.
/// </para>
/// <para>
/// ⚠ Межі до дня: початок — день моменту (день, у якому запис стає чинним, чинний);
/// виключний кінець — перший день, що починається НЕ раніше моменту (північ — сам день,
/// будь-який інший час — наступний: частково чинний день лишається чинним, як відрізок у
/// <see cref="ValidityWindow.OverlapsSegment"/>); включний кінець — день моменту + 1.
/// </para>
/// </remarks>
public static class RegistrySyncValidity
{
    /// <summary>Ключ конфігурації: пояс AF (Windows або IANA); порожньо — UTC.</summary>
    public const string TimeZoneKey = "Integration:AfTimeZoneId";

    /// <summary>«Поле» подій про початок вікна.</summary>
    public const string FromFieldCode = "@validFrom";

    /// <summary>«Поле» подій про кінець вікна.</summary>
    public const string ToFieldCode = "@validTo";

    /// <summary>«Поле» подій і аудиту про вікно цілком.</summary>
    public const string WindowFieldCode = "@validity";

    /// <summary>Ключ відмови: значення атрибута дати — не дата ISO 8601.</summary>
    public const string DateInvalidKey = "err.ECR-REG-0422.validityDateInvalid";

    /// <summary>Ключ відмови: дати джерела дають порожнє вікно (кінець не пізніше початку).</summary>
    public const string WindowEmptyKey = "err.ECR-REG-0422.validityWindowEmptyInSource";

    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd",
    ];

    /// <summary>Ключ відмови: рядок значення поля типу <c>Date</c> не в дозволеному форматі (<c>MM/dd/yyyy</c> чи ISO 8601).</summary>
    public const string DateFormatRefusedKey = "err.ECR-REG-0422.dateFormatRefused";

    /// <summary>Формат дати-рядка в AF замовника (<c>Date_Issue</c>: «04/29/2026»), відповідь людини 2026-10-01.</summary>
    public const string FieldDateFormat = "MM/dd/yyyy";

    private static readonly string[] FieldDateFormats = [.. Formats, FieldDateFormat];

    /// <summary>
    /// Рядок AF → значення поля довідника типу <c>Date</c> (UTC). Лише ЯВНІ формати: ISO 8601 і
    /// <c>MM/dd/yyyy</c>, завжди <see cref="CultureInfo.InvariantCulture"/> (культура потоку
    /// змінює роздільники: під ru-RU «04.29.2026» став би прийнятним — доведено мутацією).
    /// </summary>
    /// <remarks>
    /// ⛔ Жодного <c>DateTime.Parse</c>/фолбеку: «29/04/2026» — відмова, а не 29 квітня; «01/04/2026»
    /// — 4 січня (формат один, MM/dd — рішення людини, не здогад).
    /// </remarks>
    /// <param name="text">Непорожній рядок.</param>
    /// <param name="value">Дата в UTC.</param>
    /// <returns><c>false</c> — формат не дозволено.</returns>
    public static bool TryParseFieldDate(string text, out DateTime value)
        => DateTime.TryParseExact(
            text.Trim(),
            FieldDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out value);

    /// <summary>Пояс AF за ідентифікатором конфігурації.</summary>
    /// <param name="id">Ідентифікатор Windows чи IANA; порожній — UTC.</param>
    /// <exception cref="InvalidOperationException">Пояс невідомий — з ім'ям ключа.</exception>
    public static TimeZoneInfo ResolveTimeZone(string? id)
        => TryResolveTimeZone(id, out var zone)
            ? zone
            : throw new InvalidOperationException(
                $"{TimeZoneKey} = «{id}»: невідомий часовий пояс (очікується ідентифікатор Windows чи IANA, напр. "
                + "FLE Standard Time або Europe/Kyiv; порожньо — UTC).");

    /// <summary>Пояс AF без винятку — для перевірки конфігурації на старті.</summary>
    /// <param name="id">Ідентифікатор; порожній — UTC.</param>
    /// <param name="zone">Пояс.</param>
    public static bool TryResolveTimeZone(string? id, out TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }

    /// <summary>Значення атрибута AF → межа вікна (день).</summary>
    /// <param name="raw">Значення: рядок ISO, <see cref="DateTime"/>, <see cref="DateTimeOffset"/>; порожнє — «межі немає».</param>
    /// <param name="zone">Пояс AF.</param>
    /// <param name="bound">Яка межа.</param>
    /// <param name="day">День межі; <c>null</c> — без обмеження.</param>
    /// <returns><c>false</c> — значення не дата.</returns>
    public static bool TryParseBound(object? raw, TimeZoneInfo zone, ValidityBound bound, out DateOnly? day)
    {
        ArgumentNullException.ThrowIfNull(zone);

        day = null;
        DateTime local;

        switch (raw)
        {
            case null:
                return true;
            case string text when string.IsNullOrWhiteSpace(text):
                return true;
            case string text:
                if (!DateTime.TryParseExact(
                        text.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    return false;
                }

                // ⚠ Зсув (Kind = Local) — точний момент беремо з DateTimeOffset: DateTime вже перевела
                // його в пояс МАШИНИ, і в годину переходу на літній час зворотний перевід неоднозначний.
                local = parsed.Kind == DateTimeKind.Local
                        && DateTimeOffset.TryParseExact(
                            text.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)
                    ? TimeZoneInfo.ConvertTimeFromUtc(exact.UtcDateTime, zone)
                    : ToLocal(parsed, zone);
                break;
            case DateTime moment:
                local = ToLocal(moment, zone);
                break;
            case DateTimeOffset offset:
                local = TimeZoneInfo.ConvertTimeFromUtc(offset.UtcDateTime, zone);
                break;
            default:
                return false;
        }

        var date = DateOnly.FromDateTime(local);
        day = bound switch
        {
            ValidityBound.From => date,
            ValidityBound.ToInclusive => date.AddDays(1),
            _ => local.TimeOfDay == TimeSpan.Zero ? date : date.AddDays(1),
        };
        return true;
    }

    /// <summary>Вікно як текст для подій і аудиту: <c>[2024-01-01, ∞)</c>.</summary>
    /// <param name="window">Вікно.</param>
    public static string Text(ValidityWindow window)
        => $"[{Day(window.FromInclusive, "-∞")}, {Day(window.ToExclusive, "∞")})";

    /// <summary>
    /// Вікно з атрибутів елемента. Межа, чийого атрибута немає в політиці чи в знімку, лишається
    /// поточною; невалідна — подія <see cref="RegistrySyncEventKind.ValueRejected"/>, межа лишається.
    /// </summary>
    /// <returns>Нове вікно, якщо воно інше за <paramref name="current"/>; інакше <c>null</c>.</returns>
    internal static ValidityWindow? Plan(
        RegistrySyncValiditySource? source,
        RegistrySyncSourceElement element,
        ValidityWindow current,
        long? registryEntryId,
        List<RegistrySyncEvent> events)
    {
        if (source is null)
        {
            return null;
        }

        var from = Bound(source, source.FromAttribute, ValidityBound.From, FromFieldCode, current.FromInclusive);
        var to = Bound(
            source,
            source.ToAttribute,
            source.ToInclusive ? ValidityBound.ToInclusive : ValidityBound.ToExclusive,
            ToFieldCode,
            current.ToExclusive);

        var window = new ValidityWindow(from, to);
        if (window == current)
        {
            return null;
        }

        if (window.IsEmpty)
        {
            // Той самий інваріант, що в RegistryEntry.SetValidity (ECR-REG-0422): порожнє вікно —
            // запис, якого не видно ніколи. Писати його синк не має права; видно — подією.
            events.Add(new RegistrySyncEvent(
                RegistrySyncEventKind.ValueRejected, element.ExternalId, registryEntryId, WindowFieldCode,
                Text(current), Text(window), RegistrySyncPlanner.ValueRejectedCode, WindowEmptyKey));
            return null;
        }

        return window;

        DateOnly? Bound(
            RegistrySyncValiditySource s, string? attribute, ValidityBound bound, string fieldCode, DateOnly? keep)
        {
            // Атрибута немає в знімку — джерело нічого не сказало: межа не чіпається (як поле).
            if (attribute is null || !element.Attributes.TryGetValue(attribute, out var raw))
            {
                return keep;
            }

            if (TryParseBound(raw, s.TimeZone, bound, out var day))
            {
                return day;
            }

            events.Add(new RegistrySyncEvent(
                RegistrySyncEventKind.ValueRejected, element.ExternalId, registryEntryId, fieldCode,
                SourceValue: raw, ErrorCode: RegistrySyncPlanner.ValueRejectedCode, MessageKey: DateInvalidKey));
            return keep;
        }
    }

    private static DateTime ToLocal(DateTime moment, TimeZoneInfo zone) => moment.Kind switch
    {
        // Без поясу — уже місцевий час AF.
        DateTimeKind.Unspecified => moment,
        _ => TimeZoneInfo.ConvertTimeFromUtc(moment.ToUniversalTime(), zone),
    };

    private static string Day(DateOnly? day, string none)
        => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? none;
}

/// <summary>Яка межа вікна розбирається.</summary>
public enum ValidityBound
{
    /// <summary>Перший чинний день.</summary>
    From,

    /// <summary>Кінець — перший НЕчинний момент.</summary>
    ToExclusive,

    /// <summary>Кінець — останній чинний день.</summary>
    ToInclusive,
}
