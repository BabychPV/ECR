// src/Ecr.Application/Permits/Bindings/RefreshPermitBindingHandler.cs
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Підсумок оновлення таблиці документа з дозволу.</summary>
/// <param name="Added">Скільки рядків додано.</param>
/// <param name="Updated">У скількох наявних рядках оновлено ліміти чи довідкові значення.</param>
/// <param name="RemovedFromPermit">Ключі рядків документа, яких у дозволі більше немає (рядки НЕ видалено).</param>
/// <param name="Warnings">Попередження.</param>
public sealed record PermitRefreshResultDto(
    int Added, int Updated, IReadOnlyList<string> RemovedFromPermit, IReadOnlyList<PermitPreviewWarningDto> Warnings);

/// <summary>
/// «Оновити з дозволу» (Permits, PM-10): додає в таблицю документа нові рядки дозволу й оновлює заблоковані значення.
/// </summary>
/// <remarks>
/// <para>
/// Право - те саме, що на додавання рядка в цю таблицю (<c>CanCreateRowsAsync</c>): закритий період, поданий і
/// затверджений аркуш відхиляються тією самою відмовою <c>ECR-ACCS-0403 addRowDenied</c>, що й <c>CreateRowHandler</c>.
/// Нових кодів помилок немає.
/// </para>
/// <para>
/// ⛔ Крім права на аркуш потрібне право читати реєстри дозволу (<see cref="PermitRegistryReadGuard"/>): відповідь несе
/// дані дозволу. Типові ролі введення даних мають <c>Registry.View</c>, тож це нічого не звужує на практиці.
/// </para>
/// <para>
/// ⛔ Рядків НЕ видаляє: у них можуть бути введені факти. Зниклі з дозволу ключі повертаються в
/// <see cref="PermitRefreshResultDto.RemovedFromPermit"/>.
/// </para>
/// </remarks>
public sealed class RefreshPermitBindingHandler(
    IRowStore rows,
    IDocumentStore documents,
    IPermitBindingStore bindings,
    IPeriodStore periods,
    IPermitBindingService service,
    IPermitTypeStore types,
    IMetadataCache metadata,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IClock clock,
    ISheetEditGate sheetGate,
    IBackgroundJobScheduler jobs,
    IEditAssignmentStore? assignments = null)
{
    /// <summary>Оновлює таблицю документа з дозволу.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="periodKeyValue">Період документа (<c>РРРРММ</c>).</param>
    /// <param name="profile">Профіль того, хто оновлює.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PermitRefreshResultDto> HandleAsync(
        long documentId, string code, int periodKeyValue, AccessProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Нерозкриття: невидимий документ - 404, як в усіх обробниках рядків.
        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        var periodKey = PeriodKey.Parse(periodKeyValue);
        var bounds = await periods.FindPeriodBoundsAsync(documentId, periodKey.Value, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.PreviewPeriodUnknown(periodKey.Value);

        var versionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var view = await bindings.GetAsync(versionId, code, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.NotFound(code, versionId);

        if (!view.Binding.IsActive || view.Binding.Mode != PermitBindingMode.Rows)
        {
            return new PermitRefreshResultDto(
                0, 0, [], [new PermitPreviewWarningDto("notRowsBinding", "Прив'язка вимкнена або працює формулами: рядків із дозволу немає.")]);
        }

        // ⛔ C4: відповідь несе дані дозволу (номер у попередженнях, ключі зниклих рядків), а запис змінює ліміти в
        // комірках, - тож потрібне те саме право читати реєстри дозволу, що й у перегляді. ДО створення екземпляра.
        var type = await types.FindByCodeAsync(view.PermitTypeCode, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.NotFound(code, versionId);
        var permitTable = (await types.ListTablesAsync(type.Id, ct).ConfigureAwait(false))
            .FirstOrDefault(t => t.Id == view.Binding.PermitTypeTableId)
            ?? throw PermitBindingErrors.NotFound(code, versionId);
        PermitRegistryReadGuard.Require(profile, type, permitTable, () => PermitBindingErrors.NotFound(code, versionId));

        // Екземпляр створюється (разом із рядками дозволу) тим самим шляхом, що й під час відкриття документа.
        await rows.EnsureTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);
        var instance = (await rows.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false))
            .FirstOrDefault(i => i.TableDefId == view.Binding.SheetTableDefId)
            ?? throw DocumentVisibility.DocumentNotFound(documentId);

        await DocumentVisibility
            .RequireTableVisibleAsync(
                access, profile, documentId, instance.TableInstanceId, instance.TableDefId, instance.PeriodKey, ct)
            .ConfigureAwait(false);

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var table = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == instance.TableDefId)
            ?? throw DocumentVisibility.DocumentNotFound(documentId);
        var sheetCode = snapshot.Sheets.FirstOrDefault(s => s.Id == table.SheetDefId)?.Code;

        var plan = await service.PlanAsync(view, documentId, bounds.PeriodEnd, ct).ConfigureAwait(false);

        // Права - ДО транзакції: на ключі, що з'являться, і на наявні рядки дозволу; порожній набір питається про
        // службовий ключ, щоб стан періоду й аркуша перевірився все одно.
        var existing = await rows.GetRowIdsAsync(instance.TableInstanceId, periodKey, ct).ConfigureAwait(false);
        var keys = plan.Rows.Rows.Select(r => r.RowKey)
            .Concat(existing.Keys.Where(IsPermitKey))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count == 0)
        {
            keys.Add(PermitRowPlanner.RowKeyFor(0));
        }

        var decisions = await access
            .CanCreateRowsAsync(profile, instance.TableInstanceId, keys, ct)
            .ConfigureAwait(false);
        var assignmentsPresent = false;
        foreach (var key in keys)
        {
            var decision = decisions.TryGetValue(key, out var found)
                ? found.Row
                : EditDecision.Deny(EditDenyReason.NoGrant, "Рішення про доступ не отримано.");
            if (!decision.IsAllowed)
            {
                throw AddRowDenied(decision.Reason);
            }

            assignmentsPresent |= decision.AssignmentsPresent;
        }

        PermitRefreshResult? result = null;
        var enlist = jobs.EnlistsInCallerTransaction;

        await uow.ExecuteInTransactionAsync(
            async innerCt =>
            {
                result = await RefreshUnderLockAsync(
                        documentId, periodKey, instance, table, view, plan, profile, assignmentsPresent, sheetCode, innerCt)
                    .ConfigureAwait(false);

                // ⛔ MI-02 (в): постановка перерахунку - останнім оператором транзакції (черга в базі) або після коміту.
                if (enlist && result.ChangedCells.Count > 0)
                {
                    await EnqueueRecalculationAsync(instance, result, profile.UserId, innerCt).ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);

        if (!enlist && result is { ChangedCells.Count: > 0 })
        {
            await EnqueueRecalculationAsync(instance, result, profile.UserId, ct).ConfigureAwait(false);
        }

        var final = result!;
        return new PermitRefreshResultDto(
            final.Added,
            final.Updated,
            final.RemovedFromPermit,
            [.. final.Warnings.DistinctBy(w => w.MessageKey + w.Text).Select(w => new PermitPreviewWarningDto(w.Case, w.Text))]);
    }

    private async Task<PermitRefreshResult> RefreshUnderLockAsync(
        long documentId,
        PeriodKey periodKey,
        TableInstanceRef instance,
        Domain.Entities.Configuration.TableDef table,
        PermitBindingView view,
        PermitBindingPlan plan,
        AccessProfile profile,
        bool assignmentsPresent,
        string? sheetCode,
        CancellationToken ct)
    {
        // ⛔ L6-02: структура документа - першою, до блокування аркуша (як у створенні рядка).
        DocumentStructure.EnsureUnchanged(
            await sheetGate.EnterStructureAsync(instance.DocumentId, exclusive: false, ct).ConfigureAwait(false),
            instance.TemplateVersionId,
            instance.DocumentId);

        await RowWriteLock.EnterAsync(sheetGate, instance.DocumentId, table.SheetDefId, periodKey, ct).ConfigureAwait(false);
        var status = await sheetGate.EnterEditAsync(instance.DocumentId, table.SheetDefId, periodKey, ct).ConfigureAwait(false);

        // Поданий і затверджений аркуш не оновлюється (ті самі причини, що й у створення рядка).
        if (status == DocumentStatus.Submitted)
        {
            throw AddRowDenied(EditDenyReason.DocumentSubmitted);
        }

        if (status == DocumentStatus.Approved)
        {
            throw AddRowDenied(EditDenyReason.DocumentApproved);
        }

        await EnsureAssignmentsHoldAsync(instance, table, profile, assignmentsPresent, sheetCode, ct).ConfigureAwait(false);

        var result = await service
            .RefreshAsync(documentId, periodKey, instance.TableInstanceId, view, plan, ct)
            .ConfigureAwait(false);

        if (result.Added > 0 || result.Updated > 0)
        {
            await documents.TouchAsync(instance.DocumentId, profile.UserId, clock.UtcNow, ct).ConfigureAwait(false);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return result;
    }

    private async Task EnsureAssignmentsHoldAsync(
        TableInstanceRef instance, Domain.Entities.Configuration.TableDef table, AccessProfile profile,
        bool assignmentsPresent, string? sheetCode, CancellationToken ct)
    {
        if (!assignmentsPresent || profile.IsSimulation || profile.IsIntegrationWriter)
        {
            return;
        }

        if (assignments is null)
        {
            throw new InvalidOperationException(
                "Передачі зрізу діють, але сховища для повторної перевірки під блокуванням немає.");
        }

        var gate = await assignments.ReadFactsForGateAsync(instance.DocumentId, profile.UserId, ct).ConfigureAwait(false);
        var facts = EditAssignmentGuard.Evaluate(
            profile.UserId,
            gate.OwnerUserId,
            isIntegrationWriter: false,
            gate.Facts,
            new EditSliceFacts(
                sheetCode, table.Code, new PeriodKey(instance.PeriodKey), ColumnIsMonth: false, ColumnMonthNumber: null,
                IsRowLevel: true),
            clock.UtcNow);

        if (facts.Lock != EditDenyReason.None)
        {
            throw AddRowDenied(facts.Lock);
        }
    }

    private static bool IsPermitKey(string key)
        => key.Length > 1 && key[0] == 'P' && key.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    // Та сама відмова, що й у `CreateRowHandler`: для людини це та сама дія - додати рядок у таблицю.
    private static AccessDeniedException AddRowDenied(EditDenyReason reason)
        => new(
            "ECR-ACCS-0403",
            $"Рядок у цю таблицю додати не можна: {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.addRowDenied",
                ["reason"] = reason.ToString(),
            });

    private Task<string> EnqueueRecalculationAsync(
        TableInstanceRef instance, PermitRefreshResult result, int editorUserId, CancellationToken ct)
        => jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
            FormulaRecalculationTarget.Of(instance.DocumentId, instance.PeriodKey, editorUserId),
            new
            {
                DocumentId = instance.DocumentId,
                TableInstanceId = instance.TableInstanceId,
                PeriodKey = instance.PeriodKey,
                Cells = RecalculationSeed.Stamp(
                    result.ChangedCells.Select(c => new RecalculationSeed(c.RowId, c.ColumnId)),
                    instance.PeriodKey, instance.TableInstanceId),
            },
            ct,
            createdByUserId: editorUserId);
}
