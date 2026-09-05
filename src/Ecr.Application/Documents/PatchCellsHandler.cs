using Ecr.Application.Common;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Пакетна зміна комірок. Бюджет — **p95 300 мс на 100 комірок**
/// (tz/08 §8.2), тому кожна зайва дія тут коштує дорого.
/// </summary>
/// <remarks>
/// Часткове застосування заборонене: конфлікт у будь-якому рядку відхиляє
/// весь батч. «Перезаписати мовчки» не є опцією — користувач має побачити
/// розбіжність (B04 §2.3).
/// </remarks>
public sealed class PatchCellsHandler(
    ICellStore cellStore,
    IRowStore rowStore,
    IMetadataCache metadata,
    IAccessDecisionService access,
    Validation.ValidationEngine validation,
    IAuditWriter audit,
    IBackgroundJobScheduler jobs,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Застосовує зміни.</summary>
    /// <exception cref="ConcurrencyConflictException">
    /// Розбіжність <c>baseVersion</c> — <c>ECR-CELL-0409</c>.
    /// </exception>
    /// <exception cref="AccessDeniedException">
    /// Хоч одна комірка недоступна — <c>ECR-ACCS-0403</c> із причиною.
    /// </exception>
    /// <exception cref="BusinessRuleException">
    /// Комірковий <c>Error</c> валідації — <c>ECR-CELL-0422</c>.
    /// </exception>
    public async Task<PatchCellsResponse> HandleAsync(PatchCellsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var periodKey = new PeriodKey(request.PeriodKey);
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не може змінювати дані.");

        // 1. Структура зі знімка метаданих — без звернення до БД (D-16).
        //    Потрібна, щоб резолвити коди колонок у ColumnDefId; вигадувати
        //    їх не можна, це частина первинного ключа комірки.
        var instance = await rowStore.ResolveTableInstanceAsync(request.TableInstanceId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        // ⚠ У мапі — сам ColumnDef, а не лише Id. Значення розбирається за
        // ОГОЛОШЕНИМ типом колонки: через HTTP усе приходить JsonElement-ом, і
        // здогадка за виглядом значення клала число в текст, а ідентифікатор
        // запису довідника — у ValueNumeric (`A7-01`).
        //
        // ⛔ Мапа будується ЛИШЕ з колонок ЦІЄЇ таблиці. Код колонки унікальний
        // у межах таблиці, а не версії шаблону: у реальному шаблоні дев'яносто
        // таблиць, і `C2` є майже в кожній. До `A7-27` тут стояло групування
        // по всій версії з `g.First()` — тобто код резолвився в колонку
        // ВИПАДКОВОЇ таблиці.
        //
        // ⚠ Дані від цього НЕ псувалися, і це заслуга схеми, а не коду:
        // `FK_CellValue_Column` складений (`D-84`) —
        // `(TableDefId, ColumnDefId) → cfg.ColumnDef (TableDefId, Id)`, — тому
        // комірка з колонкою чужої таблиці відхиляється базою. Дефект давав
        // відмову запису, а не тихий запис не туди.
        //
        // ⛔ Саме тому цей ключ не можна спрощувати до `ColumnDefId`: він
        // єдиний, хто ловить помилку адресації, і зробив це раніше за будь-який
        // тест.
        var columnDefs = snapshot.ColumnsById.Values
            .Where(c => c.TableDefId == instance.TableDefId)
            .ToDictionary(c => c.Code, StringComparer.Ordinal);

        var columns = columnDefs.ToDictionary(p => p.Key, p => p.Value.Id, StringComparer.Ordinal);

        // 2. Поточний стан рядків — ОДИН запит на батч, не на рядок.
        var versions = await rowStore.GetRowVersionsAsync(request.TableInstanceId, periodKey, ct).ConfigureAwait(false);
        var rowIds = await rowStore.GetRowIdsAsync(request.TableInstanceId, periodKey, ct).ConfigureAwait(false);

        // 3. Створення і оновлення розділяються за BaseVersion (R-B2):
        //    null означає намір СТВОРИТИ рядок, а не «мені байдуже до версії».
        var creations = request.Rows.Where(r => r.BaseVersion is null).ToList();
        var updates = request.Rows.Where(r => r.BaseVersion is not null).ToList();

        var duplicates = creations.Where(r => versions.ContainsKey(r.RowKey)).Select(r => r.RowKey).ToList();
        if (duplicates.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-ROW-0409",
                $"Рядки з такими ключами вже існують: {string.Join(", ", duplicates)}.",
                new Dictionary<string, object?> { ["rowKeys"] = duplicates });
        }

        // 4. Конфлікти версій. Збираємо ВСІ, а не падаємо на першому:
        //    користувач має побачити повну картину розбіжностей.
        var conflicts = new List<CellConflictDto>();
        foreach (var row in updates)
        {
            if (!versions.TryGetValue(row.RowKey, out var current))
            {
                conflicts.Add(new CellConflictDto(row.RowKey, "*", null, null, "", clock.UtcNow, ""));
                continue;
            }

            if (!string.Equals(current, row.BaseVersion, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var cell in row.Cells)
                {
                    conflicts.Add(new CellConflictDto(
                        row.RowKey, cell.ColumnCode, cell.Value, null, "", clock.UtcNow, current));
                }
            }
        }

        if (conflicts.Count > 0)
        {
            throw new ConcurrencyConflictException(
                "ECR-CELL-0409",
                $"Батч відхилено: рядків із розбіжністю версії — {conflicts.Select(c => c.RowKey).Distinct().Count()}.",
                new Dictionary<string, object?> { ["conflicts"] = conflicts });
        }

        // 5. Права — ОДНИМ викликом на весь зріз. Поштучна перевірка комірок
        //    не вкладається в бюджет 300 мс.
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var addresses = new List<CellAddress>();
        foreach (var row in updates)
        {
            if (!rowIds.TryGetValue(row.RowKey, out var rowId))
            {
                continue;
            }
            foreach (var cell in row.Cells)
            {
                addresses.Add(new CellAddress(periodKey, rowId, ColumnDefIdOf(columns, cell.ColumnCode)));
            }
        }

        if (addresses.Count > 0)
        {
            var decisions = await access.CanEditSliceAsync(profile, request.TableInstanceId, ct)
                                        .ConfigureAwait(false);

            // Перевіряємо лише ті адреси, які справді змінюються: рішення
            // приходять на весь зріз, але відхиляти батч через заборонену
            // комірку, якої ніхто не чіпав, було б неправильно.
            var denied = addresses
                .Where(a => decisions.TryGetValue(a, out var d) && !d.IsAllowed)
                .Select(a => new KeyValuePair<CellAddress, EditDecision>(a, decisions[a]))
                .ToList();
            if (denied.Count > 0)
            {
                var first = denied[0].Value;
                throw new AccessDeniedException(
                    "ECR-ACCS-0403",
                    $"Заборонених комірок у батчі: {denied.Count}. Причина першої: {first.Reason}.",
                    new Dictionary<string, object?>
                    {
                        ["deniedCount"] = denied.Count,
                        ["reason"] = first.Reason.ToString(),
                        ["detail"] = first.Detail
                    });
            }
        }

        // 6. Розкладка на три операції (R-B4): значення → upsert,
        //    value = null → delete, isEmpty → upsert з IsEmpty = 1.
        //    Поле, ВІДСУТНЄ в запиті, сюди не потрапляє взагалі — саме тому
        //    «не чіпати» і «стерти» лишаються різними намірами.
        var upserts = new List<CellRecord>();
        var deletes = new List<CellAddress>();
        var touched = new List<long>();

        foreach (var row in creations)
        {
            var id = await rowStore.CreateRowAsync(
                request.TableInstanceId, periodKey, RowKey.Create(row.RowKey), ordinal: 0, ct).ConfigureAwait(false);
            touched.Add(id);
            Distribute(row, id, periodKey, columnDefs, instance.TableDefId, upserts, deletes);
        }

        foreach (var row in updates)
        {
            if (!rowIds.TryGetValue(row.RowKey, out var id))
            {
                continue;
            }
            touched.Add(id);
            Distribute(row, id, periodKey, columnDefs, instance.TableDefId, upserts, deletes);
        }

        // 6a. Валідація. ⚠ Блокує запис ЛИШЕ комірковий Error (R-B3, D-90):
        //     заборона зберегти проміжний стан зробила б роботу з великою
        //     таблицею неможливою — користувач заповнює її не за один раз.
        var messages = Validate(snapshot, instance.TableDefId, request, upserts, rowIds);
        var blocking = messages.Where(m => m.BlocksSave).ToList();
        if (blocking.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-CELL-0422",
                $"Валідація відхилила запис: комірок із помилкою — {blocking.Count}.",
                new Dictionary<string, object?>
                {
                    ["cells"] = blocking
                        .Select(m => new { m.RowKey, m.ColumnCode, m.RuleCode, m.Message })
                        .ToList(),
                });
        }

        var now = clock.UtcNow;

        // 7. Одна транзакція: значення, «дотик» рядків і аудит. Аудит поза
        //    транзакцією дав би журнал, у якому є зміни, яких у даних немає.
        await cellStore.ApplyAsync(
            new CellChangeSet(request.TableInstanceId, upserts, deletes, touched, userId, IsLateEdit: false),
            ct).ConfigureAwait(false);

        await rowStore.TouchRowsAsync(touched, now, ct).ConfigureAwait(false);

        await audit.WriteCellChangesAsync(
            BuildAuditRecords(request, upserts, deletes, userId, now, instance.DocumentId), ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // 8. ⚠ Перерахунок ставиться в чергу ПІСЛЯ commit і поза транзакцією:
        //    воркер інакше почав би читати рядки, яких ще не видно, і отримав
        //    би або старі значення, або блокування на піку останнього дня.
        await jobs.EnqueueAsync<Ports.IRecalculationJob>(
            new { request.TableInstanceId, request.PeriodKey }, ct).ConfigureAwait(false);

        var newVersions = await rowStore.GetRowVersionsAsync(request.TableInstanceId, periodKey, ct)
                                        .ConfigureAwait(false);

        return new PatchCellsResponse(
            AppliedCells: upserts.Count + deletes.Count,
            RowVersions: newVersions,
            // Повідомлення, які запис НЕ блокують, повертаються клієнтові:
            // інакше про них ніхто б не дізнався, і сенс рівнів зник би.
            Validation: messages
                .Select(m => new ValidationMessageDto(
                    m.Severity.ToString(), m.RuleCode, m.Message, m.RowKey, m.ColumnCode))
                .ToList());
    }

    /// <summary>Валідує змінені комірки і правила рівня рядка.</summary>
    private List<Validation.ValidationMessage> Validate(
        Domain.Entities.Configuration.TemplateVersionSnapshot snapshot,
        int tableDefId,
        PatchCellsRequest request,
        List<CellRecord> upserts,
        IReadOnlyDictionary<string, long> rowIds)
    {
        var table = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .FirstOrDefault(t => t.Id == tableDefId);

        IReadOnlyList<Domain.Entities.Configuration.ValidationRule> rules =
            table?.ValidationRules ?? [];
        var byRowId = rowIds.ToDictionary(p => p.Value, p => p.Key);
        var messages = new List<Validation.ValidationMessage>();

        foreach (var record in upserts)
        {
            if (!snapshot.ColumnsById.TryGetValue(record.Address.ColumnDefId, out var column))
            {
                continue;
            }

            var rowKey = byRowId.GetValueOrDefault(record.Address.TableRowId);
            foreach (var message in validation.ValidateCell(column, record.Value, rules))
            {
                messages.Add(message with { RowKey = rowKey });
            }
        }

        // Правила рівня рядка виконуються після коміркових і запис НЕ блокують:
        // рядок може бути незавершеним посеред заповнення, і це нормальний стан.
        foreach (var row in request.Rows)
        {
            messages.AddRange(validation
                .ValidateScope(scope: 1, rules, new PatchRowValidationContext(row))
                .Select(m => m with { RowKey = row.RowKey }));
        }

        return messages;
    }

    /// <summary>Значення рядка з самого запиту — без звернення до сховища.</summary>
    /// <remarks>
    /// ⚠ Правило рівня рядка бачить те, що клієнт ЩОЙНО надіслав, а не те, що
    /// лежить у базі: перевіряти треба намір користувача, інакше повідомлення
    /// стосувалося б стану, який зараз перезаписується.
    /// </remarks>
    private sealed class PatchRowValidationContext(PatchRow row) : Validation.IValidationContext
    {
        public object? GetCell(string columnCode)
            => CellValueReader.Normalize(
                row.Cells
                   .FirstOrDefault(c => string.Equals(c.ColumnCode, columnCode, StringComparison.OrdinalIgnoreCase))
                   ?.Value);

        public object? GetCell(string rowKey, string columnCode)
            => string.Equals(rowKey, row.RowKey, StringComparison.Ordinal) ? GetCell(columnCode) : null;
    }

    private static void Distribute(
        PatchRow row,
        long rowId,
        PeriodKey periodKey,
        IReadOnlyDictionary<string, ColumnDef> columnDefs,
        int tableDefId,
        List<CellRecord> upserts,
        List<CellAddress> deletes)
    {
        foreach (var cell in row.Cells)
        {
            var column = ColumnOf(columnDefs, cell.ColumnCode);
            var address = new CellAddress(periodKey, rowId, column.Id);

            if (cell.IsEmpty)
            {
                upserts.Add(new CellRecord(address, TableDefId: tableDefId, CellValueData.Empty));
                continue;
            }

            // ⚠ Три різні операції (R-B4). `null` — стерти, і саме тому
            // читач повертає null, а не порожнє значення: «стерти» і «явна
            // порожнеча» — різні наміри, і зводити їх в один означає втратити
            // відмінність, яку користувач висловив свідомо.
            var data = CellValueReader.Read(cell.Value, column);

            if (data is null)
            {
                deletes.Add(address);
            }
            else
            {
                upserts.Add(new CellRecord(address, TableDefId: tableDefId, data));
            }
        }
    }

    /// <summary>Опис колонки за кодом; невідомий код — відмова, а не пропуск.</summary>
    private static ColumnDef ColumnOf(IReadOnlyDictionary<string, ColumnDef> columnDefs, string code)
        => columnDefs.TryGetValue(code, out var column)
            ? column
            : throw new BusinessRuleException(
                "ECR-CELL-0422",
                $"Колонки «{code}» немає в цій версії шаблону.",
                new Dictionary<string, object?> { ["columnCode"] = code });

    private static List<CellChangeRecord> BuildAuditRecords(
        PatchCellsRequest request, List<CellRecord> upserts,
        List<CellAddress> deletes, int userId, DateTime now, long documentId)
    {
        var records = new List<CellChangeRecord>(upserts.Count + deletes.Count);

        foreach (var u in upserts)
        {
            records.Add(new CellChangeRecord(
                now, u.Address, DocumentId: documentId, RowKey: string.Empty,
                OldValue: null, NewValue: Describe(u.Value),
                userId, request.Origin, IsLateEdit: false, CorrelationId: null));
        }

        foreach (var d in deletes)
        {
            records.Add(new CellChangeRecord(
                now, d, DocumentId: documentId, RowKey: string.Empty,
                OldValue: null, NewValue: null,
                userId, request.Origin, IsLateEdit: false, CorrelationId: null));
        }

        return records;
    }

    private static string? Describe(CellValueData v)
        => v.IsEmpty ? string.Empty
         : v.ValueNumeric?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? v.ValueString
           ?? v.ValueBool?.ToString()
           ?? v.ValueDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
           ?? v.ValueRegistryEntryId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? v.ValueUnitId?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Код колонки → <c>ColumnDefId</c> за знімком структури.
    /// </summary>
    /// <remarks>
    /// Резолвиться зі знімка, а не вигадується: <c>ColumnDefId</c> — частина
    /// первинного ключа комірки, і будь-яке «приблизне» значення записало б
    /// дані в неіснуючу колонку.
    /// </remarks>
    private static int ColumnDefIdOf(Dictionary<string, int> map, string columnCode)
        => map.TryGetValue(columnCode, out var id)
            ? id
            : throw new BusinessRuleException(
                "ECR-CELL-0422", $"Колонки з кодом '{columnCode}' немає в структурі версії.");
}
