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
}
