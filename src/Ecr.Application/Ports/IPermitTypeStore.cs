// src/Ecr.Application/Ports/IPermitTypeStore.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Доступ до типів дозволів, їхніх таблиць і надбудови над полями реєстрів (Permits, PM-03).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не метод <see cref="IRegistryStore"/>: ядро реєстрів про дозволи не знає і знати не має
/// (<c>RegistryDef</c> не розширюється). Типів дозволів десятки — це метадані, тож вибірки без сторінок.
/// </remarks>
public interface IPermitTypeStore
{
    /// <summary>Тип за кодом (відстежуваний); <c>null</c> — немає.</summary>
    /// <param name="code">Код типу.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PermitTypeDef?> FindByCodeAsync(string code, CancellationToken ct);

    /// <summary>
    /// Усі типи, впорядковані за кодом, короткими знімками (без відстеження). ⚠ Текст чернетки не читається, якщо він
    /// більший за <c>PermitTypeLimits.ListDraftSummaryMaxChars</c> (PA2-S-01); повну сутність дає <see cref="FindByCodeAsync"/>.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitTypeSummary>> ListSummariesAsync(CancellationToken ct);

    /// <summary>Таблиці типу в порядку складу (відстежувані).</summary>
    /// <param name="permitTypeDefId">Тип.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitTypeTable>> ListTablesAsync(int permitTypeDefId, CancellationToken ct);

    /// <summary>Таблиці всіх типів (без відстеження) — для переліку типів одним запитом.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitTypeTable>> ListAllTablesAsync(CancellationToken ct);

    /// <summary>Надбудова над полями типу (відстежувана).</summary>
    /// <param name="permitTypeDefId">Тип.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<PermitFieldMeta>> ListFieldMetasAsync(int permitTypeDefId, CancellationToken ct);

    /// <summary>Ставить тип у чергу на вставку.</summary>
    /// <param name="type">Тип.</param>
    public void Add(PermitTypeDef type);

    /// <summary>Ставить таблицю типу в чергу на вставку.</summary>
    /// <param name="table">Таблиця.</param>
    public void Add(PermitTypeTable table);

    /// <summary>Ставить надбудову поля в чергу на вставку.</summary>
    /// <param name="meta">Надбудова.</param>
    public void Add(PermitFieldMeta meta);

    /// <summary>Прибирає надбудову поля.</summary>
    /// <param name="meta">Надбудова.</param>
    public void Remove(PermitFieldMeta meta);

    /// <summary>
    /// Ідентифікатори реєстрів, прихованих від загального переліку «Довідники»: шапки, таблиці типів і
    /// списки «лише для типу» (<c>ListScope = TypeOnly</c>).
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlySet<int>> ListHiddenRegistryIdsAsync(CancellationToken ct);

    /// <summary>
    /// Скільки живих записів у кожному з переданих реєстрів шапок (екземпляри дозволів); реєстр без записів
    /// у словнику відсутній.
    /// </summary>
    /// <param name="headerRegistryDefIds">Реєстри шапок.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyDictionary<int, int>> CountPermitsAsync(IReadOnlyCollection<int> headerRegistryDefIds, CancellationToken ct);

    /// <summary>
    /// Прибирає ніколи не опублікований тип разом із реєстром шапки й порожніми реєстрами таблиць і списків.
    /// </summary>
    /// <param name="type">Тип у стані чернетки.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RemoveDraftTypeAsync(PermitTypeDef type, CancellationToken ct);
}
