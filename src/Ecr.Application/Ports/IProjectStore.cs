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

    /// <summary>
    /// Факти для рішення «чи можна видалити проєкт» (UAT 08.11, сценарій c); <c>null</c> — проєкту немає.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Скасування.</param>
    public Task<ProjectDeletionFacts?> GetDeletionFactsAsync(int projectId, CancellationToken ct);

    /// <summary>
    /// Видаляє проєкт разом із його налаштуваннями (періоди, прив'язка версії шаблону, маршрут погодження, гранти).
    /// Викликати лише після перевірки <see cref="ProjectDeletionFacts"/>; документів і історії видалення не чіпає —
    /// з ними база відмовить зовнішнім ключем.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Скасування.</param>
    public Task DeleteAsync(int projectId, CancellationToken ct);
}

/// <summary>Факти проєкту для видалення.</summary>
/// <param name="Code">Код проєкту (для журналу).</param>
/// <param name="Status">Стан проєкту.</param>
/// <param name="TemplateVersionId">Версія шаблону (для журналу).</param>
/// <param name="DocumentCount">Скільки документів.</param>
/// <param name="HistoryCount">
/// Скільки записів історії, які видалення знищило б: прогони розрахунків, зрізи звітів, архівації, погодження
/// перерахунку. Будь-який — відмова: це вже не «порожня чернетка».
/// </param>
public sealed record ProjectDeletionFacts(
    string Code, Domain.Enums.ProjectStatus Status, int TemplateVersionId, int DocumentCount, int HistoryCount);
