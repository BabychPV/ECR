// src/Ecr.Application/Permits/History/PermitHistorySupport.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Validity;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.History;

/// <summary>A row's permit cell resolved for a history write: who, which row, which column, the base permit.</summary>
internal sealed record PermitHistoryRow(
    AccessProfile Profile, int? ProjectId, TableInstanceRef Instance, TableDef Table, ColumnDef Column, string RowKey,
    long RowId, long BaseEntryId, IReadOnlyDictionary<int, CellValueData> Cells)
{
    /// <summary>
    /// Visibility (404), the permit column (Lookup to a permit type), the row, the right to edit its permit cell (403) and
    /// its base permit (the cell must hold one).
    /// </summary>
    public static async Task<PermitHistoryRow> ResolveAsync(
        long documentId, long tableInstanceId, string rowKey, string columnCode,
        IRowStore rows, ICellStore cells, IMetadataCache metadata, IAccessDecisionService access, ICurrentUser currentUser,
        IPermitRegistryIndex? permitTargets, CancellationToken ct)
    {
        var profile = await Periods.OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);
        var instance = await DocumentVisibility.ResolveInDocumentAsync(rows, documentId, tableInstanceId, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireTableVisibleAsync(
            access, profile, documentId, instance.TableInstanceId, instance.TableDefId, instance.PeriodKey, ct).ConfigureAwait(false);

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var table = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == instance.TableDefId)
                    ?? throw PermitHistoryRules.TargetInvalid($"Таблиці {instance.TableDefId} немає у версії.");
        var column = table.Columns.FirstOrDefault(c => !c.IsDeleted && string.Equals(c.Code, columnCode, StringComparison.Ordinal));
        var isPermitTarget = permitTargets is null
            ? (_ => false)
            : await PermitTargets.LoadAsync(permitTargets, ct).ConfigureAwait(false);
        if (column is not { DataType: CellDataType.Lookup, LookupRegistryDefId: { } target } || !isPermitTarget(target))
        {
            throw PermitHistoryRules.TargetInvalid($"Колонка «{columnCode}» не є колонкою дозволу.");
        }

        var period = new PeriodKey(instance.PeriodKey);
        var keys = await rows.GetRowIdsBatchAsync([instance.TableInstanceId], period, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rowKey)
            || !keys.TryGetValue(instance.TableInstanceId, out var map) || !map.TryGetValue(rowKey, out var rowId))
        {
            throw PermitHistoryRules.TargetInvalid($"Рядка «{rowKey}» немає.");
        }

        // ⛔ The same decision as a write of the permit cell (rights, sheet state, closed period, assignments).
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

        var slice = await cells.ReadSliceAsync(instance.TableInstanceId, period, ct).ConfigureAwait(false);
        var rowCells = slice.Where(c => c.Address.TableRowId == rowId).ToDictionary(c => c.Address.ColumnDefId, c => c.Value);
        if (rowCells.GetValueOrDefault(column.Id)?.ValueRegistryEntryId is not { } baseId)
        {
            throw PermitHistoryRules.TargetInvalid("У рядку ще не обрано дозвіл.");
        }

        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false);
        return new PermitHistoryRow(profile, projectId, instance, table, column, rowKey, rowId, baseId, rowCells);
    }
}

/// <summary>A header permit field resolved for a history write.</summary>
internal sealed record PermitHistoryHeader(
    AccessProfile Profile, int ProjectId, int TemplateVersionId, TemplateVersionSnapshot Snapshot, HeaderFieldDef Field,
    long BaseEntryId)
{
    /// <summary>Visibility (404), the header write right (403), the permit field and its base permit.</summary>
    public static async Task<PermitHistoryHeader> ResolveAsync(
        long documentId, string fieldCode, IDocumentStore documents, IDocumentHeaderStore headers, IMetadataCache metadata,
        IPeriodStore periods, IAccessDecisionService access, ICurrentUser currentUser, IPermitRegistryIndex? permitTargets,
        CancellationToken ct)
    {
        var profile = await Periods.OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        var document = await documents.FindAsync(documentId, new PeriodKeyFilter(null), ct).ConfigureAwait(false);
        if (document is null || !profile.SeesDocumentsOf(document.ProjectId))
        {
            throw DocumentVisibility.NotFound(documentId);
        }

        if (profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Write)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта на запис у проєкт {document.ProjectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectWriteGrant",
                    ["projectId"] = document.ProjectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var project = await periods.FindProjectAsync(document.ProjectId, ct).ConfigureAwait(false)
                      ?? throw DocumentVisibility.NotFound(documentId);
        PatchDocumentHeaderHandler.EnsureEditable(profile, project, DocumentStatus.Draft);

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var field = snapshot.HeaderFields.FirstOrDefault(f => !f.IsDeleted && string.Equals(f.Code, fieldCode, StringComparison.Ordinal));
        var isPermitTarget = permitTargets is null
            ? (_ => false)
            : await PermitTargets.LoadAsync(permitTargets, ct).ConfigureAwait(false);
        if (field is not { LookupRegistryDefId: { } target } || !isPermitTarget(target))
        {
            throw PermitHistoryRules.TargetInvalid($"Поле шапки «{fieldCode}» не є полем дозволу.");
        }

        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        if (!values.TryGetValue(field.Id, out var value) || value.ValueRegistryEntryId is not { } baseId)
        {
            throw PermitHistoryRules.TargetInvalid("У полі шапки ще не обрано дозвіл.");
        }

        return new PermitHistoryHeader(profile, document.ProjectId, templateVersionId, snapshot, field, baseId);
    }
}

/// <summary>Rules and refusals shared by the history writes.</summary>
internal static class PermitHistoryRules
{
    /// <summary>Entity type of the audit records (<c>aud.StructureChange</c>).</summary>
    public const string AuditEntityType = "PermitSegment";

    /// <summary>
    /// The dates a permit change may take: the calendar year of the period — the year the month columns of the grid show
    /// (a monthly period's own bounds are one month, but the row's months run Jan–Dec).
    /// </summary>
    public static PeriodBounds Window(int periodKey)
    {
        var year = new PeriodKey(periodKey).Year;
        return new PeriodBounds(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
    }

    /// <summary>The date must lie inside the window, after its first day, and after the latest change.</summary>
    public static void EnsureDate(DateOnly validFrom, PeriodBounds window, IReadOnlyList<PermitSegment> segments)
    {
        var after = segments.Count > 0 && segments[^1].ValidFrom >= window.PeriodStart ? segments[^1].ValidFrom : window.PeriodStart;
        if (validFrom > after && validFrom <= window.PeriodEnd)
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.RequestInvalid,
            $"Дата {validFrom:dd.MM.yyyy} має бути після {after:dd.MM.yyyy} і не пізніше {window.PeriodEnd:dd.MM.yyyy}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-REQ-0422.permitHistoryDate",
                ["date"] = validFrom.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                ["after"] = after.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                ["until"] = window.PeriodEnd.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            });
    }

    /// <summary>The target cannot take a permit history (not a permit column/field, no row, no base permit, same permit).</summary>
    public static BusinessRuleException TargetInvalid(string why)
        => new(
            ErrorCodes.RequestInvalid,
            why,
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.permitHistoryTarget" });

    /// <summary>Nothing to undo (no later permit, or its months are already split).</summary>
    public static BusinessRuleException NothingToUndo()
        => new(
            ErrorCodes.RequestInvalid,
            "Немає доданого дозволу, який можна скасувати (або його місяці вже поділено).",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.permitHistoryNothingToUndo" });

    /// <summary>
    /// D-1: шкала, на якій перевіряли (поза транзакцією), мусить збігтися з шкалою під замком. Інакше між перевіркою й
    /// записом шкалу змінив хтось інший — 409, клієнт перечитує історію.
    /// </summary>
    public static void EnsureUnchanged(IEnumerable<int> checkedOn, IReadOnlyList<PermitSegment> locked)
    {
        if (!checkedOn.SequenceEqual(locked.Select(s => s.Id)))
        {
            throw new ConcurrencyConflictException(
                ErrorCodes.CellConflict,
                "Історію дозволу щойно змінив хтось інший: оновіть її й повторіть.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CELL-0409.concurrentChange" });
        }
    }

    /// <summary>j4 refusal parameters: both permits, the uncovered days, the date that would close the first gap.</summary>
    public static GapText Gap(
        RegistryEntry? previous, RegistryEntry next, IReadOnlyList<DayRange> gaps, DateOnly changeFrom, DateOnly horizon, string? language)
        => new(
            Permit: previous is null ? "…" : PatchCellsHandler.PermitName(previous, language),
            Validity: previous is null ? "…" : PermitMonthCoverage.DescribeValidity(previous.ValidFrom, previous.ValidTo),
            Next: PatchCellsHandler.PermitName(next, language),
            NextValidity: PermitMonthCoverage.DescribeValidity(next.ValidFrom, next.ValidTo),
            Days: string.Join(", ", gaps.Select(g => g.ToString())),
            DayCount: gaps.Sum(g => g.Last.DayNumber - g.First.DayNumber + 1).ToString(CultureInfo.InvariantCulture),
            SuggestedFrom: PermitTimeline.SuggestedFrom(gaps, changeFrom, next.ValidFrom, next.ValidTo, horizon)?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));

    /// <summary>Parameters of a j4 refusal (language-neutral strings).</summary>
    internal sealed record GapText(
        string Permit, string Validity, string Next, string NextValidity, string Days, string DayCount, string? SuggestedFrom);

    /// <summary>j6: the audit record of an add/undo — who (record author), both permits, the date, the target.</summary>
    public static StructureChangeRecord AuditRecord(
        PermitSegment segment, string operation, int templateVersionId, RegistryEntry? previous, RegistryEntry? next,
        string? tableCode, string columnOrFieldCode, int userId, string? correlationId, DateTime utcNow)
    {
        var state = JsonSerializer.Serialize(new
        {
            documentId = segment.DocumentId,
            table = tableCode,
            rowKey = segment.RowKey,
            sheetCopyId = segment.SheetCopyId,
            column = tableCode is null ? null : columnOrFieldCode,
            headerField = tableCode is null ? columnOrFieldCode : null,
            validFrom = segment.ValidFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            permit = next is null ? null : PatchCellsHandler.PermitName(next, null),
            permitId = segment.RegistryEntryId,
            previousPermit = previous is null ? null : PatchCellsHandler.PermitName(previous, null),
            previousPermitId = previous?.Id,
        });

        return new StructureChangeRecord(
            utcNow,
            TemplateVersionId: templateVersionId,
            EntityType: AuditEntityType,
            EntityId: segment.Id,
            ChangeClass: ChangeClass.Safe,
            Operation: operation,
            OldJson: operation == "Add" ? null : state,
            NewJson: operation == "Add" ? state : null,
            ChangeReason: null,
            ChangedByUserId: userId,
            CorrelationId: correlationId);
    }
}

/// <summary>Builds the history DTOs.</summary>
internal static class PermitHistoryReader
{
    /// <summary>One row with history, as read for the DTO.</summary>
    internal sealed record RowTarget(
        TableDef Table, ColumnDef? Column, long? SheetCopyId, string RowKey, PeriodKey Period, long BaseEntryId,
        IReadOnlyDictionary<int, CellValueData> Cells, IReadOnlyList<PermitSegment> Segments,
        IReadOnlyDictionary<int, string?> AddedBy, string? HeaderFieldCode = null)
    {
        /// <summary>The permit column's id (0 — the row relies on the header permit, Ship 2).</summary>
        public int PermitColumnId => Column?.Id ?? 0;

        /// <summary>The permit column's code, or the header field's code for a row relying on the header permit.</summary>
        public string PermitCode => Column?.Code ?? HeaderFieldCode ?? string.Empty;

        /// <summary>The spans of the history.</summary>
        public IReadOnlyList<PermitSpan> Spans { get; } =
            PermitTimeline.Spans(BaseEntryId, Segments.Select(s => (s.ValidFrom, s.RegistryEntryId)));
    }

    /// <summary>The history of the row just written.</summary>
    public static async Task<RowPermitHistoryDto> RowAsync(
        IPermitHistoryStore store, IRegistryStore registries, PermitHistoryRow row, string? language, CancellationToken ct)
    {
        var segments = await store
            .RowSegmentsAsync(row.Instance.DocumentId, row.Table.Id, row.Instance.SheetCopyId, row.RowKey, row.Column.Id, ct)
            .ConfigureAwait(false);
        var target = new RowTarget(
            row.Table, row.Column, row.Instance.SheetCopyId, row.RowKey, new PeriodKey(row.Instance.PeriodKey), row.BaseEntryId,
            row.Cells, segments, segments.ToDictionary(s => s.Id, _ => (string?)null));
        var entries = (await registries
                .FindEntriesByIdsAsync([.. segments.Select(s => s.RegistryEntryId).Append(row.BaseEntryId).Distinct()], ct)
                .ConfigureAwait(false))
            .ToDictionary(e => e.Id);
        var months = PermitTimeline.SharedMonths(target.Spans).Select(m => (m.Year * 100) + m.Month).ToList();
        var parts = await store.PartsAsync(row.Instance.DocumentId, months, ct).ConfigureAwait(false);
        return RowDto(target, entries, parts, language);
    }

    /// <summary>The history of the header field just written.</summary>
    public static async Task<HeaderPermitHistoryDto> HeaderAsync(
        IPermitHistoryStore store, IRegistryStore registries, long documentId, HeaderFieldDef field, long baseEntryId,
        string? language, CancellationToken ct)
    {
        var segments = await store.HeaderSegmentsAsync(documentId, field.Id, ct).ConfigureAwait(false);
        var entries = (await registries
                .FindEntriesByIdsAsync([.. segments.Select(s => s.RegistryEntryId).Append(baseEntryId).Distinct()], ct)
                .ConfigureAwait(false))
            .ToDictionary(e => e.Id);
        var spans = PermitTimeline.Spans(baseEntryId, segments.Select(s => (s.ValidFrom, s.RegistryEntryId)));
        return new HeaderPermitHistoryDto(
            field.Code, Items(spans, segments, segments.ToDictionary(s => s.Id, _ => (string?)null), entries, language));
    }

    /// <summary>A row's DTO: the history and the months still to split.</summary>
    public static RowPermitHistoryDto RowDto(
        RowTarget row, IReadOnlyDictionary<long, RegistryEntry> entries, IReadOnlyList<CellPermitPart> parts, string? language)
    {
        var split = new List<SplitMonthDto>();
        foreach (var month in PermitTimeline.SharedMonths(row.Spans))
        {
            var monthKey = (month.Year * 100) + month.Month;
            var inMonth = PermitTimeline.InMonth(row.Spans, month);
            var last = month.AddMonths(1).AddDays(-1);
            var spans = inMonth.Select((s, i) => new PermitPartSpanDto(
                    i + 1, s.EntryId, Name(entries, s.EntryId, language),
                    s.From is { } f && f > month ? f : month,
                    s.Until is { } u && u < last ? u : last))
                .ToList();

            // A cell is split when every part of it holds a value.
            var rowParts = parts
                .Where(p => p.PeriodKeyValue == monthKey && p.TableDefId == row.Table.Id && p.SheetCopyId == row.SheetCopyId
                            && string.Equals(p.RowKey, row.RowKey, StringComparison.Ordinal))
                .GroupBy(p => p.ColumnDefId)
                .ToDictionary(g => g.Key, g => g.ToList());
            var done = rowParts
                .Where(g => IsSplit(g.Value, spans.Count))
                .Select(g => g.Key)
                .ToHashSet();
            var pending = DataCellsInMonth(row.Table, row.Period, row.Cells, new HashSet<int> { row.PermitColumnId }, month)
                .Where(c => !done.Contains(c.Id))
                .Select(c => c.Code)
                .ToList();

            // The cells of the month: the month column of a month table, or the numeric cells holding data or parts.
            var monthColumns = PermitMonthCoverage.MonthColumns(row.Table);
            List<ColumnDef> cellColumns = monthColumns.Count > 0
                ? [.. monthColumns.Where(c => row.Period.Year == month.Year && c.MonthNumber == month.Month)]
                : row.Table.Columns
                    .Where(c => !c.IsDeleted && c.DataType is CellDataType.Decimal or CellDataType.Int && c.Id != row.PermitColumnId
                                && (rowParts.ContainsKey(c.Id)
                                    || DataCellsInMonth(row.Table, row.Period, row.Cells, new HashSet<int>(), month).Any(d => d.Id == c.Id)))
                    .ToList();
            var cellValues = cellColumns
                .Select(c => new CellSplitValueDto(
                    c.Code,
                    row.Cells.TryGetValue(c.Id, out var v) && !v.IsEmpty ? v.ValueNumeric : null,
                    [.. Enumerable.Range(1, spans.Count).Select(n => rowParts.TryGetValue(c.Id, out var ps)
                        ? ps.FirstOrDefault(p => p.PartNo == n)?.ValueNumeric
                        : null)],
                    done.Contains(c.Id)))
                .ToList();
            split.Add(new SplitMonthDto(month, pending, spans, cellValues));
        }

        return new RowPermitHistoryDto(
            row.Table.Id, row.Table.Code, row.SheetCopyId, row.RowKey, row.PermitCode,
            Items(row.Spans, row.Segments, row.AddedBy, entries, language), split,
            row.Column is null ? "header" : "row");
    }

    /// <summary>History items: the base permit, then each segment with who added it.</summary>
    public static IReadOnlyList<PermitHistoryItemDto> Items(
        IReadOnlyList<PermitSpan> spans, IReadOnlyList<PermitSegment> segments, IReadOnlyDictionary<int, string?> addedBy,
        IReadOnlyDictionary<long, RegistryEntry> entries, string? language)
    {
        var ordered = segments.OrderBy(s => s.ValidFrom).ToList();
        return [.. spans.Select((span, i) =>
        {
            var segment = i == 0 ? null : ordered[i - 1];
            return new PermitHistoryItemDto(
                segment?.Id, span.EntryId, Name(entries, span.EntryId, language), span.From, span.Until,
                segment is null ? null : addedBy.GetValueOrDefault(segment.Id), segment?.CreatedAt);
        })];
    }

    /// <summary>
    /// Entered (not calculated) numeric data cells of a row that belong to <paramref name="month"/>: the month column of
    /// that month (month table, the period's year), or every numeric data cell when the table has no month columns and the
    /// period is that month.
    /// </summary>
    public static IReadOnlyList<ColumnDef> DataCellsInMonth(
        TableDef table, PeriodKey period, IReadOnlyDictionary<int, CellValueData> cells, IReadOnlySet<int> skip, DateOnly month)
    {
        var monthColumns = PermitMonthCoverage.MonthColumns(table);
        IEnumerable<ColumnDef> candidates = monthColumns.Count > 0
            ? monthColumns.Where(c => period.Year == month.Year && c.MonthNumber == month.Month)
            : period.Year == month.Year && period.Sequence == month.Month
                ? table.Columns.Where(c => !c.IsDeleted && c.DataType is CellDataType.Decimal or CellDataType.Int)
                : [];
        return [.. candidates.Where(c => !skip.Contains(c.Id)
                                         && cells.TryGetValue(c.Id, out var v) && PermitMonthCoverage.HasData(v) && v.ValueNumeric is not null)];
    }

    /// <summary>A cell is split when every one of its <paramref name="partCount"/> parts holds a value.</summary>
    public static bool IsSplit(IReadOnlyCollection<CellPermitPart> parts, int partCount)
        => parts.Count >= partCount && parts.Where(p => p.PartNo <= partCount).All(p => p.ValueNumeric is not null)
           && parts.Count(p => p.PartNo <= partCount) == partCount;

    private static string Name(IReadOnlyDictionary<long, RegistryEntry> entries, long id, string? language)
        => entries.TryGetValue(id, out var e) ? PatchCellsHandler.PermitName(e, language) : id.ToString(CultureInfo.InvariantCulture);
}
