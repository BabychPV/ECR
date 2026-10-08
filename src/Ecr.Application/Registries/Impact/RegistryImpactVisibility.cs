// src/Ecr.Application/Registries/Impact/RegistryImpactVisibility.cs
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Impact;

/// <summary>
/// Межа читання для «що зачепила правка довідника» (<c>GET /registries/{code}/impact</c>,
/// <c>POST …/recalculate-impacted</c>).
/// </summary>
/// <remarks>
/// ⛔ Зв'язок «документ → довідник» виводиться з результатів методологій ВСЬОГО документа (будь-який
/// аркуш, будь-яка колонка), а не з того, що читач бачить. Читач, якому в проєкті чи періоді щось
/// схованo (аркуш, таблицю, колонку, період), отримав би з переліку факт, що методологія з виходом лише на
/// схованому аркуші порахована, і її код (<c>via</c>) - той самий клас оракула, що свіжість результатів у
/// <c>calculation-results</c> (Н-2). Тому для нього документ у переліку відсутній, а явно названий у
/// <c>recalculate-impacted</c> - те саме 422 «не зачеплений»: закрито за замовчуванням, узгоджено з
/// <c>resultsStale = null</c> (<see cref="DocumentSheetVisibility.IsProjectNarrowed"/>).
/// </remarks>
internal static class RegistryImpactVisibility
{
    /// <summary>Залишає рядки документів, у періоді яких читачеві не схованo нічого.</summary>
    /// <param name="access">Служба доступу.</param>
    /// <param name="profile">Профіль читача.</param>
    /// <param name="rows">Рядки зі сховища (вже відфільтровані за видимими проєктами).</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyList<RegistryImpactRow>> ForReaderAsync(
        IAccessDecisionService access, AccessProfile profile, IReadOnlyList<RegistryImpactRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0 || !DocumentSheetVisibility.HasRestrictions(profile))
        {
            return rows;
        }

        // Структура шаблону належить проєкту: межі будуються раз на проєкт (як у переліку документів).
        var scopes = new Dictionary<int, DocumentReadScope>();
        foreach (var row in rows)
        {
            if (!scopes.ContainsKey(row.ProjectId))
            {
                scopes[row.ProjectId] = await access.ReadScopeAsync(profile, row.DocumentId, ct).ConfigureAwait(false);
            }
        }

        var narrowed = new Dictionary<(int Project, int Period), bool>();
        var kept = new List<RegistryImpactRow>(rows.Count);
        foreach (var row in rows)
        {
            var key = (row.ProjectId, row.PeriodKey);
            if (!narrowed.TryGetValue(key, out var isNarrowed))
            {
                var scope = scopes[row.ProjectId];
                var period = new PeriodKey(row.PeriodKey);
                isNarrowed = DocumentSheetVisibility.IsProjectNarrowed(period.IsValid ? scope.InPeriod(period) : scope);
                narrowed[key] = isNarrowed;
            }

            if (!isNarrowed)
            {
                kept.Add(row);
            }
        }

        return kept;
    }
}
