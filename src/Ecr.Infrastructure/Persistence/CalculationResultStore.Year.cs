using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Читання фактів огляду «Calculation results — усі місяці року» (<see cref="ICalculationYearReader"/>).</summary>
/// <remarks>
/// ⛔ Набір запитів не залежить від числа рядків і місяців: прогони — один запит, числа — один (seek
/// <c>IX_CalculationResult_DocRun (PeriodKey, DocumentId, CalculationRunId)</c> по ≤ 12 партиціях), комірки формул,
/// рядки й підписи — по одному на екземпляри таблиць (перелік ідентифікаторів одним JSON-параметром EF), довідники й
/// версії — по одному. Жодного запиту на рядок чи на місяць.
/// </remarks>
public sealed partial class CalculationResultStore : ICalculationYearReader
{
    /// <inheritdoc />
    public async Task<CalculationYearFacts> ReadYearAsync(
        long documentId,
        IReadOnlyList<int> periodKeys,
        IReadOnlyCollection<long> tableInstanceIds,
        IReadOnlyCollection<int> labelColumnIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);
        ArgumentNullException.ThrowIfNull(tableInstanceIds);
        ArgumentNullException.ThrowIfNull(labelColumnIds);
        if (periodKeys.Count == 0)
        {
            return CalculationYearFacts.Empty;
        }

        var keys = periodKeys.Distinct().ToList();
        var current = await CurrentRunsOfDocumentAsync(documentId, ct).ConfigureAwait(false);

        // Прогін кожного періоду — тим самим правилом, що й ReadCurrentAsync; рядок числа належить періоду лише тоді,
        // коли його прогін у наборі саме цього періоду (проєктний прогін іншого періоду тут не рахується).
        var effective = keys.ToDictionary(k => k, k => EffectiveRuns(current, documentId, k).Select(r => r.Id).ToHashSet());
        var runIds = effective.Values.SelectMany(s => s).Distinct().ToList();

        var limit = Ceiling * keys.Count;
        var results = runIds.Count == 0
            ? []
            : await db.CalculationResults
                .AsNoTracking()
                .Where(r => keys.Contains(r.PeriodKey)
                            && r.DocumentId == documentId
                            && runIds.Contains(r.CalculationRunId)
                            && r.Kind == CalculationResultKind.Output)
                .OrderBy(r => r.PeriodKey)
                .ThenBy(r => r.Id)
                .Take(limit + 1)
                .Select(r => new
                {
                    r.PeriodKey, r.CalculationRunId, r.MethodologyVersionId, r.TableInstanceId, r.SourceRowKey,
                    r.OutputCode, r.SubstanceEntryId, r.UnitId, r.Value,
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);
        ThrowIfOverCeiling(results.Count, limit, documentId, keys[0]);

        var kept = results
            .Where(r => effective.TryGetValue(r.PeriodKey, out var set) && set.Contains(r.CalculationRunId))
            .ToList();

        // Прогін місяця, що справді дав числа документа: документний перед проєктним, новіший перед старішим.
        var runs = kept
            .GroupBy(r => r.PeriodKey)
            .Select(g =>
            {
                var ids = g.Select(r => r.CalculationRunId).ToHashSet();
                var run = current.Where(c => ids.Contains(c.Id))
                    .OrderByDescending(c => c.DocumentId is not null)
                    .ThenByDescending(c => c.Id)
                    .First();
                return new YearRun(g.Key, run.Id, Utc(run.FinishedAt));
            })
            .ToList();

        var instanceIds = tableInstanceIds.Distinct().ToList();
        var formulaCells = new List<YearFormulaCell>();
        var rows = new List<YearRow>();
        var labelCells = new List<YearLabelCell>();
        if (instanceIds.Count > 0)
        {
            var cells = await (
                    from row in db.TableRows.AsNoTracking()
                    where keys.Contains(row.PeriodKeyValue) && instanceIds.Contains(row.TableInstanceId) && !row.IsDeleted
                    join cell in db.CellValues.AsNoTracking()
                        on new { P = row.PeriodKeyValue, R = row.Id } equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
                    where cell.IsCalculated && !cell.IsEmpty && cell.ValueNumeric != null
                    orderby row.PeriodKeyValue, row.Id, cell.ColumnDefId
                    select new { row.PeriodKeyValue, row.TableInstanceId, row.RowKeyValue, cell.ColumnDefId, cell.ValueNumeric })
                .Take(limit + 1)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            ThrowIfOverCeiling(cells.Count, limit, documentId, keys[0]);
            formulaCells = cells.ConvertAll(c => new YearFormulaCell(
                c.PeriodKeyValue, c.TableInstanceId, c.RowKeyValue, c.ColumnDefId, c.ValueNumeric!.Value));

            rows = await db.TableRows
                .AsNoTracking()
                .Where(r => keys.Contains(r.PeriodKeyValue) && instanceIds.Contains(r.TableInstanceId) && !r.IsDeleted)
                .Select(r => new YearRow(r.TableInstanceId, r.RowKeyValue, r.RowDefId, r.Ordinal))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (labelColumnIds.Count > 0)
            {
                var columns = labelColumnIds.Distinct().ToList();
                labelCells = await (
                        from row in db.TableRows.AsNoTracking()
                        where keys.Contains(row.PeriodKeyValue) && instanceIds.Contains(row.TableInstanceId) && !row.IsDeleted
                        join cell in db.CellValues.AsNoTracking()
                            on new { P = row.PeriodKeyValue, R = row.Id } equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
                        where columns.Contains(cell.ColumnDefId) && !cell.IsEmpty
                        select new YearLabelCell(
                            row.PeriodKeyValue, row.TableInstanceId, row.RowKeyValue, cell.ColumnDefId,
                            cell.ValueString, cell.ValueRegistryEntryId, cell.ValueNumeric))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }
        }

        var entryIds = kept.Where(r => r.SubstanceEntryId is not null).Select(r => r.SubstanceEntryId!.Value)
            .Concat(labelCells.Where(c => c.EntryId is not null).Select(c => c.EntryId!.Value))
            .Distinct()
            .ToList();
        var entries = entryIds.Count == 0
            ? []
            : await db.RegistryEntries
                .AsNoTracking()
                .Where(e => entryIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Code, e.DisplayL10n })
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var versionIds = kept.Select(r => r.MethodologyVersionId).Distinct().ToList();
        var versions = versionIds.Count == 0
            ? []
            : await (
                    from version in db.MethodologyVersions.AsNoTracking()
                    where versionIds.Contains(version.Id)
                    join methodology in db.Methodologies.AsNoTracking() on version.MethodologyId equals methodology.Id
                    select new YearMethodologyVersion(
                        version.Id, methodology.Id, methodology.Code, methodology.NameL10n, version.Version))
                .ToListAsync(ct)
                .ConfigureAwait(false);

        return new CalculationYearFacts(
            runs,
            kept.ConvertAll(r => new YearResult(
                r.PeriodKey, r.MethodologyVersionId, r.TableInstanceId, r.SourceRowKey, r.OutputCode,
                r.SubstanceEntryId, r.UnitId, r.Value)),
            formulaCells,
            rows,
            labelCells,
            [.. entries.Select(e => new YearEntry(e.Id, e.Code, e.DisplayL10n))],
            versions);
    }

    private static DateTime? Utc(DateTime? value)
        => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    /// <summary>Понад стелю — відмова тим самим кодом, що й читання одного періоду (D2-05), а не обрізання.</summary>
    private static void ThrowIfOverCeiling(int count, int limit, long documentId, int periodKey)
    {
        if (count <= limit)
        {
            return;
        }

        throw new BusinessRuleException(
            "ECR-CALC-0422",
            $"Чисел документа {documentId} за рік більше за {limit}: показати їх частково означало б показати хибні суми.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0422.resultsTooLarge",
                ["documentId"] = documentId,
                ["periodKey"] = periodKey,
                ["max"] = limit,
            });
    }
}
