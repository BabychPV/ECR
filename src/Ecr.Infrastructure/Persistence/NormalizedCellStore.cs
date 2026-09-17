using System.Data;
using System.Globalization;
using System.Text;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
public sealed class NormalizedCellStore(EcrDbContext db) : ICellStore
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
    /// <remarks>
    /// ⚠ Q-243. Раніше цей метод ЗАВЖДИ відкривав і комітив ВЛАСНУ
    /// транзакцію — навіть коли викликач (<c>PatchCellsHandler.PersistChangesAsync</c>)
    /// уже тримав ширшу транзакцію через <c>IUnitOfWork.BeginTransactionAsync</c>.
    /// Комірки комітились одразу, до того як «дотик» рядків, «дотик»
    /// документа й запис аудиту навіть почали виконуватись — збій між ними
    /// лишав змінені дані БЕЗ відповідного рядка аудиту. Тепер: якщо на
    /// <c>db</c> уже відкрита транзакція (ambient, <c>CurrentTransaction</c>),
    /// приєднуємось до НЕЇ і не комітимо — коміт/відкат належить тому, хто
    /// відкрив; якщо ні (виклик поза Q-243, наприклад
    /// <c>RecalculationService</c>, який власної транзакції не відкриває) —
    /// поведінка та сама, що й раніше: коротка власна транзакція, свій коміт.
    /// </remarks>
    public async Task ApplyAsync(CellChangeSet changes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        var ambient = db.Database.CurrentTransaction;
        if (ambient is not null)
        {
            var joined = (SqlTransaction)ambient.GetDbTransaction();
            await ClaimRowsAsync(connection, joined, changes, ct).ConfigureAwait(false);
            await DeleteAsync(connection, joined, changes.Deletes, ct).ConfigureAwait(false);
            await UpsertAsync(connection, joined, changes.Upserts, ct).ConfigureAwait(false);
            await TouchRowsAsync(connection, joined, changes, ct).ConfigureAwait(false);
            return;
        }

        // ⚠ Транзакція коротка навмисно. Під RCSI кожна відкрита транзакція
        // тримає версії рядків у tempdb, і довга транзакція роздуває version
        // store так, що страждає вся база, а не лише цей запит (D-29).
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await ClaimRowsAsync(connection, tx, changes, ct).ConfigureAwait(false);
        await DeleteAsync(connection, tx, changes.Deletes, ct).ConfigureAwait(false);
        await UpsertAsync(connection, tx, changes.Upserts, ct).ConfigureAwait(false);
        await TouchRowsAsync(connection, tx, changes, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Захоплює рядки батчу під заявлену <c>RowVersion</c> — ОДНИМ запитом на
    /// чанк, ПЕРШОЮ дією транзакції.
    /// </summary>
    /// <remarks>
    /// ⛔ Тихе загублене оновлення (lost update) на сітці документа. До цього
    /// методу оптимістичне блокування трималося на порівнянні <b>в пам'яті
    /// C#</b> (<c>PatchCellsHandler.EnsureNoVersionConflicts</c>), яке
    /// виконувалось ПОЗА транзакцією запису, а самі записи не несли предиката
    /// на <c>RowVersion</c> взагалі: <c>MERGE doc.CellValue WITH (HOLDLOCK)</c>
    /// звіряв лише адресу комірки, <c>TouchRowsAsync</c> нижче — лише <c>Id</c>.
    /// Тобто між «прочитали версію» і «записали» лишалося вікно, у яке
    /// вміщався ВЕСЬ чужий батч: двоє аналітиків правлять ту саму комірку,
    /// обидва отримують <c>200</c>, другий мовчки затирає першого, а рядок
    /// аудиту стверджує перехід (<c>OldValue</c> → <c>NewValue</c>), якого
    /// ніколи не було. <c>HOLDLOCK</c> цього не закривав і закрити не міг: він
    /// стереже ДІАПАЗОН КЛЮЧІВ <c>doc.CellValue</c>, а змінюваний стан, за яким
    /// звіряються, лежить у <c>doc.TableRow.RowVersion</c>.
    ///
    /// ⚠ Звірка й запис — ОДИН <c>UPDATE</c>, а не «прочитати й порівняти»:
    /// повторне читання версії всередині транзакції дало б рівно те саме вікно,
    /// лише вужче. <c>UPDATE</c> бере блокування оновлення, читає ОСТАННЮ
    /// закомічену версію навіть під RCSI і в тій самій операції або захоплює
    /// рядок, або не знаходить його — третього стану немає.
    ///
    /// ⚠ Захоплення йде ПЕРШИМ, до <c>DELETE</c>/<c>MERGE</c>: на конфлікті
    /// транзакція відкочується, не написавши жодної комірки, і ексклюзивне
    /// блокування захоплених рядків тримається до кінця батчу — тож паралельний
    /// письменник у ті самі рядки чекає, а не проскакує повз.
    ///
    /// ⚠ Заразом це і є «дотик» рядка (<c>ModifiedAt</c>), тому
    /// <see cref="TouchRowsAsync"/> нижче захоплені рядки вже не чіпає: другий
    /// <c>UPDATE</c> по тих самих рядках коштував би зайвого проходу на шляху,
    /// чий бюджет — p95 150 мс на 100 комірок.
    ///
    /// ⚠ <c>OUTPUT … INTO</c>, а не голий <c>OUTPUT</c>: голий недоступний, щойно
    /// на таблиці з'явиться тригер або каскадний зовнішній ключ, і зламався б
    /// не тут, а в проді на першій такій зміні схеми.
    /// </remarks>
    /// <exception cref="ConcurrencyConflictException">
    /// Версія хоч одного рядка змінилася між читанням і записом —
    /// <c>ECR-CELL-0409</c>; <c>Details</c> несе
    /// <see cref="CellChangeSet.StaleRowIdsDetail"/> з переліком
    /// <c>TableRow.Id</c>.
    /// </exception>
    private static async Task ClaimRowsAsync(
        SqlConnection connection, SqlTransaction tx, CellChangeSet changes, CancellationToken ct)
    {
        var expected = changes.ExpectedRowVersions;
        if (expected is null || expected.Count == 0)
        {
            return;
        }

        var periodKey = PeriodKeyOf(changes);

        // ⚠ Порядок за Id — сталий порядок захоплення блокувань. Два батчі, що
        // перетинаються рядками, інакше беруть їх у порядку словника (тобто в
        // порядку ключів рядків клієнта) і складаються у взаємне блокування.
        var claims = expected.OrderBy(pair => pair.Key).ToArray();
        var stale = new List<long>();

        foreach (var chunk in claims.Chunk(MergeChunkSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tx;

            var values = new StringBuilder();
            for (var i = 0; i < chunk.Length; i++)
            {
                if (i > 0)
                {
                    values.Append(',');
                }

                values.Append(CultureInfo.InvariantCulture, $"(@i{i},@v{i})");
                command.Parameters.AddWithValue($"@i{i}", chunk[i].Key);

                // ⚠ Саме binary(8): `rowversion` має рівно цю ширину, і без
                // явного типу провайдер вивів би `varbinary` іншої довжини —
                // порівняння тихо не збіглося б НІКОЛИ, тобто кожен запис
                // ставав би конфліктом.
                var version = command.Parameters.Add($"@v{i}", SqlDbType.Binary, 8);
                version.Value = Convert.FromBase64String(chunk[i].Value);
            }

            var periodFilter = periodKey is null ? string.Empty : "WHERE r.PeriodKey = @pk";
            if (periodKey is not null)
            {
                command.Parameters.AddWithValue("@pk", periodKey.Value);
            }

            command.CommandText = $"""
                DECLARE @claimed TABLE (Id bigint PRIMARY KEY);
                UPDATE r SET ModifiedAt = SYSUTCDATETIME()
                OUTPUT inserted.Id INTO @claimed (Id)
                FROM doc.TableRow AS r
                INNER JOIN (VALUES {values}) AS source (Id, RowVersion)
                    ON r.Id = source.Id AND r.RowVersion = source.RowVersion
                {periodFilter};
                SELECT Id FROM @claimed;
                """;

            var claimed = new HashSet<long>();
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    claimed.Add(reader.GetInt64(0));
                }
            }

            // Не захопили — отже, версія вже інша (або рядка не стало). Обидва
            // випадки для автора батчу означають одне: те, від чого він
            // відштовхувався, більше не є правдою.
            stale.AddRange(chunk.Where(pair => !claimed.Contains(pair.Key)).Select(pair => pair.Key));
        }

        if (stale.Count > 0)
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.CellConflict,
                $"Версія рядка змінилася між читанням і записом: рядків — {stale.Count}.",
                new Dictionary<string, object?> { [CellChangeSet.StaleRowIdsDetail] = stale });
        }
    }

    /// <summary>
    /// Період батчу — ключ партиції; <c>null</c>, якщо батч не несе жодної
    /// адреси.
    /// </summary>
    /// <remarks>
    /// Усі комірки одного <c>TableInstance</c> лежать в одній партиції, і без
    /// <c>PeriodKey</c> у <c>WHERE</c> запит по <c>doc.TableRow</c> сканував би
    /// всі 25.
    /// </remarks>
    private static int? PeriodKeyOf(CellChangeSet changes)
        => changes.Upserts.Count > 0
            ? changes.Upserts[0].Address.PeriodKey.Value
            : changes.Deletes.Count > 0
                ? changes.Deletes[0].PeriodKey.Value
                : null;

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
                // ⛔ Звужено до int навмисно: фізична колонка лишається
                // `int` (RegistryEntry.Id — long у CLR, конвертований у int
                // лише для зберігання, RegistryEntryConfiguration.cs:41).
                // Сирий SQL-параметр повз конвеєр EF `HasConversion<int?>()`
                // потребує того самого звуження, що й BulkCellLoader.
                AddNullable(command, $"@ve{i}", (int?)value.ValueRegistryEntryId, SqlDbType.Int);
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
    ///
    /// ⚠ Рядки, вже захоплені <see cref="ClaimRowsAsync"/>, сюди не
    /// потрапляють: той <c>UPDATE</c> підняв їм <c>ModifiedAt</c> ЗАРАЗОМ зі
    /// звіркою версії, і повторити його означало б зайвий прохід по тих самих
    /// рядках на шляху з бюджетом p95 150 мс на 100 комірок. Лишаються ті, чиєї
    /// версії ніхто не заявляв — насамперед ЩОЙНО СТВОРЕНІ рядки батчу: у них
    /// версії від чого відштовхуватись не було, а «дотик» потрібен так само.
    /// </remarks>
    private static async Task TouchRowsAsync(
        SqlConnection connection, SqlTransaction tx, CellChangeSet changes, CancellationToken ct)
    {
        var claimed = changes.ExpectedRowVersions;
        IReadOnlyList<long> pending = claimed is null || claimed.Count == 0
            ? changes.TouchedRowIds
            : [.. changes.TouchedRowIds.Where(id => !claimed.ContainsKey(id))];

        if (pending.Count == 0)
        {
            return;
        }

        var periodKey = PeriodKeyOf(changes);

        foreach (var chunk in pending.Chunk(MergeChunkSize))
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
