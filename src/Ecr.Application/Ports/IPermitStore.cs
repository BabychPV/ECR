// src/Ecr.Application/Ports/IPermitStore.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Читання, якого немає в порту реєстрів, для екземплярів дозволів (Permits, PM-04): підсумки по рядках таблиці дозволу.
/// </summary>
/// <remarks>
/// ⚠ Окремий порт від <see cref="IPermitTypeStore"/> (метадані типів) і <see cref="IRegistryStore"/> (ядро реєстрів про
/// дозволи не знає).
/// </remarks>
public interface IPermitStore
{
    /// <summary>
    /// Скільки живих рядків таблиці у кожного дозволу і Σ головного числового стовпця — одним запитом на таблицю.
    /// </summary>
    /// <param name="permitFieldDefId">Поле композиції <c>PERMIT</c> таблиці (посилання на дозвіл-батька).</param>
    /// <param name="totalFieldDefId">Головний числовий стовпець; <c>null</c> - підсумку немає.</param>
    /// <param name="permitIds">Лише ці дозволи; <c>null</c> - усі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Дозвіл → підсумок; дозвіл без рядків у словнику відсутній.</returns>
    public Task<IReadOnlyDictionary<long, PermitRowStat>> RowStatsAsync(
        int permitFieldDefId, int? totalFieldDefId, IReadOnlyCollection<long>? permitIds, CancellationToken ct);
}

/// <summary>Підсумок рядків однієї таблиці одного дозволу.</summary>
/// <param name="Count">Живих рядків.</param>
/// <param name="Total">Σ головного стовпця; <c>null</c> - стовпця немає.</param>
public sealed record PermitRowStat(int Count, decimal? Total);
