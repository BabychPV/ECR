using System.Data;
using System.Globalization;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Нормалізоване сховище комірок — **базова модель** (D-21).
/// </summary>
/// <remarks>
/// Бюджет: <c>ReadSliceAsync</c> — p95 &lt; 600 мс на 500×60,
/// <c>ApplyAsync</c> — p95 &lt; 150 мс на 100 комірок (tz/08 §8.2).
/// Ці числа і є критерієм гейта Етапу 0: якщо не проходить після індексів і
/// стиснення — вибірково по таблицях вмикається гібрид, а не глобально.
/// </remarks>
public sealed class NormalizedCellStore(EcrDbContext db, BulkCellLoader bulk) : ICellStore
{
    /// <summary>
    /// Скільки комірок іде в один <c>MERGE</c>.
    /// </summary>
    /// <remarks>
    /// Обмеження не з голови: SQL Server приймає максимум 2100 параметрів на
    /// запит, а тут їх 12 на комірку. 100 × 12 = 1200 — із запасом на
    /// службові. Без чанкування батч на 200 комірок падав би не в тестах, а в
    /// проді, на найбільшій таблиці.
    /// </remarks>
    private const int MergeChunkSize = 100;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct)
    {
        // ОДИН запит. TableInstance приєднаний не заради своїх полів, а заради
        // PeriodKey: він дає оптимізатору кореляцію, за якою відсікається
        // партиція. Без нього довелося б або читати період окремим запитом,
        // або сканувати всі 25 партицій.
        var rows = await (
            from instance in db.TableInstances.AsNoTracking()
            where instance.Id == tableInstanceId
            join row in db.TableRows.AsNoTracking()
                on new { P = instance.PeriodKeyValue, I = instance.Id }
                equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
            join cell in db.CellValues.AsNoTracking()
                on new { P = row.PeriodKeyValue, R = row.Id }
                equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
            where !row.IsDeleted
            select new
            {
                cell.PeriodKeyValue,
                cell.TableRowId,
                cell.ColumnDefId,
                cell.TableDefId,
                cell.ValueString,
                cell.ValueNumeric,
                cell.ValueDate,
                cell.ValueBool,
                cell.ValueRegistryEntryId,
                cell.ValueUnitId,
                cell.IsCalculated,
                cell.IsEmpty,
            }).ToListAsync(ct).ConfigureAwait(false);

        // Порожніх комірок у базі не існує взагалі — клієнт бере
        // ColumnDef.DefaultValue (ФВ-3.8). Явна порожнеча — це рядок із
        // IsEmpty = 1, і він повертається (R-B4).
        return [.. rows.Select(r => new CellRecord(
            new CellAddress(new PeriodKey(r.PeriodKeyValue), r.TableRowId, r.ColumnDefId),
            r.TableDefId,
            new CellValueData
            {
                ValueString = r.ValueString,
                ValueNumeric = r.ValueNumeric,
                ValueDate = r.ValueDate,
                ValueBool = r.ValueBool,
                ValueRegistryEntryId = r.ValueRegistryEntryId,
                ValueUnitId = r.ValueUnitId,
                IsCalculated = r.IsCalculated,
                IsEmpty = r.IsEmpty,
            }))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ReadSlicesAsync(
        IReadOnlyList<long> tableInstanceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableInstanceIds);
        if (tableInstanceIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<CellRecord>>();
        }

        var rows = await (
            from instance in db.TableInstances.AsNoTracking()
            where tableInstanceIds.Contains(instance.Id)
            join row in db.TableRows.AsNoTracking()
                on new { P = instance.PeriodKeyValue, I = instance.Id }
                equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
            join cell in db.CellValues.AsNoTracking()
                on new { P = row.PeriodKeyValue, R = row.Id }
                equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
            where !row.IsDeleted
            select new
            {
                instance.Id,
                cell.PeriodKeyValue,
                cell.TableRowId,
                cell.ColumnDefId,
                cell.TableDefId,
                cell.ValueString,
                cell.ValueNumeric,
                cell.ValueDate,
                cell.ValueBool,
                cell.ValueRegistryEntryId,
                cell.ValueUnitId,
                cell.IsCalculated,
                cell.IsEmpty,
            }).ToListAsync(ct).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.Id)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<CellRecord> (g) => [.. g.Select(r => new CellRecord(
                    new CellAddress(new PeriodKey(r.PeriodKeyValue), r.TableRowId, r.ColumnDefId),
                    r.TableDefId,
                    new CellValueData
                    {
                        ValueString = r.ValueString,
                        ValueNumeric = r.ValueNumeric,
                        ValueDate = r.ValueDate,
                        ValueBool = r.ValueBool,
                        ValueRegistryEntryId = r.ValueRegistryEntryId,
                        ValueUnitId = r.ValueUnitId,
                        IsCalculated = r.IsCalculated,
                        IsEmpty = r.IsEmpty,
                    }))]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var result = new Dictionary<CellAddress, CellValueData>();
        if (addresses.Count == 0)
        {
            return result;
        }

        // Групування за періодом — не мікрооптимізація: кожна група це рівно
        // одна партиція, тож запит на групу читає один діапазон замість усіх.
        foreach (var group in addresses.GroupBy(a => a.PeriodKey.Value))
        {
            var periodKey = group.Key;
            var rowIds = group.Select(a => a.TableRowId).Distinct().ToArray();
            var columnIds = group.Select(a => a.ColumnDefId).Distinct().ToArray();

            var cells = await db.CellValues
                .AsNoTracking()
                .Where(c => c.PeriodKeyValue == periodKey
                            && rowIds.Contains(c.TableRowId)
                            && columnIds.Contains(c.ColumnDefId))
                .Select(c => new
                {
                    c.TableRowId,
                    c.ColumnDefId,
                    c.ValueString,
                    c.ValueNumeric,
                    c.ValueDate,
                    c.ValueBool,
                    c.ValueRegistryEntryId,
                    c.ValueUnitId,
                    c.IsCalculated,
                    c.IsEmpty,
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // Декартів добуток rowIds × columnIds ширший за запитані адреси,
            // тому зайве відсіюється тут, а не в базі: звузити запит до точних
            // пар означало б або OR на кожну адресу, або тимчасову таблицю.
            var wanted = group.ToHashSet();

            foreach (var cell in cells)
            {
                var address = new CellAddress(new PeriodKey(periodKey), cell.TableRowId, cell.ColumnDefId);
                if (!wanted.Contains(address))
                {
                    continue;
                }

                result[address] = new CellValueData
                {
                    ValueString = cell.ValueString,
                    ValueNumeric = cell.ValueNumeric,
                    ValueDate = cell.ValueDate,
                    ValueBool = cell.ValueBool,
                    ValueRegistryEntryId = cell.ValueRegistryEntryId,
                    ValueUnitId = cell.ValueUnitId,
                    IsCalculated = cell.IsCalculated,
                    IsEmpty = cell.IsEmpty,
                };
            }
        }

        // Адреси, яких немає в базі, у словник не потрапляють: «немає комірки»
        // і «комірка явно порожня» — різні стани (R-B4), і склеювати їх тут
        // означало б втратити різницю назавжди.
        return result;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(CellChangeSet changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        // ⚠ Транзакція коротка навмисно. Під RCSI кожна відкрита транзакція
        // тримає версії рядків у tempdb, і довга транзакція роздуває version
        // store так, що страждає вся база, а не лише цей запит (D-29).
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await DeleteAsync(connection, tx, changes.Deletes, ct).ConfigureAwait(false);
        await UpsertAsync(connection, tx, changes.Upserts, ct).ConfigureAwait(false);
        await TouchRowsAsync(connection, tx, changes, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task BulkInsertAsync(IReadOnlyList<CellRecord> records, CancellationToken ct)
        => bulk.LoadAsync(records, ct);

    private static async Task DeleteAsync(
        SqlConnection connection, SqlTransaction tx, IReadOnlyList<CellAddress> deletes, CancellationToken ct)
    {
        if (deletes.Count == 0)
        {
            return;
        }

        // Видалення сутностей не завантажуємо: читати рядок, щоб його стерти,
        // означало б подвоїти кількість звернень на кожну комірку.
        foreach (var chunk in deletes.Chunk(MergeChunkSize))
        {
            var sql = new StringBuilder("DELETE FROM doc.CellValue WHERE ");
            await using var command = connection.CreateCommand();
            command.Transaction = tx;

            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0)
                {
                    sql.Append(" OR ");
                }

                sql.Append(CultureInfo.InvariantCulture,
                    $"(PeriodKey = @p{i} AND TableRowId = @r{i} AND ColumnDefId = @c{i})");

                command.Parameters.AddWithValue($"@p{i}", chunk[i].PeriodKey.Value);
                command.Parameters.AddWithValue($"@r{i}", chunk[i].TableRowId);
                command.Parameters.AddWithValue($"@c{i}", chunk[i].ColumnDefId);
            }

            command.CommandText = sql.ToString();
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Вставляє нові комірки і оновлює наявні одним <c>MERGE</c> на чанк.</summary>
    /// <remarks>
    /// <c>MERGE</c>, а не «прочитати — порівняти — записати»: другий варіант
    /// дає гонку між читанням і записом, яку під RCSI не видно на тестах і
    /// добре видно в останній день періоду.
    /// </remarks>
    private static async Task UpsertAsync(
        SqlConnection connection, SqlTransaction tx, IReadOnlyList<CellRecord> upserts, CancellationToken ct)
    {
        if (upserts.Count == 0)
        {
            return;
        }

        foreach (var chunk in upserts.Chunk(MergeChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;

            var values = new StringBuilder();
            for (var i = 0; i < chunk.Length; i++)
            {
                var record = chunk[i];
                var value = record.Value;

                if (i > 0)
                {
                    values.Append(',');
                }

                values.Append(CultureInfo.InvariantCulture,
                    $"(@p{i},@r{i},@c{i},@t{i},@vs{i},@vn{i},@vd{i},@vb{i},@ve{i},@vu{i},@ic{i},@ie{i})");

                command.Parameters.AddWithValue($"@p{i}", record.Address.PeriodKey.Value);
                command.Parameters.AddWithValue($"@r{i}", record.Address.TableRowId);
                command.Parameters.AddWithValue($"@c{i}", record.Address.ColumnDefId);
                command.Parameters.AddWithValue($"@t{i}", record.TableDefId);
                AddNullable(command, $"@vs{i}", value.ValueString, SqlDbType.NVarChar);
                AddNullable(command, $"@vn{i}", value.ValueNumeric, SqlDbType.Decimal);
                AddNullable(command, $"@vd{i}", value.ValueDate, SqlDbType.DateTime2);
                AddNullable(command, $"@vb{i}", value.ValueBool, SqlDbType.Bit);
                AddNullable(command, $"@ve{i}", value.ValueRegistryEntryId, SqlDbType.Int);
                AddNullable(command, $"@vu{i}", value.ValueUnitId, SqlDbType.Int);
                command.Parameters.AddWithValue($"@ic{i}", value.IsCalculated);
                command.Parameters.AddWithValue($"@ie{i}", value.IsEmpty);
            }

            command.CommandText = $"""
                MERGE doc.CellValue WITH (HOLDLOCK) AS target
                USING (VALUES {values}) AS source
                    (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
                     ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
                ON  target.PeriodKey   = source.PeriodKey
                AND target.TableRowId  = source.TableRowId
                AND target.ColumnDefId = source.ColumnDefId
                WHEN MATCHED THEN UPDATE SET
                    TableDefId = source.TableDefId,
                    ValueString = source.ValueString,
                    ValueNumeric = source.ValueNumeric,
                    ValueDate = source.ValueDate,
                    ValueBool = source.ValueBool,
                    ValueRegistryEntryId = source.ValueRegistryEntryId,
                    ValueUnitId = source.ValueUnitId,
                    IsCalculated = source.IsCalculated,
                    IsEmpty = source.IsEmpty
                WHEN NOT MATCHED THEN INSERT
                    (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
                     ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
                    VALUES (source.PeriodKey, source.TableRowId, source.ColumnDefId, source.TableDefId,
                            source.ValueString, source.ValueNumeric, source.ValueDate, source.ValueBool,
                            source.ValueRegistryEntryId, source.ValueUnitId, source.IsCalculated, source.IsEmpty);
                """;

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Піднімає <c>ModifiedAt</c> у зачеплених рядках.</summary>
    /// <remarks>
    /// ⚠ ОБОВ'ЯЗКОВО. <c>RowVersion</c> у <c>doc.TableRow</c> росте від
    /// <c>UPDATE</c> самого рядка, а не від запису в <c>doc.CellValue</c>.
    /// Без цього «дотику» оптимістичне блокування тихо не працює: два
    /// користувачі правлять одні й ті самі комірки, обидва бачать незмінений
    /// <c>RowVersion</c>, і другий перезаписує першого (B04 §2.4).
    /// </remarks>
    private static async Task TouchRowsAsync(
        SqlConnection connection, SqlTransaction tx, CellChangeSet changes, CancellationToken ct)
    {
        if (changes.TouchedRowIds.Count == 0)
        {
            return;
        }

        // Період беремо з адрес батчу: усі комірки одного TableInstance лежать
        // в одній партиції, і без PeriodKey у WHERE оновлення сканувало б усі.
        var periodKey = changes.Upserts.Count > 0
            ? changes.Upserts[0].Address.PeriodKey.Value
            : changes.Deletes.Count > 0
                ? changes.Deletes[0].PeriodKey.Value
                : (int?)null;

        foreach (var chunk in changes.TouchedRowIds.Chunk(MergeChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;

            var ids = new StringBuilder();
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0)
                {
                    ids.Append(',');
                }

                ids.Append(CultureInfo.InvariantCulture, $"@i{i}");
                command.Parameters.AddWithValue($"@i{i}", chunk[i]);
            }

            var periodFilter = periodKey is null ? string.Empty : "PeriodKey = @pk AND ";
            if (periodKey is not null)
            {
                command.Parameters.AddWithValue("@pk", periodKey.Value);
            }

            command.CommandText =
                $"UPDATE doc.TableRow SET ModifiedAt = SYSUTCDATETIME() WHERE {periodFilter}Id IN ({ids});";

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static void AddNullable<T>(SqlCommand command, string name, T? value, SqlDbType type)
        where T : struct
    {
        var parameter = command.Parameters.Add(name, type);
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
    }

    private static void AddNullable(SqlCommand command, string name, string? value, SqlDbType type)
    {
        var parameter = command.Parameters.Add(name, type, 1000);
        parameter.Value = (object?)value ?? DBNull.Value;
    }
}
