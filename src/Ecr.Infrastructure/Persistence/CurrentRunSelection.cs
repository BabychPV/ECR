// src/Ecr.Infrastructure/Persistence/CurrentRunSelection.cs
using Ecr.Domain.Entities.Calculations;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Актуальний прогін (проєкція для вибору).</summary>
/// <param name="Id">Прогін.</param>
/// <param name="DocumentId">Документ прогону; <c>null</c> — прогін усього проєкту.</param>
/// <param name="PeriodKey">Період прогону.</param>
/// <param name="FinishedAt">Завершення прогону.</param>
internal sealed record CurrentRunRow(long Id, long? DocumentId, int? PeriodKey, DateTime? FinishedAt);

/// <summary>
/// ✎ D-557: ЄДИНЕ правило «які прогони зараз актуальні для документа й періоду» — для читання результатів, для
/// переносу (<c>CarryOverResultsAsync</c>) і для планування <c>RecalculationJob</c> (який екземпляр має що переносити).
/// Дві копії правила розходилися б: екземпляр виключено з розрахунку за результатами одного прогону, а переносити
/// брали б з іншого — числа зникли б.
/// </summary>
internal static class CurrentRunSelection
{
    /// <summary>Актуальні прогони проєкту документа: власні документні та прогони всього проєкту (усіх періодів).</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<List<CurrentRunRow>> RunsOfDocumentAsync(EcrDbContext db, long documentId, CancellationToken ct)
        => await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join r in db.CalculationRuns.AsNoTracking() on d.ProjectId equals r.ProjectId
                where r.Status == CalculationRun.CurrentStatus
                      && (r.DocumentId == documentId || r.DocumentId == null)
                select new CurrentRunRow(r.Id, r.DocumentId, r.PeriodKey, r.FinishedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Правило UX-PASS R4 для одного періоду: власний документний прогін ЦЬОГО періоду має перевагу; без нього —
    /// прогони всього проєкту.
    /// </summary>
    /// <param name="current">Актуальні прогони проєкту документа.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    public static IEnumerable<CurrentRunRow> Effective(IReadOnlyList<CurrentRunRow> current, long documentId, int periodKey)
    {
        var hasDedicatedCurrent = current.Any(r => r.DocumentId == documentId && r.PeriodKey == periodKey);

        return current.Where(r => r.DocumentId == documentId || (!hasDedicatedCurrent && r.DocumentId == null));
    }
}
