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

    /// <summary>Скільки разів повторити читання, якщо множина актуальних прогонів змінилась між запитами.</summary>
    private const int ReadAttempts = 3;

    /// <summary>
    /// ✎ D-557 / D-560: які з <paramref name="approvedInstanceIds"/> мають власні результати в актуальних прогонах
    /// (<c>Attributed</c>) і в яких періодах є «безприв'язні» результати до D-401 (<c>LegacyPeriods</c>).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periods">Періоди затверджених екземплярів.</param>
    /// <param name="approvedInstanceIds">Затверджені екземпляри.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ D-560: прогони й рядки — кількома запитами, а витіснений прогін прибирається одразу після перемикання. Без
    /// перечитування прогонів перемикання між запитами дало б «немає результатів» для екземпляра, що їх має (числа
    /// затвердженого аркуша перераховувались би замість переносу). Тому актуальні прогони перечитуються ПІСЛЯ
    /// рядків: змінились — повтор (до <see cref="ReadAttempts"/>), як у <c>ReadCurrentCoreAsync</c>.
    /// </remarks>
    public static async Task<(HashSet<long> Attributed, HashSet<int> LegacyPeriods)> ApprovedAttributionAsync(
        EcrDbContext db, long documentId, IReadOnlyCollection<int> periods, IReadOnlyCollection<long> approvedInstanceIds,
        CancellationToken ct)
    {
        var approved = approvedInstanceIds.ToList();
        var attributed = new HashSet<long>();
        var legacyPeriods = new HashSet<int>();

        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            attributed.Clear();
            legacyPeriods.Clear();

            var currentRuns = await RunsOfDocumentAsync(db, documentId, ct).ConfigureAwait(false);
            foreach (var period in periods)
            {
                var runIds = Effective(currentRuns, documentId, period).Select(r => r.Id).ToList();
                if (runIds.Count == 0)
                {
                    continue;
                }

                var periodResults = db.CalculationResults
                    .AsNoTracking()
                    .Where(r => r.DocumentId == documentId && r.PeriodKey == period && runIds.Contains(r.CalculationRunId));
                attributed.UnionWith(await periodResults
                    .Where(r => r.TableInstanceId != null && approved.Contains(r.TableInstanceId.Value))
                    .Select(r => r.TableInstanceId!.Value)
                    .Distinct()
                    .ToListAsync(ct)
                    .ConfigureAwait(false));
                if (await periodResults.AnyAsync(r => r.TableInstanceId == null, ct).ConfigureAwait(false))
                {
                    legacyPeriods.Add(period);
                }
            }

            var after = await RunsOfDocumentAsync(db, documentId, ct).ConfigureAwait(false);
            if (currentRuns.Select(r => r.Id).Order().SequenceEqual(after.Select(r => r.Id).Order()))
            {
                break;
            }
        }

        return (attributed, legacyPeriods);
    }
}
