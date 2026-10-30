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
/// тексту, жирність — «змінювати колір комірки як в Excel» (CELL-STYLE L3b), у тому числі ОБЛАСТЮ
/// «рядки × колонки» одним запитом (<see cref="PatchCellStylesRequest.Scope"/>).
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
/// колонка, заборона S6), — одна відмова <c>unknownCell</c> без назви причини. В області
/// <c>allColumns</c> такі колонки просто не входять у розгортання.
///
/// ⛔ Під транзакцією, у порядку блокувань запису комірок: структура документа → стан аркуша →
/// передачі зрізу (<see cref="IEditAssignmentStore.ReadFactsForGateAsync"/>, завжди, fail-closed).
///
/// ⚠ <c>items</c> і <c>scope</c>: на спільній комірці items сильніші. Відмова комірки з <c>items</c> —
/// повна (403, нічого не записано), як і раніше; комірки <c>scope</c> відмовляються ЧАСТКОВО —
/// застосовується дозволена частина, решта — у <c>deniedCells</c>. Нуль дозволених — 403.
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

    /// <summary>Скільки ключів рядків області кладеться в подію (решта — лише кількість і <c>rowKeysTruncated</c>).</summary>
    private const int AuditRowKeysCap = 100;

    /// <summary>Скільки рядків передається в одне рішення про права й одне читання оформлення (ліміт параметрів SQL).</summary>
    private const int RowChunk = 1000;

    /// <summary>
    /// Поріг змін між проміжними <c>SaveChanges</c> (усе в ОДНІЙ транзакції). Перевіряється ПІСЛЯ кожної порції
    /// рядків (<see cref="RowChunk"/>), а не після кожної тисячі змін: на 1000 рядків × N колонок збереження
    /// йде за порцію, тож змін за раз може бути й більше за 1000 (до рядків порції × колонок).
    /// </summary>
    private const int SaveChunk = 1000;

    /// <summary>Скільки причин відмови повертається клієнту.</summary>
    private const int DeniedReasonsCap = 10;

    /// <summary>Застосовує зміни оформлення до комірок екземпляра таблиці.</summary>
    /// <param name="documentId">Документ з маршруту.</param>
    /// <param name="instance">Екземпляр таблиці, уже розв'язаний контролером (нерозкриття).</param>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CELL-4224</c>; <c>ECR-PRD-0422</c> — період тіла ≠ періоду екземпляра.</exception>
    /// <exception cref="AccessDeniedException"><c>ECR-ACCS-0403</c> — комірка з items недоступна, або дозволених комірок немає.</exception>
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
        var scope = request.Scope;
        if (items.Count == 0 && scope is null)
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

        // Розгортання: спершу область, потім items — items ПЕРЕЗАПИСУЮТЬ зміну області на спільній комірці.
        var targets = new Dictionary<CellAddress, Target>();
        var unknown = 0;

        if (scope is not null)
        {
            ExpandScope(scope, period, rowIdByKey, columnIdByCode, targets, ref unknown);
        }

        foreach (var item in items)
        {
            if (rowIdByKey.TryGetValue(item.RowKey, out var rowId)
                && columnIdByCode.TryGetValue(item.ColumnCode, out var columnId))
            {
                targets[new CellAddress(period, rowId, columnId)] = new Target(
                    item.RowKey, item.ColumnCode, item.BackgroundHex, item.ForegroundHex, item.IsBold,
                    request.Reset, FromItem: true);
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

        if (targets.Count > PatchCellStylesRequest.ScopeMaxCells)
        {
            throw TooManyCells(targets.Count);
        }

        if (targets.Count == 0)
        {
            return new PatchCellStylesResponse(0, 0);
        }

        var decided = await DecideAsync(profile, instance, period, targets, ct).ConfigureAwait(false);
        var sheetCode = snapshot.Sheets.FirstOrDefault(s => s.Id == table.SheetDefId)?.Code;

        var now = clock.UtcNow;
        var applied = 0;
        var reset = 0;
        var changes = new List<object>();
        var truncated = false;
        var deniedTally = new Dictionary<EditDenyReason, int>();
        var deniedTotal = 0;
        var skipped = 0;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // Лічильники скидаються: тіло може виконатися повторно (стратегія повторів EF).
            applied = 0;
            reset = 0;
            skipped = 0;
            changes.Clear();
            truncated = false;
            deniedTally.Clear();
            foreach (var (reason, count) in decided.ScopeDenied)
            {
                deniedTally[reason] = count;
            }

            deniedTotal = decided.ScopeDenied.Values.Sum();

            // ⛔ L6-02: структура документа — першою; перенос версії тримає її винятково.
            DocumentStructure.EnsureUnchanged(
                await sheetGate.EnterStructureForPeriodAsync(documentId, exclusive: false, instance.PeriodKey, innerCt).ConfigureAwait(false),
                instance.TemplateVersionId,
                documentId);

            // Стан аркуша — ПІД блокуванням: подання, що зафіксувалося між рішенням і тут, відхиляє зміну.
            // ⚠ Це стан усього аркуша, а не комірки: для області — теж повна відмова (дозволених 0).
            var status = await sheetGate
                .EnterEditAsync(documentId, table.SheetDefId, period, innerCt)
                .ConfigureAwait(false);
            EnsureSheetEditable(status, targets.Count + decided.ScopeDenied.Values.Sum());

            // ⛔ EA-B: передача зрізу ПОВТОРНО — під блокуванням аркуша, і порядок блокувань той самий, що в
            // CreateRow/DeleteRow/PatchCells: структура → аркуш → передачі (U) → виконавці. Рішення про права
            // прийняте поза транзакцією, а передачу можуть відкликати, позначити «Заповнено»/«Валідовано»
            // чи вона може спливти між ним і комітом.
            var blocked = await FindBlockedColumnsAsync(
                    profile, instance, table, sheetCode, decided.Allowed, decided.LiftedColumns, innerCt)
                .ConfigureAwait(false);

            var effective = decided.Allowed;
            if (blocked.Count > 0)
            {
                // items у закритій колонці — повна відмова (як раніше); комірки області — часткова.
                var firstBlockedItem = decided.Allowed
                    .Where(t => t.Target.FromItem && blocked.ContainsKey(t.Address.ColumnDefId))
                    .Select(t => (EditDenyReason?)blocked[t.Address.ColumnDefId])
                    .FirstOrDefault();
                if (firstBlockedItem is { } reason)
                {
                    throw DeniedCells(targets.Count, reason);
                }

                effective = [];
                foreach (var entry in decided.Allowed)
                {
                    if (blocked.TryGetValue(entry.Address.ColumnDefId, out var lockReason))
                    {
                        deniedTally[lockReason] = deniedTally.GetValueOrDefault(lockReason) + 1;
                        deniedTotal++;
                    }
                    else
                    {
                        effective.Add(entry);
                    }
                }

                if (effective.Count == 0)
                {
                    throw DeniedCells(deniedTotal, deniedTally.OrderByDescending(p => p.Value).First().Key);
                }
            }

            skipped = effective.Count;
            var pending = 0;

            // Чанки по рядках: оформлення наявних комірок читається порціями, а не одним IN на всю область.
            foreach (var rowChunk in effective.GroupBy(e => e.Address.TableRowId).Chunk(RowChunk))
            {
                var existing = (await styles
                        .LoadForUpdateAsync(period, [.. rowChunk.Select(g => g.Key)], innerCt)
                        .ConfigureAwait(false))
                    .ToDictionary(s => (s.TableRowId, s.ColumnDefId));

                foreach (var (address, target) in rowChunk.SelectMany(g => g))
                {
                    existing.TryGetValue((address.TableRowId, address.ColumnDefId), out var style);
                    var before = Snapshot(style);

                    if (target.Reset)
                    {
                        if (style is null)
                        {
                            continue;
                        }

                        styles.Remove(style);
                        reset++;
                        Record(target, before, null);
                        pending++;
                    }
                    else
                    {
                        var isNew = style is null;
                        style ??= new CellStyle(address, userId, now);

                        if (!style.Apply(target.BackgroundHex, target.ForegroundHex, target.IsBold, userId, now))
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
                                Record(target, before, null);
                                pending++;
                            }

                            continue;
                        }

                        if (isNew)
                        {
                            styles.Add(style);
                        }

                        applied++;
                        Record(target, before, Snapshot(style));
                        pending++;
                    }
                }

                if (pending >= SaveChunk)
                {
                    await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
                    pending = 0;
                }
            }

            skipped -= applied + reset;
            if (applied == 0 && reset == 0)
            {
                // Нічого не змінилося (ідемпотентний повтор): запису й події немає.
                return;
            }

            if (pending > 0)
            {
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }

            var allReset = (items.Count == 0 || request.Reset) && (scope is null || scope.Reset);
            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now,
                    allReset ? ResetEventType : SetEventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        documentId,
                        tableInstanceId = instance.TableInstanceId,
                        periodKey = instance.PeriodKey,
                        applied,
                        reset,
                        denied = deniedTotal,
                        scope = scope is null ? null : DescribeScope(scope),
                        cells = changes,
                        truncated,
                    }),
                    userId,
                    currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        IReadOnlyDictionary<string, int>? byReason = deniedTotal == 0
            ? null
            : deniedTally
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key.ToString(), StringComparer.Ordinal)
                .Take(DeniedReasonsCap)
                .ToDictionary(p => p.Key.ToString(), p => p.Value, StringComparer.Ordinal);

        return new PatchCellStylesResponse(applied, reset, deniedTotal, byReason, skipped);

        void Record(Target target, object? was, object? became)
        {
            // ⚠ Перелік комірок — лише для items (≤ 2000): область не розписується по комірках.
            if (!target.FromItem)
            {
                return;
            }

            if (changes.Count >= AuditCellsCap)
            {
                truncated = true;
                return;
            }

            changes.Add(new { rowKey = target.RowKey, columnCode = target.ColumnCode, was, now = became });
        }
    }

    private static object? Snapshot(CellStyle? style)
        => style is null
            ? null
            : new { background = style.BackgroundHex, foreground = style.ForegroundHex, bold = style.IsBold };

    /// <summary>
    /// Опис області для журналу: кількості, задані кольори й ПЕРЕЛІК рядків/колонок області (рядки — до
    /// <see cref="AuditRowKeysCap"/> + <c>rowKeysTruncated</c>; колонки — усі: їх не більше, ніж у таблиці), без
    /// переліку самих комірок — інакше «яку частину таблиці перефарбовано» не розслідувати.
    /// </summary>
    private static object DescribeScope(CellStyleScope scope)
    {
        var rowKeys = scope.AllRows ? null : (scope.RowKeys ?? []).Distinct(StringComparer.Ordinal).ToList();
        var columnCodes = scope.AllColumns ? null : (scope.ColumnCodes ?? []).Distinct(StringComparer.Ordinal).ToList();

        return new
        {
            rows = scope.AllRows ? "all" : "list",
            rowKeysCount = rowKeys?.Count,
            rowKeys = rowKeys?.Take(AuditRowKeysCap).ToList(),
            rowKeysTruncated = rowKeys is not null && rowKeys.Count > AuditRowKeysCap,
            columns = scope.AllColumns ? "all" : "list",
            columnCodesCount = columnCodes?.Count,
            columnCodes,
            reset = scope.Reset,
            background = scope.Reset ? null : scope.BackgroundHex,
            foreground = scope.Reset ? null : scope.ForegroundHex,
            bold = scope.Reset ? null : scope.IsBold,
        };
    }

    /// <summary>
    /// Розгортає область у комірки: рядки × колонки, яких читач не бачить, не входять (<c>allColumns</c>)
    /// або дають <c>unknownCell</c> (явний код). Стеля — ДО побудови списку.
    /// </summary>
    private static void ExpandScope(
        CellStyleScope scope,
        PeriodKey period,
        IReadOnlyDictionary<string, long> rowIdByKey,
        Dictionary<string, int> columnIdByCode,
        Dictionary<CellAddress, Target> targets,
        ref int unknown)
    {
        var rows = new List<(string Key, long Id)>();
        if (scope.AllRows)
        {
            rows.AddRange(rowIdByKey.Select(p => (p.Key, p.Value)));
        }
        else
        {
            foreach (var key in (scope.RowKeys ?? []).Distinct(StringComparer.Ordinal))
            {
                if (rowIdByKey.TryGetValue(key, out var id))
                {
                    rows.Add((key, id));
                }
                else
                {
                    unknown++;
                }
            }
        }

        var columns = new List<(string Code, int Id)>();
        if (scope.AllColumns)
        {
            columns.AddRange(columnIdByCode.Select(p => (p.Key, p.Value)));
        }
        else
        {
            foreach (var code in (scope.ColumnCodes ?? []).Distinct(StringComparer.Ordinal))
            {
                if (columnIdByCode.TryGetValue(code, out var id))
                {
                    columns.Add((code, id));
                }
                else
                {
                    unknown++;
                }
            }
        }

        if (unknown > 0)
        {
            return;
        }

        var cells = (long)rows.Count * columns.Count;
        if (cells > PatchCellStylesRequest.ScopeMaxCells)
        {
            throw TooManyCells(cells);
        }

        foreach (var (rowKey, rowId) in rows)
        {
            foreach (var (code, columnId) in columns)
            {
                targets[new CellAddress(period, rowId, columnId)] = new Target(
                    rowKey, code, scope.BackgroundHex, scope.ForegroundHex, scope.IsBold, scope.Reset, FromItem: false);
            }
        }
    }

    /// <summary>
    /// Рішення про права на кожну комірку (порціями по рядках; немає рішення — відмова) і розділення на
    /// дозволені й відмовлені. Відмова комірки з items — одразу повна (403); області — рахується.
    /// </summary>
    private async Task<Decided> DecideAsync(
        AccessProfile profile,
        TableInstanceRef instance,
        PeriodKey period,
        Dictionary<CellAddress, Target> targets,
        CancellationToken ct)
    {
        var decisions = new Dictionary<CellAddress, EditDecision>(targets.Count);
        foreach (var rowChunk in targets.Keys.GroupBy(a => a.TableRowId).Chunk(RowChunk))
        {
            var part = await access
                .CanClearCellsAsync(
                    profile, instance.TableInstanceId, period, [.. rowChunk.SelectMany(g => g)], ct)
                .ConfigureAwait(false);

            foreach (var (address, decision) in part)
            {
                decisions[address] = decision;
            }
        }

        var allowed = new List<(CellAddress Address, Target Target)>(targets.Count);
        var lifted = new HashSet<int>();
        var scopeDenied = new Dictionary<EditDenyReason, int>();
        EditDecision? firstItemDenied = null;
        var itemDenied = 0;

        foreach (var (address, target) in targets)
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

                allowed.Add((address, target));
            }
            else if (target.FromItem)
            {
                itemDenied++;
                firstItemDenied ??= decision;
            }
            else
            {
                scopeDenied[decision.Reason] = scopeDenied.GetValueOrDefault(decision.Reason) + 1;
            }
        }

        if (itemDenied > 0)
        {
            throw DeniedCells(itemDenied, firstItemDenied!.Value.Reason);
        }

        if (allowed.Count == 0)
        {
            // Нуль дозволених: форма відмови items (403), причина — найчастіша.
            var top = scopeDenied.OrderByDescending(p => p.Value).First();
            throw DeniedCells(scopeDenied.Values.Sum(), top.Key);
        }

        return new Decided(allowed, lifted, scopeDenied);
    }

    /// <summary>
    /// EA-B: повторна перевірка передачі зрізу під блокуванням аркуша. Повертає колонки, у яких запис
    /// заборонений, з причиною; порожньо — усе дозволено.
    /// </summary>
    /// <remarks>
    /// ⚠ Дві умови відмови (як у запису комірок): з'явилася заборона передачі (Filled/Validated/Expired/
    /// LockedByOwner) АБО колонку дозволила САМЕ передача, а тепер вона не піднімає (відкликана).
    /// Читання фактів — завжди (не за прапорцем зі старого рішення), крім симуляції й системного писаря.
    /// </remarks>
    private async Task<Dictionary<int, EditDenyReason>> FindBlockedColumnsAsync(
        AccessProfile profile,
        TableInstanceRef instance,
        Ecr.Domain.Entities.Configuration.TableDef table,
        string? sheetCode,
        List<(CellAddress Address, Target Target)> allowed,
        HashSet<int> liftedColumns,
        CancellationToken ct)
    {
        var blocked = new Dictionary<int, EditDenyReason>();
        if (profile.IsSimulation || profile.IsIntegrationWriter)
        {
            return blocked;
        }

        var gate = await assignments
            .ReadFactsForGateAsync(instance.DocumentId, profile.UserId, ct)
            .ConfigureAwait(false);

        if (gate.Facts.Count == 0 && liftedColumns.Count == 0)
        {
            return blocked;
        }

        var utcNow = clock.UtcNow;
        var columnsById = table.Columns.ToDictionary(c => c.Id);

        foreach (var columnId in allowed.Select(t => t.Address.ColumnDefId).Distinct())
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
                blocked[columnId] = facts.Lock;
            }
            else if (liftedColumns.Contains(columnId) && !facts.DelegatedWrite)
            {
                blocked[columnId] = EditDenyReason.InsufficientGrantLevel;
            }
        }

        return blocked;
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

    private static BusinessRuleException TooManyCells(long count)
        => new(
            ErrorCodes.CellStyleInvalid,
            $"Область охоплює {count} комірок; стеля — {PatchCellStylesRequest.ScopeMaxCells}.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-CELL-4224.tooManyCells",
                ["count"] = count.ToString(CultureInfo.InvariantCulture),
                ["max"] = PatchCellStylesRequest.ScopeMaxCells.ToString(CultureInfo.InvariantCulture),
            });

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

    /// <summary>Розгорнута комірка: що з нею зробити й звідки це взялося.</summary>
    private sealed record Target(
        string RowKey,
        string ColumnCode,
        string? BackgroundHex,
        string? ForegroundHex,
        bool? IsBold,
        bool Reset,
        bool FromItem);

    private sealed record Decided(
        List<(CellAddress Address, Target Target)> Allowed,
        HashSet<int> LiftedColumns,
        Dictionary<EditDenyReason, int> ScopeDenied);
}
