using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Результат видалення рядка (D3).</summary>
/// <param name="RecalculationJobId">
/// Задача каскадного перерахунку, поставлена видаленням; <c>null</c> — таблиця без колонок
/// (перераховувати нічого).
/// </param>
public sealed record DeleteRowResult(string? RecalculationJobId);

/// <summary>М'яко видаляє динамічний рядок таблиці документа (D3-A).</summary>
/// <remarks>
/// <para>
/// ⛔ Правила (рішення D3-A, 2026-10-12): лише рядки без <c>RowDefId</c> (додані користувачем); лише поки
/// аркуш у <c>Draft</c>/<c>Rejected</c> (рівно та сама перевірка стану, що й у <see cref="CreateRowHandler"/>);
/// право — те саме, що на створення рядка (<c>CanCreateRowsAsync</c>); причина не потрібна.
/// </para>
/// <para>
/// ⚠ Видалення м'яке (<c>TableRow.SoftDelete</c>, ФВ-7.6): комірки й ключ лишаються, а всі читачі вже фільтрують
/// <c>!IsDeleted</c>. Ключ видаленого рядка ЗАЙНЯТИЙ (<c>UQ_TableRow_Key</c> не знає <c>IsDeleted</c>) —
/// повторне створення з тим самим ключем відмовляє <c>ECR-ROW-0409</c>, а не падає на індексі.
/// </para>
/// <para>
/// ⚠ Слід — <c>aud.StructureChange</c> зі складом комірок ДО видалення, у ТІЙ САМІЙ транзакції (за прецедентом
/// <c>SourceEventSyncJob.JournalAndDeleteAsync</c>): відкат видалення забирає й запис.
/// </para>
/// <para>
/// ⛔ Перерахунок: видалення міняє агрегати (<c>SUM</c> по колонці, <c>Rollup</c>), тому ставиться та сама
/// <c>IFormulaRecalculationJob</c>, що й після PATCH; насіння — усі колонки таблиці видаленого рядка. Сам рядок
/// у прогін не потрапляє (живі рядки читаються з <c>!IsDeleted</c>), тому його результатів не пишуть.
/// </para>
/// </remarks>
public sealed class DeleteRowHandler(
    IRowStore rowStore,
    IDocumentStore documents,
    IMetadataCache metadata,
    IAccessDecisionService access,
    ICellStore cells,
    IAuditWriter audit,
    IBackgroundJobScheduler jobs,
    IUnitOfWork uow,
    IClock clock,
    ISheetEditGate sheetGate)
{
    /// <summary>Тип сутності запису аудиту (<c>aud.StructureChange.EntityType</c>).</summary>
    public const string AuditEntityType = "doc.TableRow";

    /// <summary>Операція запису аудиту.</summary>
    public const string AuditOperation = "RowDeleted";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Видаляє рядок <paramref name="rowKey"/> екземпляра таблиці.</summary>
    /// <exception cref="Errors.NotFoundException">
    /// Рядка немає АБО його вже видалено (однакова відповідь), документ чи таблиця недоступні.
    /// </exception>
    /// <exception cref="Errors.BusinessRuleException">
    /// Рядок із шаблону або від джерела подій: <c>ECR-ROW-0409</c>.
    /// </exception>
    public async Task<DeleteRowResult> HandleAsync(
        long documentId, long tableInstanceId, string rowKey, AccessProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var instance = await rowStore.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);

        // ⛔ Належність екземпляра документові з МАРШРУТУ — як у `CreateRowHandler`.
        if (instance.DocumentId != documentId)
        {
            throw new Errors.NotFoundException(
                "ECR-DOC-0404",
                $"Екземпляр таблиці {tableInstanceId} не належить документу {documentId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.tableInstanceNotInDocument",
                    ["tableInstanceId"] = tableInstanceId.ToString(CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Видимість документа — до будь-якої відмови про таблицю чи рядок (нерозкриття): 404, не 403.
        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new Errors.NotFoundException(
                "ECR-DOC-0404",
                $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ D-6: схована таблиця для читача НЕ ІСНУЄ — 404 ДО відмови про рядок.
        await DocumentVisibility
            .RequireTableVisibleAsync(
                access, profile, documentId, instance.TableInstanceId, instance.TableDefId, instance.PeriodKey, ct)
            .ConfigureAwait(false);

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var table = snapshot.Sheets
            .SelectMany(sh => sh.Tables)
            .FirstOrDefault(t => t.Id == instance.TableDefId)
            ?? throw new Errors.NotFoundException(
                "ECR-TMPL-0404", $"Таблиці {instance.TableDefId} немає в структурі версії {instance.TemplateVersionId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.table",
                    ["tableDefId"] = instance.TableDefId.ToString(CultureInfo.InvariantCulture),
                    ["versionId"] = instance.TemplateVersionId.ToString(CultureInfo.InvariantCulture),
                });

        // Ключ, який не може бути ключем рядка, — це «такого рядка немає» (404), а не 400: форма ключа
        // не розрізняє «видалено» від «ніколи не було».
        if (!RowKey.TryCreate(rowKey, out var key))
        {
            throw RowNotFound();
        }

        // Права — ДО транзакції й блокування аркуша (P3, як у створенні).
        await RequireDeleteAllowedAsync(profile, tableInstanceId, key, ct).ConfigureAwait(false);

        string? jobId = null;
        var enlist = jobs.EnlistsInCallerTransaction;
        IReadOnlyList<RecalculationSeed> seeds = [];

        await uow.ExecuteInTransactionAsync(
            async innerCt =>
            {
                seeds = await DeleteUnderLockAsync(instance, table, key, profile, innerCt).ConfigureAwait(false);

                // ⛔ MI-02 (в): черга в базі — постановка ВСЕРЕДИНІ транзакції останнім оператором; Quartz — після коміту.
                if (enlist && seeds.Count > 0)
                {
                    jobId = await EnqueueRecalculationAsync(instance, seeds, profile.UserId, innerCt).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);

        if (!enlist && seeds.Count > 0)
        {
            jobId = await EnqueueRecalculationAsync(instance, seeds, profile.UserId, ct).ConfigureAwait(false);
        }

        return new DeleteRowResult(jobId);
    }

    /// <summary>Рішення служби доступу — те саме питання, що й на створення рядка.</summary>
    private async Task RequireDeleteAllowedAsync(
        AccessProfile profile, long tableInstanceId, RowKey key, CancellationToken ct)
    {
        var decisions = await access
            .CanCreateRowsAsync(profile, tableInstanceId, [key.Value], ct)
            .ConfigureAwait(false);

        // ⛔ Немає рішення — ВІДМОВА.
        var decision = decisions.TryGetValue(key.Value, out var found)
            ? found.Row
            : EditDecision.Deny(EditDenyReason.NoGrant, "Рішення про доступ на видалення рядка не отримано.");

        if (!decision.IsAllowed)
        {
            throw DeleteDenied(decision.Reason);
        }
    }

    private static Errors.AccessDeniedException DeleteDenied(EditDenyReason reason)
        => new(
            "ECR-ACCS-0403",
            $"Рядок із цієї таблиці видалити не можна: {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.deleteRowDenied",
                ["reason"] = reason.ToString(),
            });

    // ⚠ Без ключа рядка й коду таблиці в тексті та деталях (сторож `DenyLeakGuardTests`, T1-01): відмова
    // «немає / уже видалено» мусить бути БАЙТ-в-байт однаковою, а клієнт і так знає, який рядок просив.
    private static Errors.NotFoundException RowNotFound()
        => new(
            "ECR-ROW-0404",
            "Рядок у цій таблиці не знайдено.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ROW-0404.rowNotFound",
            });

    private static Errors.BusinessRuleException NotDeletable(string messageKey)
        => new(
            "ECR-ROW-0409",
            "Цей рядок видалити не можна.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
            });
    /// <summary>Перевірки й видалення під блокуванням аркуша; кличеться лише всередині транзакції.</summary>
    /// <returns>Насіння каскаду перерахунку (порожнє — перераховувати нічого).</returns>
    private async Task<IReadOnlyList<RecalculationSeed>> DeleteUnderLockAsync(
        TableInstanceRef instance, TableDef table, RowKey key, AccessProfile profile, CancellationToken ct)
    {
        // ⛔ L6-02: структура документа — першою, до блокування аркуша (як у створенні).
        DocumentStructure.EnsureUnchanged(
            await sheetGate.EnterStructureAsync(instance.DocumentId, exclusive: false, ct).ConfigureAwait(false),
            instance.TemplateVersionId,
            instance.DocumentId);

        var periodKey = new PeriodKey(instance.PeriodKey);

        await RowWriteLock
            .EnterAsync(sheetGate, instance.DocumentId, table.SheetDefId, periodKey, ct)
            .ConfigureAwait(false);
        var status = await sheetGate
            .EnterEditAsync(instance.DocumentId, table.SheetDefId, periodKey, ct)
            .ConfigureAwait(false);

        var stateReason = status switch
        {
            DocumentStatus.Submitted => (EditDenyReason?)EditDenyReason.DocumentSubmitted,
            DocumentStatus.Approved => EditDenyReason.DocumentApproved,
            _ => null,
        };

        if (stateReason is { } denied)
        {
            throw DeleteDenied(denied);
        }

        // Живий рядок: «немає» і «уже видалено» — одна відповідь (нерозкриття).
        var row = await rowStore
            .FindLiveRowAsync(instance.TableInstanceId, periodKey, key.Value, ct)
            .ConfigureAwait(false)
            ?? throw RowNotFound();

        // Рядки шаблону (і Fixed-таблиці цілком) видаляти не можна: на них посилаються формули з діапазонами.
        if (row.RowDefId is not null || !table.AllowsDynamicRows)
        {
            throw NotDeletable("err.ECR-ROW-0409.templateRowNotDeletable");
        }

        if (row.IsSourceManaged)
        {
            throw NotDeletable("err.ECR-ROW-0409.sourceRowNotDeletable");
        }

        var columns = table.Columns.Where(c => !c.IsDeleted).ToList();
        var addresses = columns.ConvertAll(c => new CellAddress(periodKey, row.Id, c.Id));
        var values = addresses.Count == 0
            ? new Dictionary<CellAddress, CellValueData>()
            : (await cells.ReadCellsAsync(addresses, ct).ConfigureAwait(false)).ToDictionary(p => p.Key, p => p.Value);

        // ⛔ Склад ДО видалення — у тій самій транзакції, що й видалення.
        var now = clock.UtcNow;
        var oldJson = JsonSerializer.Serialize(
            new
            {
                rowKey = key.Value,
                rowId = row.Id,
                row.Ordinal,
                documentId = instance.DocumentId,
                tableInstanceId = instance.TableInstanceId,
                tableCode = table.Code,
                periodKey = instance.PeriodKey,
                cells = columns
                    .Select(c => (Column: c, Value: values.GetValueOrDefault(new CellAddress(periodKey, row.Id, c.Id))))
                    .Where(x => x.Value is not null && !x.Value.IsEmpty)
                    .Select(x => new
                    {
                        column = x.Column.Code,
                        columnDefId = x.Column.Id,
                        text = x.Value!.ValueString,
                        number = x.Value.ValueNumeric,
                        date = x.Value.ValueDate,
                        flag = x.Value.ValueBool,
                        registryEntryId = x.Value.ValueRegistryEntryId,
                        unitId = x.Value.ValueUnitId,
                        calculated = x.Value.IsCalculated,
                    })
                    .ToList(),
            },
            JsonOptions);

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                now, instance.TemplateVersionId, AuditEntityType,
                // `EntityId` — int, а `TableRow.Id` — long: повний Id лишається в `OldJson.rowId`.
                (int)Math.Min(row.Id, int.MaxValue),
                ChangeClass.Guarded, AuditOperation,
                oldJson, NewJson: null, ChangeReason: null,
                ChangedByUserId: profile.UserId, CorrelationId: null),
            ct).ConfigureAwait(false);

        var deleted = await rowStore.SoftDeleteRowAsync(row.Id, periodKey, ct).ConfigureAwait(false);
        if (!deleted)
        {
            // Гонитва, якої блокування аркуша не пускає; усе одно — чесна відповідь про живий рядок.
            throw RowNotFound();
        }

        // ⛔ Видалення рядка — зміна документа (`H-23d`), як і додавання.
        await documents.TouchAsync(instance.DocumentId, profile.UserId, now, ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return [.. columns.Select(c => new RecalculationSeed(row.Id, c.Id))];
    }

    /// <summary>Та сама задача, що й після PATCH: масив <c>cells</c> зливається з тією, що чекає.</summary>
    private Task<string> EnqueueRecalculationAsync(
        TableInstanceRef instance, IReadOnlyList<RecalculationSeed> seeds, int editorUserId, CancellationToken ct)
        => jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
            FormulaRecalculationTarget.Of(instance.DocumentId, instance.PeriodKey, editorUserId),
            new
            {
                DocumentId = instance.DocumentId,
                TableInstanceId = instance.TableInstanceId,
                PeriodKey = instance.PeriodKey,
                Cells = seeds,
            },
            ct,
            createdByUserId: editorUserId);
}
