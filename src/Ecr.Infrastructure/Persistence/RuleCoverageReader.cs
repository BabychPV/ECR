using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRuleCoverageReader"/>.</summary>
/// <remarks>
/// ⚠ Сирий SQL: кількість колонок групування залежить від правил, і форму результату
/// EF наперед не знає. Кожна колонка — <c>LEFT JOIN doc.CellValue</c> пошуком по
/// первинному ключу <c>(PeriodKey, TableRowId, ColumnDefId)</c>; <c>PeriodKey</c> стоїть
/// у кожному предикаті партиційованих таблиць (урок <c>WR-05</c>).
/// </remarks>
public sealed class RuleCoverageReader(EcrDbContext db) : IRuleCoverageReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RuleCoverageCombination>> ReadAsync(
        IReadOnlyList<int> tableDefIds,
        IReadOnlyList<int> columnDefIds,
        int periodFrom,
        int periodTo,
        int limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableDefIds);
        ArgumentNullException.ThrowIfNull(columnDefIds);

        if (tableDefIds.Count == 0)
        {
            return [];
        }

        var select = new StringBuilder();
        var joins = new StringBuilder();
        var group = new List<string>();
        for (var i = 0; i < columnDefIds.Count; i++)
        {
            var c = $"c{i.ToString(CultureInfo.InvariantCulture)}";
            select.Append(CultureInfo.InvariantCulture, $"{c}.ValueString, {c}.ValueNumeric, {c}.ValueRegistryEntryId, {c}.ValueBool, ");
            joins.Append(CultureInfo.InvariantCulture, $"""
                LEFT JOIN doc.CellValue {c}
                  ON {c}.PeriodKey = r.PeriodKey AND {c}.TableRowId = r.Id AND {c}.ColumnDefId = @col{i}

                """);
            group.Add($"{c}.ValueString, {c}.ValueNumeric, {c}.ValueRegistryEntryId, {c}.ValueBool");
        }

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Живі рядки (IsDeleted = 0) — ті самі, що бачить прогін (RowStore.RowsQuery).
        command.CommandText = $"""
            SELECT TOP (@take) {select}COUNT_BIG(*) AS RowsCount, COUNT(DISTINCT ti.DocumentId) AS Documents
              FROM doc.TableInstance ti
              JOIN doc.TableRow r
                ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
            {joins}
             WHERE ti.PeriodKey BETWEEN @from AND @to
               AND ti.TableDefId IN (SELECT CAST(j.value AS int) FROM OPENJSON(@tables) j)
            {(group.Count == 0 ? string.Empty : "GROUP BY " + string.Join(", ", group))}
             ORDER BY RowsCount DESC
            """;

        command.Parameters.AddWithValue("@take", limit + 1);
        command.Parameters.AddWithValue("@from", periodFrom);
        command.Parameters.AddWithValue("@to", periodTo);
        command.Parameters.AddWithValue("@tables", JsonSerializer.Serialize(tableDefIds));
        for (var i = 0; i < columnDefIds.Count; i++)
        {
            command.Parameters.AddWithValue($"@col{i.ToString(CultureInfo.InvariantCulture)}", columnDefIds[i]);
        }

        var result = new List<RuleCoverageCombination>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var rows = reader.GetInt64(columnDefIds.Count * 4);
            if (rows == 0)
            {
                // Агрегат без GROUP BY над порожньою вибіркою дає один нульовий рядок.
                continue;
            }

            var values = new CellValueData?[columnDefIds.Count];
            for (var i = 0; i < columnDefIds.Count; i++)
            {
                var o = i * 4;
                var value = new CellValueData
                {
                    ValueString = reader.IsDBNull(o) ? null : reader.GetString(o),
                    ValueNumeric = reader.IsDBNull(o + 1) ? null : reader.GetDecimal(o + 1),
                    ValueRegistryEntryId = reader.IsDBNull(o + 2) ? null : reader.GetInt64(o + 2),
                    ValueBool = reader.IsDBNull(o + 3) ? null : reader.GetBoolean(o + 3),
                };
                values[i] = value is { ValueString: null, ValueNumeric: null, ValueRegistryEntryId: null, ValueBool: null }
                    ? null
                    : value;
            }

            result.Add(new RuleCoverageCombination(values, rows, reader.GetInt32(columnDefIds.Count * 4 + 1)));
        }

        return result;
    }
}
