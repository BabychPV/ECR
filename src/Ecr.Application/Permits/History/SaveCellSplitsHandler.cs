// src/Ecr.Application/Permits/History/SaveCellSplitsHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Validity;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.History;

/// <summary>
/// Feature (j) j2/jh3, human 08.11 («Користувач має сам поділити значення для місяця на 2 частини»): saves the parts of
/// month cells shared by two (or more) permits of the row. The user types every part — ECR never divides by days.
/// </summary>
/// <remarks>
/// <para>
/// A cell whose parts are ALL entered gets the month total = the sum of the parts, written through the ordinary cell write
/// (<see cref="PatchCellsHandler"/>: rights, sheet state, validity rule, audit of the value, recalculation, row version) in
/// <see cref="PermitSplitWrite"/> scope, so the j3 refusal (<c>ECR-CELL-4225</c>) does not refuse the split itself. Then
/// the parts are stored (<c>doc.CellPermitPart</c>) and audited (<c>CellPermitPart/Split</c>: who, every part, its permit
/// and days — j6). Partial save (jh3): parts without a total — the old single value stays «unsplit» and keeps blocking
/// Submit.
/// </para>
/// <para>
/// ⛔ Rights: the same edit decision as the month cell (<see cref="IAccessDecisionService.CanEditCellAsync"/>), checked for
/// every item before anything is written.
/// </para>
/// </remarks>
public sealed class SaveCellSplitsHandler(
    IPermitHistoryStore store,
    IRowStore rows,
    ICellStore cells,
    IMetadataCache metadata,
    IRegistryStore registries,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    PatchCellsHandler patchCells,
    GetPermitHistoryHandler history,
    IDocumentHeaderStore headers,
    IPermitRegistryIndex? permitTargets = null,

    // F13 p223 (1): the stored Issues of the year are re-run after the split. `null` (tests) — not re-run.
    ValidateDocumentHandler? revalidate = null,
    IValidationResultStore? validationResults = null)
{
    /// <summary>Entity type of the split audit records (<c>aud.StructureChange</c>).</summary>
    public const string AuditEntityType = "CellPermitPart";

    /// <summary>Most cells in one request (the bulk split of jh3 lists one row per cell).</summary>
    public const int MaxItems = 500;

    /// <summary>Saves the parts and returns the document's permit history in <c>PeriodKey</c>.</summary>
    public async Task<DocumentPermitHistoryDto> HandleAsync(long documentId, SaveCellSplitsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items is not { Count: > 0 } items || items.Count > MaxItems)
        {
            throw PartsInvalid("Порожній запит або забагато комірок.");
        }

        var profile = await Periods.OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);
        var segments = (await store.ListAsync(documentId, ct).ConfigureAwait(false)).Select(r => r.Segment).ToList();
        var isPermitTarget = permitTargets is null
            ? (_ => false)
            : await PermitTargets.LoadAsync(permitTargets, ct).ConfigureAwait(false);

        // 1. Resolve and check every item before anything is written.
        var plans = new List<Plan>(items.Count);
        foreach (var item in items)
        {
            plans.Add(await PlanAsync(documentId, profile, item, segments, isPermitTarget, ct).ConfigureAwait(false));
        }

        // 2 + 3. ONE transaction (coordinator 09.11, no debt): the totals — the ordinary cell write, whose own
        // ExecuteInTransactionAsync JOINS the current transaction — and the parts with their audit. A failure of either
        // leaves neither: never a new total without its parts.
        var now = clock.UtcNow;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            using (PermitSplitWrite.Begin())
            {
                foreach (var group in plans.Where(p => p.Total is not null).GroupBy(p => p.Instance.TableInstanceId))
                {
                    var first = group.First();
                    var patch = new PatchCellsRequest(
                        first.Instance.TableInstanceId,
                        first.Instance.PeriodKey,
                        CellChangeOrigins.UserEdit,
                        [.. group.GroupBy(p => p.RowKey, StringComparer.Ordinal).Select(r => new PatchRow(
                            r.Key,
                            r.First().BaseVersion,
                            [.. r.Select(p => new PatchCell(p.Column.Code, p.Total))]))]);
                    await patchCells.HandleAsync(patch, innerCt, first.Instance).ConfigureAwait(false);
                }
            }

            foreach (var plan in plans)
            {
                var monthKey = (plan.Month.Year * 100) + plan.Month.Month;
                var stored = await store.CellPartsAsync(
                        monthKey, documentId, plan.Table.Id, plan.Instance.SheetCopyId, plan.RowKey, plan.Column.Id, innerCt)
                    .ConfigureAwait(false);
                foreach (var old in stored)
                {
                    store.RemovePart(old);
                }

                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
                for (var i = 0; i < plan.Spans.Count; i++)
                {
                    var span = plan.Spans[i];
                    store.AddPart(new CellPermitPart(
                        monthKey, documentId, plan.Table.Id, plan.Instance.SheetCopyId, plan.RowKey, plan.Column.Id,
                        (byte)(i + 1), span.EntryId, span.DayFrom, span.DayTo, plan.Parts[i]));
                }

                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        now,
                        TemplateVersionId: plan.Instance.TemplateVersionId,
                        EntityType: AuditEntityType,
                        EntityId: plan.Column.Id,
                        ChangeClass: ChangeClass.Safe,
                        Operation: "Split",
                        OldJson: stored.Count == 0
                            ? null
                            : JsonSerializer.Serialize(new { parts = stored.Select(p => new { p.PartNo, p.RegistryEntryId, value = p.ValueNumeric }) }),
                        NewJson: JsonSerializer.Serialize(new
                        {
                            documentId,
                            table = plan.Table.Code,
                            sheetCopyId = plan.Instance.SheetCopyId,
                            rowKey = plan.RowKey,
                            column = plan.Column.Code,
                            month = plan.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                            total = plan.Total,
                            parts = plan.Spans.Select((s, i) => new
                            {
                                part = i + 1,
                                permit = s.PermitCode,
                                permitId = s.EntryId,
                                from = s.DayFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                to = s.DayTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                value = plan.Parts[i],
                            }),
                        }),
                        ChangeReason: null,
                        ChangedByUserId: profile.UserId,
                        CorrelationId: currentUser.CorrelationId),
                    innerCt).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);

        await PermitHistoryRevalidation.RunAsync(revalidate, validationResults, documentId, request.PeriodKey / 100, ct).ConfigureAwait(false);
        return await history.HandleAsync(documentId, request.PeriodKey, ct).ConfigureAwait(false);
    }

    private async Task<Plan> PlanAsync(
        long documentId, AccessProfile profile, CellSplitItem item, IReadOnlyList<PermitSegment> segments,
        Func<int, bool> isPermitTarget, CancellationToken ct)
    {
        var instance = await DocumentVisibility.ResolveInDocumentAsync(rows, documentId, item.TableInstanceId, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireTableVisibleAsync(
            access, profile, documentId, instance.TableInstanceId, instance.TableDefId, instance.PeriodKey, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var table = snapshot.Sheets.SelectMany(s => s.Tables).First(t => t.Id == instance.TableDefId);
        var column = table.Columns.FirstOrDefault(c => !c.IsDeleted && string.Equals(c.Code, item.ColumnCode, StringComparison.Ordinal)
                                                      && c.DataType is CellDataType.Decimal or CellDataType.Int)
                     ?? throw PartsInvalid("Колонка не числова або її немає.");

        var period = new PeriodKey(instance.PeriodKey);
        var keys = await rows.GetRowIdsBatchAsync([instance.TableInstanceId], period, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(item.RowKey) || string.IsNullOrWhiteSpace(item.BaseVersion)
            || !keys.TryGetValue(instance.TableInstanceId, out var map) || !map.TryGetValue(item.RowKey, out var rowId))
        {
            throw PartsInvalid("Рядка немає або немає baseVersion.");
        }

        var decision = await access.CanEditCellAsync(profile, documentId, new CellAddress(period, rowId, column.Id), ct)
            .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw new AccessDeniedException(
                "ECR-ACCS-0403",
                $"Заборонених комірок у запиті: 1. Причина першої: {decision.Reason}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ACCS-0403.deniedCells",
                    ["deniedCount"] = "1",
                    ["reason"] = decision.Reason.ToString(),
                });
        }

        // The row's permit column WITH history (the one whose permits share the month).
        var rowSegments = segments
            .Where(s => s.TableDefId == table.Id && s.SheetCopyId == instance.SheetCopyId
                        && string.Equals(s.RowKey, item.RowKey, StringComparison.Ordinal))
            .GroupBy(s => s.ColumnDefId!.Value)
            .Where(g => table.Columns.Any(c => c.Id == g.Key && c.LookupRegistryDefId is { } t && isPermitTarget(t)))
            .ToList();
        IReadOnlyList<PermitSpan> spans;
        if (rowSegments.Count == 1)
        {
            var slice = await cells.ReadSliceAsync(instance.TableInstanceId, period, ct).ConfigureAwait(false);
            var baseId = slice.FirstOrDefault(c => c.Address.TableRowId == rowId && c.Address.ColumnDefId == rowSegments[0].Key)
                ?.Value.ValueRegistryEntryId ?? throw PartsInvalid("У рядку ще не обрано дозвіл.");
            spans = PermitHistoryCoverage.SpansOf(baseId, rowSegments[0]);
        }
        else if (rowSegments.Count == 0
                 && !table.Columns.Any(c => !c.IsDeleted && c.LookupRegistryDefId is { } t && isPermitTarget(t))
                 && segments.Where(s => s.HeaderFieldDefId is not null).GroupBy(s => s.HeaderFieldDefId!.Value).OrderBy(g => g.Key)
                     .FirstOrDefault() is { } header)
        {
            // ✎ Ship 2 (jh3): a table without its own permit column relies on the header permit and its history.
            var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
            var baseId = values.TryGetValue(header.Key, out var value) && value.ValueRegistryEntryId is { } id
                ? id
                : throw PartsInvalid("У шапці ще не обрано дозвіл.");
            spans = PermitHistoryCoverage.SpansOf(baseId, header);
        }
        else
        {
            throw PartsInvalid("У рядка немає історії дозволу.");
        }

        var monthNumber = column.MonthNumber is { } m ? (int)m : period.Sequence;
        var month = new DateOnly(period.Year, monthNumber, 1);
        var inMonth = PermitTimeline.InMonth(spans, month);
        if (inMonth.Count < 2)
        {
            throw PartsInvalid("Цей місяць не ділиться між дозволами.");
        }

        if (item.Parts is null || item.Parts.Count != inMonth.Count)
        {
            throw PartsInvalid($"Очікується частин: {inMonth.Count}.");
        }

        var entries = (await registries.FindEntriesByIdsAsync([.. inMonth.Select(s => s.EntryId).Distinct()], ct).ConfigureAwait(false))
            .ToDictionary(e => e.Id);
        var last = month.AddMonths(1).AddDays(-1);
        var parts = inMonth
            .Select(s => new PartSpan(
                s.EntryId,
                entries.TryGetValue(s.EntryId, out var e) ? PatchCellsHandler.PermitName(e, null) : string.Empty,
                s.From is { } f && f > month ? f : month,
                s.Until is { } u && u < last ? u : last))
            .ToList();

        // No pro rata, ever: the total exists only when the user typed every part.
        decimal? total = item.Parts.All(p => p is not null) ? item.Parts.Sum(p => p!.Value) : null;
        return new Plan(instance, table, column, item.RowKey, item.BaseVersion, month, parts, item.Parts, total);
    }

    private static BusinessRuleException PartsInvalid(string why)
        => new(
            ErrorCodes.RequestInvalid,
            why,
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.permitSplitInvalid" });

    private sealed record PartSpan(long EntryId, string PermitCode, DateOnly DayFrom, DateOnly DayTo);

    private sealed record Plan(
        TableInstanceRef Instance, Domain.Entities.Configuration.TableDef Table, Domain.Entities.Configuration.ColumnDef Column,
        string RowKey, string BaseVersion, DateOnly Month, IReadOnlyList<PartSpan> Spans, IReadOnlyList<decimal?> Parts,
        decimal? Total);
}
