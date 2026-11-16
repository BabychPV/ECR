using System.Globalization;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <content>✎ B79: чи змінив новий прогін числа методологій (підстава позначки «перераховано після подання»).</content>
public sealed partial class CalculationResultStore
{
    /// <summary>
    /// Пари «методологія : екземпляр таблиці», де множина чисел нового прогону (<c>@newRun</c>) і актуальних прогонів
    /// (<c>@current</c>, JSON-масив) різна: симетрична різниця двома <c>EXCEPT</c>. Обидва боки — seek по
    /// <c>(PeriodKey, DocumentId, CalculationRunId)</c>, як читання актуального. Один стовпець-рядок
    /// (<c>методологія:екземпляр</c>, екземпляр порожній для результату без нього), щоб не заводити тип під запит.
    /// </summary>
    private const string ChangedResultsSql = """
        WITH fresh AS (
            SELECT mv.MethodologyId, r.TableInstanceId, r.SourceRowKey, r.OutputCode, r.Kind, r.SubstanceEntryId, r.UnitId, r.Value
              FROM calc.CalculationResult r
              JOIN calc.MethodologyVersion mv ON mv.Id = r.MethodologyVersionId
             WHERE r.PeriodKey = @periodKey AND r.DocumentId = @documentId AND r.CalculationRunId = @newRun
               AND mv.MethodologyId IN (SELECT CAST(j.[value] AS int) FROM OPENJSON(@methodologies) j)),
        actual AS (
            SELECT mv.MethodologyId, r.TableInstanceId, r.SourceRowKey, r.OutputCode, r.Kind, r.SubstanceEntryId, r.UnitId, r.Value
              FROM calc.CalculationResult r
              JOIN calc.MethodologyVersion mv ON mv.Id = r.MethodologyVersionId
             WHERE r.PeriodKey = @periodKey AND r.DocumentId = @documentId
               AND r.CalculationRunId IN (SELECT CAST(j.[value] AS bigint) FROM OPENJSON(@current) j)
               AND mv.MethodologyId IN (SELECT CAST(j.[value] AS int) FROM OPENJSON(@methodologies) j))
        SELECT DISTINCT CONCAT(d.MethodologyId, N':', d.TableInstanceId) AS Value
          FROM ((SELECT * FROM fresh EXCEPT SELECT * FROM actual)
                UNION ALL
                (SELECT * FROM actual EXCEPT SELECT * FROM fresh)) d
        """;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangedResultScope>> ChangedResultsAsync(
        long calculationRunId, long documentId, IReadOnlyCollection<int> methodologyIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(methodologyIds);
        if (methodologyIds.Count == 0)
        {
            return [];
        }

        var periodKey = await db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.Id == calculationRunId)
            .Select(r => r.PeriodKey)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? 0;

        // Та сама вибірка «що зараз актуальне», що й у ReadCurrentAsync і переносі; сам новий прогін ще не актуальний.
        // Актуального немає — порожній масив: кожне число нового прогону тоді «додалося».
        var current = await CurrentRunIdsAsync(documentId, periodKey, calculationRunId, ct).ConfigureAwait(false);

        var changed = await db.Database
            .SqlQueryRaw<string>(
                ChangedResultsSql,
                new SqlParameter("@periodKey", periodKey),
                new SqlParameter("@documentId", documentId),
                new SqlParameter("@newRun", calculationRunId),
                new SqlParameter("@current", System.Text.Json.JsonSerializer.Serialize(current)),
                new SqlParameter("@methodologies", System.Text.Json.JsonSerializer.Serialize(methodologyIds)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. changed.Select(ParseChangedScope)];
    }

    /// <summary>Розбирає рядок <c>методологія:екземпляр</c> запиту <see cref="ChangedResultsSql"/>.</summary>
    private static ChangedResultScope ParseChangedScope(string value)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        var methodologyId = int.Parse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture);
        var table = value.AsSpan(separator + 1);

        return new ChangedResultScope(
            methodologyId,
            table.IsEmpty ? null : long.Parse(table, NumberStyles.None, CultureInfo.InvariantCulture));
    }
}
