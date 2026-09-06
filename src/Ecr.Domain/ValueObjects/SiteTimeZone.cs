// src/Ecr.Domain/ValueObjects/SiteTimeZone.cs

using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Domain.ValueObjects;

/// <summary>
/// Часовий пояс майданчика — **ідентифікатор IANA** (<c>Asia/Aqtau</c>), а не
/// зсув і не Windows-ідентифікатор (директива ПК-1 №06 §3).
/// </summary>
/// <remarks>
/// ⛔ Зсув (<c>+05:00</c>) не зберігається саме тому, що він МІНЯЄТЬСЯ.
/// Збережене число переживе перехід на літній час і почне брехати:
/// <c>Europe/Kyiv</c> на цій машині виміряно як <c>+02:00</c> 15 січня і
/// <c>+03:00</c> 15 липня. IANA-ідентифікатор — це правило, а не число.
///
/// ⛔ Windows-ідентифікатор теж не годиться, і це не смак. Виміряно на цій
/// машині (.NET 10.0.11, Windows 10.0.26200): <c>Asia/Aqtau</c> →
/// <c>West Asia Standard Time</c> → назад у IANA дає <c>Asia/Tashkent</c>.
/// Тобто Актау через Windows-ідентифікатор перетворюється на Ташкент —
/// сьогодні обидва <c>+05:00</c>, але це різні набори правил і різні держави,
/// і розійдуться вони мовчки.
///
/// ⚠ Перевірку не можна звести до <c>FindSystemTimeZoneById</c>. Виміряно:
/// на Windows він приймає І IANA (<c>Asia/Aqtau</c>, <c>HasIanaId=true</c>),
/// І Windows-ідентифікатор (<c>Central Asia Standard Time</c>,
/// <c>HasIanaId=false</c>), і навіть <c>UTC+13</c>. Тобто сам по собі він
/// вимогу «IANA» не забезпечує взагалі.
/// </remarks>
public readonly record struct SiteTimeZone
{
    private SiteTimeZone(string id) => Id = id;

    /// <summary>Ідентифікатор IANA, як його записали: <c>Asia/Aqtau</c>.</summary>
    public string Id { get; }

    /// <summary>
    /// Створює пояс майданчика або кидає доменний виняток.
    /// </summary>
    /// <param name="value">Ідентифікатор IANA.</param>
    /// <returns>Перевірений пояс.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-CFG-4221</c> — значення порожнє, не є ідентифікатором IANA або
    /// невідоме цій системі.
    /// </exception>
    /// <remarks>
    /// ⛔ <see cref="DomainException"/>, а не <c>ArgumentException</c> і не
    /// <c>TimeZoneNotFoundException</c>: конвеєр обробки помилок мапить
    /// доменний виняток у <c>422</c> з кодом і текстом, а решта провалюється
    /// в гілку «невідомий виняток» — тобто <c>500</c> «Внутрішня помилка».
    /// Той, хто зробив опечатку в поясі, побачив би аварію сервера замість
    /// назви поля.
    /// </remarks>
    public static SiteTimeZone Create(string? value)
        => TryCreate(value, out var zone)
            ? zone
            : throw new DomainException(
                ErrorCodes.ProjectTimeZoneNotIana,
                $"Часовий пояс майданчика «{value}» не є відомим ідентифікатором IANA "
                + "(наприклад, «Asia/Aqtau»). Windows-ідентифікатори на кшталт "
                + "«Central Asia Standard Time» і зсуви на кшталт «+05:00» не приймаються: "
                + "пояс задає межі періодів і позначки пізніх змін, і після відкриття "
                + "першого періоду його вже не змінити.");

    /// <summary>
    /// Перевіряє ідентифікатор без винятку.
    /// </summary>
    /// <param name="value">Ідентифікатор IANA.</param>
    /// <param name="zone">Результат; <c>default</c>, якщо значення не підходить.</param>
    /// <returns><c>true</c>, якщо значення — відомий системі пояс IANA.</returns>
    /// <remarks>
    /// ⚠ Перевірок ДВІ, і вони відповідають на різні питання.
    ///
    /// <c>TryConvertIanaIdToWindowsId</c> тут НЕ для конверсії — конвертувати
    /// нічого не треба, <c>FindSystemTimeZoneById</c> на цій машині читає IANA
    /// напряму (виміряно: 15 з 15 ідентифікаторів, <c>HasIanaId=true</c>).
    /// Він тут єдиний доступний спосіб СКАЗАТИ, що рядок узагалі є
    /// ідентифікатором IANA: виміряно на всіх 139 поясах CLDR цієї машини —
    /// жоден IANA не відкинутий, і з 141 Windows-ідентифікатора пройшов рівно
    /// один, <c>UTC</c>, який і в IANA існує. Заразом він чутливий до
    /// регістру (<c>asia/aqtau</c> → <c>false</c>), і це потрібно: у базі
    /// лежить рядок, і два написання того самого поясу були б двома різними
    /// значеннями.
    ///
    /// <c>FindSystemTimeZoneById</c> відповідає на друге питання — чи є в ЦІЄЇ
    /// системи правила цього поясу. Контейнер із урізаним <c>tzdata</c> знає
    /// не все, що знає CLDR, і пояс без правил не порахує жодної межі.
    /// </remarks>
    public static bool TryCreate(string? value, out SiteTimeZone zone)
    {
        zone = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out _))
        {
            return false;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(value);
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }

        zone = new SiteTimeZone(value);

        return true;
    }

    /// <summary>Правила поясу з цієї системи.</summary>
    /// <returns>Пояс із правилами переходів.</returns>
    public TimeZoneInfo ToTimeZoneInfo() => TimeZoneInfo.FindSystemTimeZoneById(Id);

    /// <summary>Ідентифікатор як рядок.</summary>
    /// <returns>Значення <see cref="Id"/>.</returns>
    public override string ToString() => Id;

    /// <summary>Неявне перетворення на рядок для запису в сутність.</summary>
    /// <param name="zone">Пояс.</param>
    public static implicit operator string(SiteTimeZone zone) => zone.Id;
}
