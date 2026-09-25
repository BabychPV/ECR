using Ecr.Application.Common;
using Ecr.Application.Projects;

namespace Ecr.Application.Ports;

/// <summary>Читання проєктів для переліків.</summary>
/// <remarks>
/// Окремо від <see cref="IPeriodStore"/>: той віддає ПРОЄКТ ІЗ ПЕРІОДАМИ для
/// операцій над календарем, а тут потрібен плоский зріз для списку. Спільний
/// метод означав би, що кожен перелік тягне повний граф періодів — на сотні
/// проєктів це сотні тисяч рядків заради двох колонок на екрані.
/// </remarks>
public interface IProjectStore
{
    /// <summary>Сторінка проєктів серед <paramref name="visibleIds"/>.</summary>
    /// <remarks>
    /// ⛔ Фільтр доступу — ЧАСТИНА запиту, а не пост-обробка сторінки: інакше
    /// сторінка з N перших проєктів бази, відфільтрована після `Take`, лишає
    /// користувача з грантом на (N+1)-й проєкт із порожнім переліком.
    /// </remarks>
    public Task<PagedResult<ProjectSummary>> ListAsync(
        CursorRequest page, IReadOnlyCollection<int> visibleIds, CancellationToken ct);
}
