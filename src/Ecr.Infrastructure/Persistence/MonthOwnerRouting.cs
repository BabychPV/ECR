// src/Ecr.Infrastructure/Persistence/MonthOwnerRouting.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ✎ PERF-06b (<c>D-572</c>): ОДНЕ рішення «зміна місяця m джерела зачіпає лише період-власник m споживача» — для постановки
/// (<see cref="RegistryImpactStore.ListTemplateConsumersRoutedAsync"/>, через <c>RegistryImpactRecalculationJob</c>) і для воріт
/// F5 (<c>SourceCascadePendingGate</c>).
/// </summary>
/// <remarks>
/// ⛔ Два окремі рішення розійшлися б: сховище, що відкотилося до всього року (немає рядка календаря власника), будило б
/// раніші місяці, а ворота, що звужують, дозволили б їх затвердити, поки їхній перерахунок у черзі.
/// </remarks>
public static class MonthOwnerRouting
{
    /// <summary>Ребра зв'язки до шаблону-споживача — усі «комірка періоду-власника місяця», без методологій.</summary>
    /// <param name="edges">Ребра зв'язки (згорнуті) до одного шаблону-споживача.</param>
    public static bool EdgesOwned(IEnumerable<TemplateDependency> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        var any = false;
        foreach (var edge in edges)
        {
            if (!edge.MonthOwned || edge.ViaMethodology)
            {
                return false;
            }

            any = true;
        }

        return any;
    }

    /// <summary>
    /// Чи звужується набір періодів споживача до власника місяця: лише місячний проєкт, у календарі якого є рядок періоду
    /// <c>(рік, m)</c> (будь-який стан). Інакше — увесь рік.
    /// </summary>
    /// <param name="consumerKind">Періодичність проєкту споживача.</param>
    /// <param name="ownerRowExists">Рядок календаря власника є.</param>
    public static bool Narrows(PeriodKind consumerKind, bool ownerRowExists)
        => consumerKind == PeriodKind.Monthly && ownerRowExists;

    /// <summary>Які з пар «проєкт × ключ періоду» мають рядок календаря (одне звернення).</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="projectIds">Проєкти.</param>
    /// <param name="periodKeys">Ключі періодів-власників.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<HashSet<(int ProjectId, int PeriodKey)>> ExistingOwnersAsync(
        EcrDbContext db, IReadOnlyCollection<int> projectIds, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(projectIds);
        ArgumentNullException.ThrowIfNull(periodKeys);
        if (projectIds.Count == 0 || periodKeys.Count == 0)
        {
            return [];
        }

        var rows = await db.Periods.AsNoTracking()
            .Where(p => projectIds.Contains(p.ProjectId) && periodKeys.Contains(p.PeriodKeyValue))
            .Select(p => new { p.ProjectId, p.PeriodKeyValue })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => (r.ProjectId, r.PeriodKeyValue))];
    }
}
