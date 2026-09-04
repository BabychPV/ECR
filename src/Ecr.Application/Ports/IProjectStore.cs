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
    /// <summary>Сторінка проєктів.</summary>
    public Task<PagedResult<ProjectSummary>> ListAsync(CursorRequest page, CancellationToken ct);
}
