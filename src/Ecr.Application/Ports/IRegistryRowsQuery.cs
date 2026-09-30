// src/Ecr.Application/Ports/IRegistryRowsQuery.cs
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання рядків довідника зі значеннями для редактора даних (RT-13, FEATURE-REGISTRY-TABLES §7.1)
/// — поточний стан або <c>FOR SYSTEM_TIME AS OF</c> момент (<c>D-158</c>).
/// </summary>
/// <remarks>
/// ⚠ Порт лише читає; відбір видимих записів робить обробник правилом <c>RegistryResolver</c> —
/// тим самим, що в пікері й у знімку для формул. Друге формулювання видимості в SQL розійшлося б
/// із ними на межі дня або на частині невидимого батька.
/// <para>
/// Усі методи з тим самим <c>asOfUtc</c> читають один момент: <c>null</c> — поточні таблиці.
/// </para>
/// </remarks>
public interface IRegistryRowsQuery
{
    /// <summary>Усі записи довідника станом на момент, без фільтра видимості.</summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="asOfUtc">Системний момент; <c>null</c> — поточні дані.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryEntry>> ListEntriesAsync(int registryDefId, DateTime? asOfUtc, CancellationToken ct);

    /// <summary>Значення переліку полів у всіх записах — для фільтрів і ланцюжка композиції.</summary>
    /// <param name="registryFieldDefIds">Поля; порожній набір — порожній результат.</param>
    /// <param name="asOfUtc">Системний момент; <c>null</c> — поточні дані.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryRowValue>> ListFieldValuesAsync(
        IReadOnlyCollection<int> registryFieldDefIds, DateTime? asOfUtc, CancellationToken ct);

    /// <summary>
    /// Значення, версії й записи-цілі <c>Lookup</c> для сторінки — сталою кількістю запитів.
    /// </summary>
    /// <param name="registryEntryIds">Записи сторінки (≤ 500).</param>
    /// <param name="asOfUtc">Системний момент; <c>null</c> — поточні дані.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<RegistryRowsSlice> ReadRowsAsync(
        IReadOnlyCollection<long> registryEntryIds, DateTime? asOfUtc, CancellationToken ct);

    /// <summary>
    /// Усі системні версії запису та його значень (<c>FOR SYSTEM_TIME ALL</c>, RT-15) — сировина
    /// історії запису; порівняння версій робить обробник.
    /// </summary>
    /// <param name="registryEntryId">Запис; видалений логічно теж.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Версії нульової тривалості (<c>PeriodStart = PeriodEnd</c>: кілька змін рядка в одній
    /// транзакції) відкинуто — їхнього стану ніхто ніколи не бачив.
    /// </remarks>
    public Task<RegistryEntryHistorySlice> ReadEntryHistoryAsync(long registryEntryId, CancellationToken ct);
}

/// <summary>Значення одного поля запису — типізовані колонки <c>dic.RegistryValue</c>.</summary>
/// <param name="RegistryEntryId">Запис.</param>
/// <param name="RegistryFieldDefId">Поле.</param>
/// <param name="Numeric">Число (<c>decimal(34,16)</c>, D-30).</param>
/// <param name="Text">Рядок.</param>
/// <param name="Date">Дата.</param>
/// <param name="Bool">Логічне.</param>
/// <param name="RefEntryId">Ціль <c>Lookup</c>.</param>
/// <param name="UnitId">Одиниця числа або значення поля типу <c>Unit</c>.</param>
/// <param name="UnitCode">Код одиниці.</param>
public sealed record RegistryRowValue(
    long RegistryEntryId,
    int RegistryFieldDefId,
    decimal? Numeric,
    string? Text,
    DateTime? Date,
    bool? Bool,
    long? RefEntryId,
    int? UnitId,
    string? UnitCode);

/// <summary>Дані сторінки рядків.</summary>
/// <param name="Values">Значення полів записів сторінки.</param>
/// <param name="Versions">
/// Запис → найпізніший початок системного періоду запису та його значень (<c>PeriodStart</c>) —
/// жетон конкуренції рядка (<c>D-166</c>).
/// </param>
/// <param name="Referenced">Записи, на які посилаються <c>Lookup</c>-значення сторінки.</param>
public sealed record RegistryRowsSlice(
    IReadOnlyList<RegistryRowValue> Values,
    IReadOnlyDictionary<long, DateTime> Versions,
    IReadOnlyList<RegistryEntry> Referenced);

/// <summary>Системна версія рядка запису (<c>dic.RegistryEntry</c> + <c>dic.RegistryEntryHistory</c>).</summary>
/// <param name="FromUtc">Початок версії (<c>PeriodStart</c>) — момент зміни.</param>
/// <param name="ToUtc">Кінець версії (<c>PeriodEnd</c>); чинна версія — <c>9999-12-31</c>.</param>
/// <param name="ChangedByUserId">Автор версії (<c>D-158</c>); <c>null</c> — невідомий.</param>
/// <param name="Display">Назва мовами каталогу.</param>
/// <param name="ValidFrom">Перший чинний день.</param>
/// <param name="ValidTo">Перший НЕчинний день.</param>
/// <param name="IsActive">Активний.</param>
/// <param name="IsDeleted">Видалений логічно.</param>
public sealed record RegistryEntryVersion(
    DateTime FromUtc,
    DateTime ToUtc,
    int? ChangedByUserId,
    Domain.ValueObjects.LocalizedText Display,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    bool IsActive,
    bool IsDeleted);

/// <summary>Системна версія значення поля (<c>dic.RegistryValue</c> + <c>dic.RegistryValueHistory</c>).</summary>
/// <param name="ValueId">Рядок значення: видалене й знову додане значення — інший рядок.</param>
/// <param name="FromUtc">Початок версії.</param>
/// <param name="ToUtc">Кінець версії; чинна — <c>9999-12-31</c>.</param>
/// <param name="ChangedByUserId">Автор версії; <c>null</c> — невідомий.</param>
/// <param name="Value">Значення в типізованих колонках.</param>
public sealed record RegistryValueVersion(
    long ValueId,
    DateTime FromUtc,
    DateTime ToUtc,
    int? ChangedByUserId,
    RegistryRowValue Value);

/// <summary>Сировина історії одного запису.</summary>
/// <param name="Entry">Версії рядка запису в порядку часу.</param>
/// <param name="Values">Версії значень запису в порядку часу.</param>
/// <param name="UserNames">Автор → відображуване ім'я (<c>sec.User.DisplayName</c>, не логін — R-A2).</param>
/// <param name="Referenced">Поточні записи-цілі <c>Lookup</c>-значень усіх версій.</param>
public sealed record RegistryEntryHistorySlice(
    IReadOnlyList<RegistryEntryVersion> Entry,
    IReadOnlyList<RegistryValueVersion> Values,
    IReadOnlyDictionary<int, string> UserNames,
    IReadOnlyList<RegistryEntry> Referenced);
