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
    ISheetEditGate sheetGate,

    // ⛔ EA-B / P1-4: повторна перевірка передачі зрізу під блокуванням аркуша (як у `CreateRowHandler`).
    // Контейнер передає завжди; `null` — лише тестові фабрики, і тоді документ із передачами
    // відхиляється (fail-closed).
    IEditAssignmentStore? assignments = null)
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

        // ⛔ S-4: ВІДСУТНІЙ екземпляр дає те саме тіло 404, що й чужий/недоступний (без оракула існування).
        TableInstanceRef instance;
        try
        {
            instance = await rowStore.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
        }
        catch (Errors.NotFoundException)
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        // ⛔ Належність екземпляра документові з МАРШРУТУ — як у `CreateRowHandler`.
        // ⛔ P3 (оракул існування): «екземпляр чужого документа» і «документ недоступний» — ОДНА відповідь
        // (404 `err.ECR-DOC-0404.document`), інакше за відмінністю тексту можна вгадувати чужі `TableInstanceId`.
        var read = instance.DocumentId == documentId
            ? await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false)
            : EditDecision.Deny(EditDenyReason.NoGrant);
        if (!read.IsAllowed)
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        // ⛔ D-6: схована таблиця для читача НЕ ІСНУЄ — 404 ДО відмови про рядок.
        try
        {
            await DocumentVisibility
                .RequireTableVisibleAsync(
                    access, profile, documentId, instance.TableInstanceId, instance.TableDefId, instance.PeriodKey, ct)
                .ConfigureAwait(false);
        }
        catch (Errors.NotFoundException)
        {
            // S-4: схована таблиця — те саме тіло, що й «документа немає».
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

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

        // Права на РЯДОК — ДО транзакції й блокування аркуша (P3, як у створенні); права на значення
        // колонок рядка — під блокуванням, коли відомий склад (P1-2).
        var rowDecision = await RequireDeleteAllowedAsync(profile, tableInstanceId, key, ct).ConfigureAwait(false);
        var sheetCode = snapshot.Sheets.FirstOrDefault(s => s.Id == table.SheetDefId)?.Code;

        string? jobId = null;
        var enlist = jobs.EnlistsInCallerTransaction;
        IReadOnlyList<RecalculationSeed> seeds = [];

        await uow.ExecuteInTransactionAsync(
            async innerCt =>
            {
                seeds = await DeleteUnderLockAsync(
                        instance, table, key, profile, rowDecision.AssignmentsPresent, sheetCode, innerCt)
                    .ConfigureAwait(false);

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
    private async Task<EditDecision> RequireDeleteAllowedAsync(
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

        return decision;
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
    /// <summary>
    /// Відмова через ЗНАЧЕННЯ комірок рядка (колонковий грант, правила періоду). ⚠ Без `{reason}` у тексті й без
    /// назви колонки: reason фіксований (`BusinessRule`) — різні причини розрізняли б приховану колонку.
    /// </summary>
    private static Errors.AccessDeniedException DeleteCellsDenied(bool window)
        => new(
            "ECR-ACCS-0403",
            window
                ? "Рядок видалити не можна: значення в ньому поза вікном доступу до періоду."
                : "Рядок видалити не можна: у ньому є значення, які ви не можете стерти.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = window
                    ? "err.ECR-ACCS-0403.deleteRowOutOfWindow"
                    : "err.ECR-ACCS-0403.deleteRowValuesDenied",
                ["reason"] = nameof(EditDenyReason.BusinessRule),
            });

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
        TableInstanceRef instance, TableDef table, RowKey key, AccessProfile profile, bool assignmentsPresent,
        string? sheetCode, CancellationToken ct)
    {
        // ⛔ P3: очікування блокувань обмежене (409 ECR-DOC-4091 замість висіння до CommandTimeout) — до всіх блокувань.
        await cells.LimitLockWaitAsync(ct).ConfigureAwait(false);

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

        // ⛔ P1-4 (EA-B): передача зрізу повторно — під блокуванням аркуша: «Заповнено»/«Валідовано»/строк/
        // ексклюзивність, що з'явилися між рішенням і комітом, відхиляють видалення (рядковий рівень, без колонки).
        await EnsureAssignmentsHoldAsync(instance, table, profile, assignmentsPresent, sheetCode, ct)
            .ConfigureAwait(false);

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

        // ⛔ P1-3: рядок від події джерела або з останнім значенням від інтеграції/імпорту/збору видаляти
        // «рукою» не можна (за замовчуванням): людина стерла б машинні дані, які «відродить» наступний прогін
        // (а ключ видаленого рядка зайнятий). Відмова та сама, що й для `SourceEventLink`.
        if (row.IsSourceManaged
            || await rowStore.HasMachineOriginAsync(instance.DocumentId, periodKey, row.Id, ct).ConfigureAwait(false))
        {
            throw NotDeletable("err.ECR-ROW-0409.sourceRowNotDeletable");
        }

        var columns = table.Columns.Where(c => !c.IsDeleted).ToList();
        var addresses = columns.ConvertAll(c => new CellAddress(periodKey, row.Id, c.Id));
        var values = addresses.Count == 0
            ? new Dictionary<CellAddress, CellValueData>()
            : (await cells.ReadCellsAsync(addresses, ct).ConfigureAwait(false)).ToDictionary(p => p.Key, p => p.Value);

        // ⛔ P1-2 (безпека): право на РЯДОК (`CanCreateRowsAsync` ≈ `CanEdit` із колонкою 0) не бачить грантів
        // на колонки й правил доступу до періоду. Видалення стирає значення КОЖНОЇ непорожньої комірки, тож для
        // кожної питаємо рішення «стерти» (`CanClearCellsAsync`): те саме, що для правки, але БЕЗ ознак колонки
        // «обчислювана»/«лише для читання» (S-2: вони йдуть раніше за грант і ховали б явний Deny).
        // Будь-яка заборона — а також дозвіл «за підтвердженням» і «з позначкою» (S-3) — блокує видалення.
        // ⚠ Видалення СТРОГІШЕ за PATCH: PatchCellsHandler пропускає `Warn` («з позначкою») без підтвердження,
        // а AllowWithConfirmation пише лише з підтвердженням оператора; у DELETE каналу підтвердження немає,
        // тож обидва випадки — відмова. Це прийнятно: стерти значення поза вікном — гірше, ніж виправити його.
        // ⚠ S-4 (нерозкриття): reason ОДИН для всіх заборонених КОМІРОК (`BusinessRule`) і без назви колонки:
        // різні причини розрізняли б «у прихованій колонці є значення» і «у відкритій».
        // ⚠ Рядок лише з порожніми комірками цієї перевірки не проходить (нічого стирати) — вікно періоду для
        // нього перевіряє лише право на рядок (`CanEdit` із ColumnDefId = 0): прийнято свідомо (02-contracts).
        var filled = addresses.Where(a => values.TryGetValue(a, out var v) && v is { IsEmpty: false }).ToList();
        if (filled.Count > 0)
        {
            var decisions = await access
                .CanClearCellsAsync(profile, instance.TableInstanceId, periodKey, filled, ct)
                .ConfigureAwait(false);

            foreach (var address in filled)
            {
                if (!decisions.TryGetValue(address, out var decision))
                {
                    throw DeleteCellsDenied(window: false);
                }

                // Поза вікном доступу: заборона, або «дозволено за підтвердженням»/«з позначкою».
                if (decision.RequiresConfirmation
                    || decision.OutOfWindowMark
                    || decision.Reason is EditDenyReason.OutOfAccessWindow or EditDenyReason.OutsidePermitWindow)
                {
                    throw DeleteCellsDenied(window: true);
                }

                if (!decision.IsAllowed)
                {
                    throw DeleteCellsDenied(window: false);
                }
            }
        }
        // ⛔ Склад ДО видалення — у тій самій транзакції, що й видалення. P2-1: ЛИШЕ склад (колонка, чи
        // обчислена), БЕЗ значень: `GetStructureChangesHandler` читає за `Security.ViewAudit` без звуження
        // за колонками, а значення лишаються в `aud.CellChange` під фільтром видимості (S6).
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
                    .Where(x => x.Value is not null)
                    .Select(x => new
                    {
                        column = x.Column.Code,
                        columnDefId = x.Column.Id,
                        isEmpty = x.Value!.IsEmpty,
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

    /// <summary>Передача зрізу під блокуванням аркуша (дзеркало `CreateRowHandler.EnsureAssignmentsHoldAsync`).</summary>
    private async Task EnsureAssignmentsHoldAsync(
        TableInstanceRef instance, TableDef table, AccessProfile profile, bool assignmentsPresent,
        string? sheetCode, CancellationToken ct)
    {
        if (profile.IsSimulation || profile.IsIntegrationWriter)
        {
            return;
        }

        // ⛔ S-5: прапорець «передачі є» з рішення обчислено ДО блокування, тож під замком передачі читаються
        // ЗАВЖДИ, коли є сховище (видалення рідкісне — один дешевий запит), а не за застарілим прапорцем.
        if (assignments is null)
        {
            if (!assignmentsPresent)
            {
                return;
            }

            throw new InvalidOperationException(
                "Передачі зрізу діють, але сховища для повторної перевірки під блокуванням немає.");
        }

        var gate = await assignments
            .ReadFactsForGateAsync(instance.DocumentId, profile.UserId, ct)
            .ConfigureAwait(false);

        // Рівень рядка: без колонки й місяця, без підйому (D5) — лише заборони передачі.
        var facts = EditAssignmentGuard.Evaluate(
            profile.UserId,
            gate.OwnerUserId,
            isIntegrationWriter: false,
            gate.Facts,
            new EditSliceFacts(
                sheetCode, table.Code, new PeriodKey(instance.PeriodKey), ColumnIsMonth: false,
                ColumnMonthNumber: null, IsRowLevel: true),
            clock.UtcNow);

        if (facts.Lock != EditDenyReason.None)
        {
            throw DeleteDenied(facts.Lock);
        }
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
