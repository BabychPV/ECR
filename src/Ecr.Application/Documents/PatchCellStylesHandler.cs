// src/Ecr.Application/Documents/PatchCellStylesHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Користувацьке оформлення комірок (<c>PATCH …/tables/{id}/cell-styles</c>): заливка, колір
/// тексту, жирність — «змінювати колір комірки як в Excel» (CELL-STYLE L3b).
/// </summary>
/// <remarks>
/// ⚠ Не дані. Значення, <c>RowVersion</c>, перерахунок і <c>aud.CellChange</c> не зачіпаються;
/// слід лишається в <c>aud.SecurityEvent</c> (<see cref="SetEventType"/> / <see cref="ResetEventType"/>).
///
/// ⛔ Права — ТІ САМІ, що на запис комірки (<see cref="IAccessDecisionService.CanClearCellsAsync"/> —
/// те саме рішення, що «стерти значення»: грант <c>Write</c>, період, архів, стан аркуша, передача
/// зрізу). Ознаки колонки «обчислювана»/«лише читання» не заважають: колір — не значення, а
/// майже весь Land — readOnly/calculated. Рядок «лише читання» (<c>RowIsReadOnly</c>) і далі
/// відмовляє. Колір не можна змінити в поданому/затвердженому аркуші й закритому періоді.
///
/// ⛔ Нерозкриття: рядок чи колонка, яких немає в зрізі АБО яких читач не бачить (прихована
/// колонка, заборона S6), — одна відмова <c>unknownCell</c> без назви причини.
///
/// ⛔ Під транзакцією, у порядку блокувань запису комірок: структура документа → стан аркуша →
/// передачі зрізу (<see cref="IEditAssignmentStore.ReadFactsForGateAsync"/>, завжди, fail-closed).
/// </remarks>
public sealed class PatchCellStylesHandler(
    IRowStore rowStore,
    ICellStyleStore styles,
    IMetadataCache metadata,
    IAccessDecisionService access,
    ISheetEditGate sheetGate,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,

    // ⛔ EA-B: передачі зрізу читаються ПІД блокуванням аркуша ЗАВЖДИ (fail-closed: сховище обов'язкове).
    IEditAssignmentStore assignments)
{
    /// <summary>Тип події журналу безпеки: оформлення задано/змінено.</summary>
    public const string SetEventType = "CellStyleSet";

    /// <summary>Тип події журналу безпеки: оформлення знято (<c>reset</c>).</summary>
    public const string ResetEventType = "CellStyleReset";

    /// <summary>Скільки комірок (з переходом «було → стало») кладеться в подробиці події.</summary>
    private const int AuditCellsCap = 100;

    /// <summary>Застосовує зміни оформлення до комірок екземпляра таблиці.</summary>
    /// <param name="documentId">Документ з маршруту.</param>
    /// <param name="instance">Екземпляр таблиці, уже розв'язаний контролером (нерозкриття).</param>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CELL-4224</c>; <c>ECR-PRD-0422</c> — період тіла ≠ періоду екземпляра.</exception>
    /// <exception cref="AccessDeniedException"><c>ECR-ACCS-0403</c> — хоч одна комірка недоступна.</exception>
    public async Task<PatchCellStylesResponse> HandleAsync(
        long documentId, TableInstanceRef instance, PatchCellStylesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(request);

        // Форма запиту — до будь-якого читання бази (контролер перевіряє те саме раніше).
        request.EnsureValid();

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         ErrorCodes.Unauthorized,
                         "Анонімний запит не може змінювати оформлення комірок.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        // ⛔ Невидимий документ — 404, а не 403 (та сама відповідь, що на неіснуючий).
        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw DocumentVisibility.NotFound(documentId);
        }

        await DocumentVisibility
            .RequireTableVisibleAsync(
                access, profile, documentId, instance.TableInstanceId, instance.TableDefId, instance.PeriodKey, ct)
            .ConfigureAwait(false);

        if (instance.PeriodKey != request.PeriodKey)
        {
            throw new BusinessRuleException(
                ErrorCodes.PeriodOutOfProject,
                $"Період {request.PeriodKey} не збігається з періодом {instance.PeriodKey} екземпляра таблиці {instance.TableInstanceId}.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-PRD-0422.periodMismatch",
                    ["periodKey"] = request.PeriodKey.ToString(CultureInfo.InvariantCulture),
                    ["expectedPeriodKey"] = instance.PeriodKey.ToString(CultureInfo.InvariantCulture),
                    ["tableInstanceId"] = instance.TableInstanceId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var items = request.Items ?? [];
        if (items.Count == 0)
        {
            return new PatchCellStylesResponse(0, 0);
        }

        var period = new PeriodKey(instance.PeriodKey);
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var table = snapshot.Sheets
                        .SelectMany(sh => sh.Tables)
                        .FirstOrDefault(t => t.Id == instance.TableDefId)
                    ?? throw new NotFoundException(
                        "ECR-TMPL-0404",
                        $"Таблиці {instance.TableDefId} немає в структурі версії {instance.TemplateVersionId}.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-TMPL-0404.table",
                            ["tableDefId"] = instance.TableDefId.ToString(CultureInfo.InvariantCulture),
                            ["versionId"] = instance.TemplateVersionId.ToString(CultureInfo.InvariantCulture),
                        });

        // ⛔ S6: колонка, яку читач не бачить (прихована чи під забороною), для нього не існує.
        var readable = (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(period);
        var columnIdByCode = table.Columns
            .Where(c => !c.IsDeleted && !c.IsHidden && readable.CanReadColumn(c.Id))
            .ToDictionary(c => c.Code.ToString()!, c => c.Id, StringComparer.Ordinal);

        var rowIdByKey = await rowStore.GetRowIdsAsync(instance.TableInstanceId, period, ct).ConfigureAwait(false);

        var targets = new List<(CellStyleChange Item, CellAddress Address)>(items.Count);
        var unknown = 0;
        foreach (var item in items)
        {
            if (rowIdByKey.TryGetValue(item.RowKey, out var rowId)
                && columnIdByCode.TryGetValue(item.ColumnCode, out var columnId))
            {
                targets.Add((item, new CellAddress(period, rowId, columnId)));
            }
            else
            {
                unknown++;
            }
        }

        if (unknown > 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.CellStyleInvalid,
                $"Комірок поза зрізом: {unknown}.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-CELL-4224.unknownCell",
                    ["cellCount"] = unknown.ToString(CultureInfo.InvariantCulture),
                });
        }

        var liftedColumns = await EnsureAllowedAsync(profile, instance, period, targets, ct).ConfigureAwait(false);
        var sheetCode = snapshot.Sheets.FirstOrDefault(s => s.Id == table.SheetDefId)?.Code;

        var now = clock.UtcNow;
        var applied = 0;
        var reset = 0;
        var changes = new List<object>();
        var truncated = false;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // Лічильники скидаються: тіло може виконатися повторно (стратегія повторів EF).
            applied = 0;
            reset = 0;
            changes.Clear();
            truncated = false;

            // ⛔ L6-02: структура документа — першою; перенос версії тримає її винятково.
            DocumentStructure.EnsureUnchanged(
                await sheetGate.EnterStructureAsync(documentId, exclusive: false, innerCt).ConfigureAwait(false),
                instance.TemplateVersionId,
                documentId);

            // Стан аркуша — ПІД блокуванням: подання, що зафіксувалося між рішенням і тут, відхиляє зміну.
            var status = await sheetGate
                .EnterEditAsync(documentId, table.SheetDefId, period, innerCt)
                .ConfigureAwait(false);
            EnsureSheetEditable(status, targets.Count);

            // ⛔ EA-B: передача зрізу ПОВТОРНО — під блокуванням аркуша, і порядок блокувань той самий, що в
            // CreateRow/DeleteRow/PatchCells: структура → аркуш → передачі (U) → виконавці. Рішення про права
            // прийняте поза транзакцією, а передачу можуть відкликати, позначити «Заповнено»/«Валідовано»
            // чи вона може спливти між ним і комітом.
            await EnsureAssignmentsHoldAsync(profile, instance, table, sheetCode, targets, liftedColumns, innerCt)
                .ConfigureAwait(false);

            var existing = (await styles
                    .LoadForUpdateAsync(period, [.. targets.Select(t => t.Address.TableRowId).Distinct()], innerCt)
                    .ConfigureAwait(false))
                .ToDictionary(s => (s.TableRowId, s.ColumnDefId));

            foreach (var (item, address) in targets)
            {
                existing.TryGetValue((address.TableRowId, address.ColumnDefId), out var style);
                var before = Snapshot(style);

                if (request.Reset)
                {
                    if (style is null)
                    {
                        continue;
                    }

                    styles.Remove(style);
                    reset++;
                    Record(item, before, null);
                    continue;
                }

                var isNew = style is null;
                style ??= new CellStyle(address, userId, now);

                if (!style.Apply(item.BackgroundHex, item.ForegroundHex, item.IsBold, userId, now))
                {
                    continue;
                }

                if (style.IsBlank)
                {
                    // Усе зняли полями: порожній рядок не тримаємо (як «Скинути»).
                    if (!isNew)
                    {
                        styles.Remove(style);
                        reset++;
                        Record(item, before, null);
                    }

                    continue;
                }

                if (isNew)
                {
                    styles.Add(style);
                }

                applied++;
                Record(item, before, Snapshot(style));
            }

            if (applied == 0 && reset == 0)
            {
                return;
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now,
                    request.Reset ? ResetEventType : SetEventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        documentId,
                        tableInstanceId = instance.TableInstanceId,
                        periodKey = instance.PeriodKey,
                        applied,
                        reset,
                        cells = changes,
                        truncated,
                    }),
                    userId,
                    currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return new PatchCellStylesResponse(applied, reset);

        void Record(CellStyleChange item, object? was, object? became)
        {
            if (changes.Count >= AuditCellsCap)
            {
                truncated = true;
                return;
            }

            changes.Add(new { rowKey = item.RowKey, columnCode = item.ColumnCode, was, now = became });
        }
    }

    private static object? Snapshot(CellStyle? style)
        => style is null
            ? null
            : new { background = style.BackgroundHex, foreground = style.ForegroundHex, bold = style.IsBold };

    /// <summary>Рішення про права на кожну комірку; немає рішення — відмова.</summary>
    private async Task<HashSet<int>> EnsureAllowedAsync(
        AccessProfile profile,
        TableInstanceRef instance,
        PeriodKey period,
        List<(CellStyleChange Item, CellAddress Address)> targets,
        CancellationToken ct)
    {
        var decisions = await access
            .CanClearCellsAsync(
                profile, instance.TableInstanceId, period, [.. targets.Select(t => t.Address)], ct)
            .ConfigureAwait(false);

        EditDecision? first = null;
        var denied = 0;
        var lifted = new HashSet<int>();
        foreach (var (_, address) in targets)
        {
            // ⛔ Немає рішення — ВІДМОВА (як у `PatchCellsHandler.CheckAccess`).
            if (!decisions.TryGetValue(address, out var decision))
            {
                decision = EditDecision.Deny(EditDenyReason.NoGrant);
            }

            if (decision.IsAllowed)
            {
                if (decision.ViaAssignment)
                {
                    lifted.Add(address.ColumnDefId);
                }

                continue;
            }

            denied++;
            first ??= decision;
        }

        if (denied == 0)
        {
            return lifted;
        }

        throw DeniedCells(denied, first!.Value.Reason);
    }

    /// <summary>
    /// EA-B: повторна перевірка передачі зрізу під блокуванням аркуша. Відмова — та сама форма
    /// <c>ECR-ACCS-0403 deniedCells</c>, нічого не записано.
    /// </summary>
    /// <remarks>
    /// ⚠ Дві умови відмови (як у запису комірок): з'явилася заборона передачі (Filled/Validated/Expired/
    /// LockedByOwner) АБО колонку дозволила САМЕ передача, а тепер вона не піднімає (відкликана).
    /// Читання фактів — завжди (не за прапорцем зі старого рішення), крім симуляції й системного писаря.
    /// </remarks>
    private async Task EnsureAssignmentsHoldAsync(
        AccessProfile profile,
        TableInstanceRef instance,
        Ecr.Domain.Entities.Configuration.TableDef table,
        string? sheetCode,
        List<(CellStyleChange Item, CellAddress Address)> targets,
        HashSet<int> liftedColumns,
        CancellationToken ct)
    {
        if (profile.IsSimulation || profile.IsIntegrationWriter)
        {
            return;
        }

        var gate = await assignments
            .ReadFactsForGateAsync(instance.DocumentId, profile.UserId, ct)
            .ConfigureAwait(false);

        if (gate.Facts.Count == 0 && liftedColumns.Count == 0)
        {
            return;
        }

        var utcNow = clock.UtcNow;
        var columnsById = table.Columns.ToDictionary(c => c.Id);

        foreach (var columnId in targets.Select(t => t.Address.ColumnDefId).Distinct())
        {
            if (!columnsById.TryGetValue(columnId, out var column))
            {
                continue;
            }

            var facts = EditAssignmentGuard.Evaluate(
                profile.UserId,
                gate.OwnerUserId,
                isIntegrationWriter: false,
                gate.Facts,
                new EditSliceFacts(
                    sheetCode, table.Code, new PeriodKey(instance.PeriodKey), column.IsMonthColumn, column.MonthNumber),
                utcNow);

            if (facts.Lock != EditDenyReason.None)
            {
                throw DeniedCells(targets.Count, facts.Lock);
            }

            if (liftedColumns.Contains(columnId) && !facts.DelegatedWrite)
            {
                throw DeniedCells(targets.Count, EditDenyReason.InsufficientGrantLevel);
            }
        }
    }

    private static void EnsureSheetEditable(DocumentStatus status, int cells)
    {
        var reason = status switch
        {
            DocumentStatus.Submitted => (EditDenyReason?)EditDenyReason.DocumentSubmitted,
            DocumentStatus.Approved => EditDenyReason.DocumentApproved,
            _ => null,
        };

        if (reason is { } denied)
        {
            throw DeniedCells(cells, denied);
        }
    }

    /// <summary>Та сама відмова й форма, що й у <c>PATCH …/cells</c>: для людини це та сама причина.</summary>
    private static AccessDeniedException DeniedCells(int count, EditDenyReason reason)
        => new(
            "ECR-ACCS-0403",
            $"Заборонених комірок у запиті: {count}. Причина першої: {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.deniedCells",
                ["deniedCount"] = count.ToString(CultureInfo.InvariantCulture),
                ["reason"] = reason.ToString(),
            });
}
