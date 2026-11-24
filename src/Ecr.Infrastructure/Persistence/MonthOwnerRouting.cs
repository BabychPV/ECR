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
    /// <summary>
    /// ✎ W15z M1: версії шаблону-споживача, на яких КОЖНЕ читання довідника формулами шаблону — «комірка періоду-власника
    /// місяця» (перетин <see cref="TemplateDependency.OwnedVersions"/> ребер з формулами); ребро з формулами без таких версій —
    /// порожньо. Ребра лише з методологіями версій не обмежують (їх перевіряє <see cref="RefusalReason"/>).
    /// </summary>
    /// <param name="edges">Ребра зв'язки (згорнуті) до одного шаблону-споживача.</param>
    public static IReadOnlySet<int> OwnedVersions(IEnumerable<TemplateDependency> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        HashSet<int>? owned = null;
        foreach (var edge in edges.Where(e => e.ViaTemplateFormula))
        {
            if (edge.OwnedVersions is not { Count: > 0 } versions)
            {
                return new HashSet<int>();
            }

            if (owned is null)
            {
                owned = [.. versions];
            }
            else
            {
                owned.IntersectWith(versions);
            }
        }

        return owned ?? [];
    }

    /// <summary>
    /// Чому звуження до власника місяця не діє (для журналу й рішення); <c>null</c> — діє для періодів на версіях
    /// <see cref="OwnedVersions"/>.
    /// </summary>
    /// <param name="edges">Ребра зв'язки до шаблону-споживача.</param>
    /// <remarks>
    /// Методологія, що читає довідник обсягом «місяць» (місяць споживача = місяць джерела), сумісна: зміна місяця m
    /// зачіпає її результат лише в періоді-власнику m (W15z: методології Land 15/16/23/40/42 читають HSE400 саме так).
    /// «Рік», «усе» чи похідний довідник — увесь рік.
    /// </remarks>
    public static string? RefusalReason(IReadOnlyCollection<TemplateDependency> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);
        if (edges.Count == 0)
        {
            return "no edges";
        }

        if (edges.Any(e => e.ViaMethodology && e.MethodologyScope?.Kind != Ecr.Expressions.Binding.PeriodScopeKind.Month))
        {
            return "a methodology reads the registry beyond the consumer's own month (or via a derived registry)";
        }

        if (!edges.Any(e => e.ViaTemplateFormula))
        {
            return "no template formula reads the registry (month-scope methodologies only)";
        }

        return OwnedVersions(edges).Count == 0
            ? "no template version reads the registry only in month-owner cells"
            : null;
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
