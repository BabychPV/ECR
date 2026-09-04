using Ecr.Domain.Entities.Documents;

namespace Ecr.Application.Ports;

/// <summary>
/// Доступ до проєктів і їхніх періодів для календаря і адміністративних
/// операцій над періодами.
/// </summary>
/// <remarks>
/// ⚠ Окремий порт із тієї самої причини, що й <see cref="IWorkflowStore"/>:
/// <see cref="LockAsync"/> мусить брати рядок періоду з <c>UPDLOCK</c>, бо
/// інакше адміністративне відкриття і <c>PeriodStateJob</c> перегоняють одне
/// одного (ФВ-1.10a). «Взяти з блокуванням» узагальненим сховищем не
/// виражається.
/// </remarks>
public interface IPeriodStore
{
    /// <summary>Проєкт із завантаженими періодами; <c>null</c> — не існує.</summary>
    public Task<Project?> FindProjectAsync(int projectId, CancellationToken ct);

    /// <summary>Політика періодів проєкту.</summary>
    public Task<PeriodPolicy> GetPolicyAsync(int periodPolicyId, CancellationToken ct);

    /// <summary>Період із <c>UPDLOCK</c> до кінця транзакції.</summary>
    public Task<Period?> LockAsync(int periodId, CancellationToken ct);

    /// <summary>Додає нові періоди календаря.</summary>
    public void AddRange(IEnumerable<Period> periods);

    /// <summary>Додає проєкт; ідентифікатор з'являється після збереження.</summary>
    public Task AddProjectAsync(Project project, CancellationToken ct);

    /// <summary>
    /// Стани періодів проєкту: <c>periodKey</c> → стан.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Конкретний період; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Окремо від <see cref="FindProjectAsync"/> навмисно: перевірка «чи не
    /// закритий період» не потребує ні документів, ні політики, ні поясу — а
    /// агрегат тягне їх усі. На запуску перерахунку повного року це різниця
    /// між одним запитом і завантаженням проєкту цілком.
    /// </remarks>
    public Task<IReadOnlyList<PeriodStateRef>> GetPeriodStatesAsync(
        int projectId, int? periodKey, CancellationToken ct);
}

/// <summary>Стан одного періоду.</summary>
/// <param name="PeriodKey">Ключ періоду (R-A6).</param>
/// <param name="State">Стан; <c>Closed</c> блокує перерахунок (ФВ-9.7).</param>
public sealed record PeriodStateRef(int PeriodKey, Ecr.Domain.Enums.PeriodState State);
