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
