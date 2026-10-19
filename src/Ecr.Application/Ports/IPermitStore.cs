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

    /// <summary>
    /// Бере транзакційний замок на номер дозволу типу: до коміту чи відкату транзакції інший запит із тим самим типом і
    /// номером чекає тут (PA-A-07, аудит, прохід A).
    /// </summary>
    /// <remarks>
    /// ⛔ Викликається ЛИШЕ всередині транзакції запису й ПЕРШИМ ділом, а перевірка «такий дозвіл уже є» - ПІСЛЯ замка: ключ
    /// (тип, номер, початок чинності) унікальним індексом не захищений (чернетка ключ не тримає, міграції немає), тож
    /// перевірка без замка бачить лише закомічене, і двадцять паралельних <c>POST /permits</c> створювали двадцять чернеток.
    /// Номер порівнюється без урахування регістру, як і перевірка дубля.
    /// </remarks>
    /// <param name="typeCode">Код типу.</param>
    /// <param name="number">Номер без крайніх пробілів.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.ConcurrencyConflictException">Замка не дочекалися (<c>ECR-DOC-4091</c>).</exception>
    public Task LockNumberAsync(string typeCode, string number, CancellationToken ct);
}

/// <summary>Підсумок рядків однієї таблиці одного дозволу.</summary>
/// <param name="Count">Живих рядків.</param>
/// <param name="Total">Σ головного стовпця; <c>null</c> - стовпця немає.</param>
public sealed record PermitRowStat(int Count, decimal? Total);
