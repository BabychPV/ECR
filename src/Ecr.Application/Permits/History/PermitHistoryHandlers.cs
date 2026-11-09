// src/Ecr.Application/Permits/History/PermitHistoryHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Validity;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.History;

/// <summary>
/// Feature (j), human 08.11 («Користувач має сам поділити значення для місяця на 2 частини»; mockup approved 09.11): a
/// row's permit cell keeps its history — «KZ…A until 14.02 · KZ…B from 15.02» — in the same document and the same row.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Who may add: anyone who may edit the row's permit cell (human default 1: «anyone who can edit the sheet»; the
/// registry itself stays admin-only) — <see cref="IAccessDecisionService.CanEditCellAsync"/>, the same decision as a cell
/// write; otherwise <c>403 ECR-ACCS-0403 deniedCells</c>. An invisible document — <c>404</c>, as everywhere.
/// </para>
/// <para>
/// j4: a gap between the previous permit's validity and the new one is refused naming the days
/// (<c>422 ECR-CELL-4223 permitGap</c>). Every add/undo goes to <c>aud.StructureChange</c> (<c>PermitSegment</c>) with
/// the user, both permits and the date (j6).
/// </para>
/// </remarks>
public sealed class AddRowPermitHandler(
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
    IPermitRegistryIndex? permitTargets = null,
    Groups.ILookupTargetResolver? groups = null)
{
    /// <summary>Adds the permit and returns the row's history.</summary>
    public async Task<RowPermitHistoryDto> HandleAsync(long documentId, AddRowPermitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await PermitHistoryRow.ResolveAsync(
            documentId, request.TableInstanceId, request.RowKey, request.ColumnCode,
            rows, cells, metadata, access, currentUser, permitTargets, ct).ConfigureAwait(false);

        var window = PermitHistoryRules.Window(row.Instance.PeriodKey);
        var segments = await store
            .RowSegmentsAsync(documentId, row.Table.Id, row.Instance.SheetCopyId, row.RowKey, row.Column.Id, ct)
            .ConfigureAwait(false);
        PermitHistoryRules.EnsureDate(request.ValidFrom, window, segments);

        var previousId = segments.Count > 0 ? segments[^1].RegistryEntryId : row.BaseEntryId;
        var entries = (await registries.FindEntriesByIdsAsync([previousId, request.RegistryEntryId], ct).ConfigureAwait(false))
            .ToDictionary(e => e.Id);

        if (entries.TryGetValue(request.RegistryEntryId, out var candidate))
        {
            await Groups.LookupTargetMatcher
                .EnsureFreshOnMismatchAsync(groups, [(row.Column.LookupRegistryDefId, candidate.RegistryDefId)], ct)
                .ConfigureAwait(false);
        }

        if (candidate is not { } next || next.IsDeleted || !next.IsActive
            || !Groups.LookupTargetMatcher.Belongs(groups, row.Profile, row.Column.LookupRegistryDefId, next.RegistryDefId, row.ProjectId))
        {
            throw new BusinessRuleException(
                ErrorCodes.CellRegistryEntryMissing,
                $"Запис {request.RegistryEntryId} не можна обрати для колонки дозволу.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CELL-4223.missingEntry",
                    ["cellCount"] = "1",
                });
        }

        if (previousId == next.Id)
        {
            throw PermitHistoryRules.TargetInvalid("Новий дозвіл той самий, що й чинний.");
        }

        var previous = entries.GetValueOrDefault(previousId);
        var gaps = PermitTimeline.GapDays(previous?.ValidTo, request.ValidFrom, next.ValidFrom, next.ValidTo, window.PeriodEnd);
        if (gaps.Count > 0)
        {
            var gap = PermitHistoryRules.Gap(previous, next, gaps, request.ValidFrom, window.PeriodEnd, currentUser.Language);
            throw new BusinessRuleException(
                ErrorCodes.CellRegistryEntryMissing,
                $"Між дозволами немає чинного дозволу: {gap.Days}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CELL-4223.permitGap",
                    ["permit"] = gap.Permit,
                    ["validity"] = gap.Validity,
                    ["next"] = gap.Next,
                    ["nextValidity"] = gap.NextValidity,
                    ["days"] = gap.Days,
                    ["dayCount"] = gap.DayCount,
                    ["suggestedFrom"] = gap.SuggestedFrom,
                    ["rowKey"] = row.RowKey,
                });
        }

        var now = clock.UtcNow;
        var segment = PermitSegment.ForRow(
            documentId, row.Table.Id, row.Instance.SheetCopyId, row.RowKey, row.Column.Id,
            request.ValidFrom, next.Id, row.Profile.UserId, now);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            store.Add(segment);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            await audit.WriteStructureChangeAsync(
                PermitHistoryRules.AuditRecord(
                    segment, "Add", row.Instance.TemplateVersionId, previous, next, row.Table.Code, row.Column.Code,
                    row.Profile.UserId, currentUser.CorrelationId, now),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return await PermitHistoryReader.RowAsync(store, registries, row, currentUser.Language, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Undoes the latest permit added to a row's permit cell (the same right as adding it). Refused while a split of that
/// permit's months is entered (the parts depend on it) and when there is nothing to undo.
/// </summary>
public sealed class UndoRowPermitHandler(
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
    IPermitRegistryIndex? permitTargets = null)
{
    /// <summary>Removes the latest permit and returns the row's history.</summary>
    public async Task<RowPermitHistoryDto> HandleAsync(long documentId, UndoRowPermitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await PermitHistoryRow.ResolveAsync(
            documentId, request.TableInstanceId, request.RowKey, request.ColumnCode,
            rows, cells, metadata, access, currentUser, permitTargets, ct).ConfigureAwait(false);

        var segments = await store
            .RowSegmentsAsync(documentId, row.Table.Id, row.Instance.SheetCopyId, row.RowKey, row.Column.Id, ct)
            .ConfigureAwait(false);
        if (segments.Count == 0)
        {
            throw PermitHistoryRules.NothingToUndo();
        }

        var last = segments[^1];
        if (await store.RowHasPartsOfAsync(documentId, row.Table.Id, row.Instance.SheetCopyId, row.RowKey, last.RegistryEntryId, ct)
                .ConfigureAwait(false))
        {
            throw PermitHistoryRules.NothingToUndo();
        }

        var previousId = segments.Count > 1 ? segments[^2].RegistryEntryId : row.BaseEntryId;
        var entries = (await registries.FindEntriesByIdsAsync([previousId, last.RegistryEntryId], ct).ConfigureAwait(false))
            .ToDictionary(e => e.Id);

        var now = clock.UtcNow;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            store.Remove(last);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            await audit.WriteStructureChangeAsync(
                PermitHistoryRules.AuditRecord(
                    last, "Remove", row.Instance.TemplateVersionId, entries.GetValueOrDefault(previousId),
                    entries.GetValueOrDefault(last.RegistryEntryId), row.Table.Code, row.Column.Code,
                    row.Profile.UserId, currentUser.CorrelationId, now),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return await PermitHistoryReader.RowAsync(store, registries, row, currentUser.Language, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// jh1: a header permit «from date». Ship 1 (coordinator 09.11): only a change on the 1st is accepted (no month is
/// shared, nothing to split). A change mid-month is refused with «add the permit per row», NAMING the rows that rely on
/// the header permit and hold data in that month (<c>422 ECR-HDR-4223 permitChangeMidMonth</c>).
/// </summary>
/// <remarks>
/// ⛔ The same rights as a header write (project Write grant + <see cref="EditRules"/> on the document state).
/// </remarks>
public sealed class AddHeaderPermitHandler(
    IPermitHistoryStore store,
    IDocumentStore documents,
    IDocumentHeaderStore headers,
    IRowStore rows,
    ICellStore cells,
    IMetadataCache metadata,
    IPeriodStore periods,
    IRegistryStore registries,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    IPermitRegistryIndex? permitTargets = null,
    Groups.ILookupTargetResolver? groups = null)
{
    /// <summary>How many rows a mid-month refusal names; the rest as a count.</summary>
    public const int NamedRowsLimit = 10;

    /// <summary>Adds the permit and returns the field's history.</summary>
    public async Task<HeaderPermitHistoryDto> HandleAsync(long documentId, AddHeaderPermitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = await PermitHistoryHeader.ResolveAsync(
            documentId, request.FieldCode, documents, headers, metadata, periods, access, currentUser, permitTargets, ct)
            .ConfigureAwait(false);

        var window = PermitHistoryRules.Window(request.PeriodKey);
        var segments = await store.HeaderSegmentsAsync(documentId, header.Field.Id, ct).ConfigureAwait(false);
        PermitHistoryRules.EnsureDate(request.ValidFrom, window, segments);

        if (request.ValidFrom.Day != 1)
        {
            var affected = await RowsRelyingOnHeaderAsync(documentId, header, request, ct).ConfigureAwait(false);
            throw new BusinessRuleException(
                ErrorCodes.HeaderEntryNotUsable,
                $"Зміна дозволу шапки посеред місяця ({request.ValidFrom:dd.MM.yyyy}): додайте дозвіл у рядках.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-HDR-4223.permitChangeMidMonth",
                    ["headerFieldCode"] = header.Field.Code,
                    ["date"] = request.ValidFrom.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                    ["rows"] = affected.Count == 0
                        ? "—"
                        : string.Join(", ", affected.Take(NamedRowsLimit))
                          + (affected.Count > NamedRowsLimit ? $" (+{affected.Count - NamedRowsLimit})" : string.Empty),
                    ["rowCount"] = affected.Count.ToString(CultureInfo.InvariantCulture),
                    ["affectedRows"] = affected,
                });
        }

        var previousId = segments.Count > 0 ? segments[^1].RegistryEntryId : header.BaseEntryId;
        var entries = (await registries.FindEntriesByIdsAsync([previousId, request.RegistryEntryId], ct).ConfigureAwait(false))
            .ToDictionary(e => e.Id);
        if (entries.TryGetValue(request.RegistryEntryId, out var candidate))
        {
            await Groups.LookupTargetMatcher
                .EnsureFreshOnMismatchAsync(groups, [(header.Field.LookupRegistryDefId, candidate.RegistryDefId)], ct)
                .ConfigureAwait(false);
        }

        if (candidate is not { } next
            || !Groups.LookupTargetMatcher.Belongs(groups, header.Profile, header.Field.LookupRegistryDefId, next.RegistryDefId, header.ProjectId))
        {
            throw HeaderEntry(EntryProblem.ForeignRegistry, header.Field.Code);
        }

        if (next.IsDeleted)
        {
            throw HeaderEntry(EntryProblem.Deleted, header.Field.Code);
        }

        if (!next.IsActive)
        {
            throw HeaderEntry(EntryProblem.Inactive, header.Field.Code);
        }

        if (previousId == next.Id)
        {
            throw PermitHistoryRules.TargetInvalid("Новий дозвіл той самий, що й чинний.");
        }

        var previous = entries.GetValueOrDefault(previousId);
        var gaps = PermitTimeline.GapDays(previous?.ValidTo, request.ValidFrom, next.ValidFrom, next.ValidTo, window.PeriodEnd);
        if (gaps.Count > 0)
        {
            var gap = PermitHistoryRules.Gap(previous, next, gaps, request.ValidFrom, window.PeriodEnd, currentUser.Language);
            throw new BusinessRuleException(
                ErrorCodes.HeaderEntryNotUsable,
                $"Між дозволами шапки немає чинного дозволу: {gap.Days}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-HDR-4223.permitGap",
                    ["permit"] = gap.Permit,
                    ["validity"] = gap.Validity,
                    ["next"] = gap.Next,
                    ["nextValidity"] = gap.NextValidity,
                    ["days"] = gap.Days,
                    ["dayCount"] = gap.DayCount,
                    ["suggestedFrom"] = gap.SuggestedFrom,
                    ["headerFieldCode"] = header.Field.Code,
                });
        }

        var now = clock.UtcNow;
        var segment = PermitSegment.ForHeader(documentId, header.Field.Id, request.ValidFrom, next.Id, header.Profile.UserId, now);
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            store.Add(segment);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            await audit.WriteStructureChangeAsync(
                PermitHistoryRules.AuditRecord(
                    segment, "Add", header.TemplateVersionId, previous, next, tableCode: null, header.Field.Code,
                    header.Profile.UserId, currentUser.CorrelationId, now),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return await PermitHistoryReader.HeaderAsync(store, registries, documentId, header.Field, header.BaseEntryId, currentUser.Language, ct)
            .ConfigureAwait(false);
    }

    private enum EntryProblem
    {
        ForeignRegistry,
        Deleted,
        Inactive,
    }

    private static BusinessRuleException HeaderEntry(EntryProblem problem, string fieldCode)
        => new(
            ErrorCodes.HeaderEntryNotUsable,
            $"Запис не можна обрати для поля дозволу шапки ({problem}).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = problem switch
                {
                    EntryProblem.Deleted => "err.ECR-HDR-4223.deletedEntry",
                    EntryProblem.Inactive => "err.ECR-HDR-4223.inactiveEntry",
                    _ => "err.ECR-HDR-4223.foreignRegistry",
                },
                ["headerFieldCode"] = fieldCode,
            });

    /// <summary>
    /// «Table · row» of the rows that rely on the header permit (tables WITHOUT their own permit column) and hold entered
    /// data in the month of the change — only tables the author can READ (a hidden table is neither named nor counted).
    /// </summary>
    private async Task<List<string>> RowsRelyingOnHeaderAsync(
        long documentId, PermitHistoryHeader header, AddHeaderPermitRequest request, CancellationToken ct)
    {
        var period = new PeriodKey(request.PeriodKey);
        var readable = (await access.ReadScopeAsync(header.Profile, documentId, ct).ConfigureAwait(false)).InPeriod(period);
        var isPermitTarget = permitTargets is null
            ? (_ => false)
            : await PermitTargets.LoadAsync(permitTargets, ct).ConfigureAwait(false);
        var tables = header.Snapshot.Sheets.SelectMany(s => s.Tables)
            .Where(t => readable.CanReadTable(t.Id)
                        && !t.Columns.Any(c => !c.IsDeleted && c.DataType == CellDataType.Lookup
                                               && c.LookupRegistryDefId is { } target && isPermitTarget(target)))
            .ToDictionary(t => t.Id);

        var instances = (await rows.GetTableInstancesAsync(documentId, period, ct).ConfigureAwait(false))
            .Where(i => tables.ContainsKey(i.TableDefId))
            .ToList();
        if (instances.Count == 0)
        {
            return [];
        }

        var ids = instances.ConvertAll(i => i.TableInstanceId);
        var slices = await cells.ReadSlicesAsync(ids, period, ct).ConfigureAwait(false);
        var keys = await rows.GetRowIdsBatchAsync(ids, period, ct).ConfigureAwait(false);
        var month = new DateOnly(request.ValidFrom.Year, request.ValidFrom.Month, 1);
        var found = new List<string>();

        foreach (var instance in instances.OrderBy(i => tables[i.TableDefId].Code, StringComparer.Ordinal))
        {
            if (!slices.TryGetValue(instance.TableInstanceId, out var slice))
            {
                continue;
            }

            var table = tables[instance.TableDefId];
            var keyOf = keys.TryGetValue(instance.TableInstanceId, out var map)
                ? map.ToDictionary(p => p.Value, p => p.Key)
                : [];
            foreach (var rowCells in slice.GroupBy(c => c.Address.TableRowId).OrderBy(g => keyOf.GetValueOrDefault(g.Key), StringComparer.Ordinal))
            {
                var byColumn = rowCells.ToDictionary(c => c.Address.ColumnDefId, c => c.Value);
                if (PermitHistoryReader.DataCellsInMonth(table, period, byColumn, new HashSet<int>(), month).Count > 0)
                {
                    found.Add($"{table.Code} · {keyOf.GetValueOrDefault(rowCells.Key) ?? rowCells.Key.ToString(CultureInfo.InvariantCulture)}");
                }
            }
        }

        return found;
    }
}

/// <summary>Undoes the latest permit added to a header field.</summary>
public sealed class UndoHeaderPermitHandler(
    IPermitHistoryStore store,
    IDocumentStore documents,
    IDocumentHeaderStore headers,
    IMetadataCache metadata,
    IPeriodStore periods,
    IRegistryStore registries,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    IPermitRegistryIndex? permitTargets = null)
{
    /// <summary>Removes the latest permit and returns the field's history.</summary>
    public async Task<HeaderPermitHistoryDto> HandleAsync(long documentId, UndoHeaderPermitRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = await PermitHistoryHeader.ResolveAsync(
            documentId, request.FieldCode, documents, headers, metadata, periods, access, currentUser, permitTargets, ct)
            .ConfigureAwait(false);

        var segments = await store.HeaderSegmentsAsync(documentId, header.Field.Id, ct).ConfigureAwait(false);
        if (segments.Count == 0)
        {
            throw PermitHistoryRules.NothingToUndo();
        }

        var last = segments[^1];
        var previousId = segments.Count > 1 ? segments[^2].RegistryEntryId : header.BaseEntryId;
        var entries = (await registries.FindEntriesByIdsAsync([previousId, last.RegistryEntryId], ct).ConfigureAwait(false))
            .ToDictionary(e => e.Id);

        var now = clock.UtcNow;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            store.Remove(last);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            await audit.WriteStructureChangeAsync(
                PermitHistoryRules.AuditRecord(
                    last, "Remove", header.TemplateVersionId, entries.GetValueOrDefault(previousId),
                    entries.GetValueOrDefault(last.RegistryEntryId), tableCode: null, header.Field.Code,
                    header.Profile.UserId, currentUser.CorrelationId, now),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return await PermitHistoryReader.HeaderAsync(store, registries, documentId, header.Field, header.BaseEntryId, currentUser.Language, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The permit history of a document (header fields and rows the reader can see) and the cells that still need a split
/// (j2/jh2 counts). Rows are read in <c>periodKey</c> (their month cells as the grid shows them).
/// </summary>
public sealed class GetPermitHistoryHandler(
    IPermitHistoryStore store,
    IRowStore rows,
    ICellStore cells,
    IDocumentStore documents,
    IDocumentHeaderStore headers,
    IMetadataCache metadata,
    IRegistryStore registries,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>The history.</summary>
    public async Task<DocumentPermitHistoryDto> HandleAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var profile = await Periods.OpenDocumentPeriodReopenHandler.ProfileAsync(access, currentUser, ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        var segments = await store.ListAsync(documentId, ct).ConfigureAwait(false);
        if (segments.Count == 0)
        {
            return new DocumentPermitHistoryDto([], [], 0);
        }

        var language = currentUser.Language;
        var period = new PeriodKey(periodKey);
        var snapshot = await metadata.GetAsync(
            await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        var readable = (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(period);

        // Header fields with history.
        var headerValues = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        var headerItems = new List<(HeaderFieldDef Field, long BaseId, List<PermitSegmentRecord> Segments)>();
        foreach (var group in segments.Where(s => s.Segment.HeaderFieldDefId is not null).GroupBy(s => s.Segment.HeaderFieldDefId!.Value))
        {
            var field = snapshot.HeaderFields.FirstOrDefault(f => f.Id == group.Key && !f.IsDeleted);
            if (field is not null && headerValues.TryGetValue(field.Id, out var value) && value.ValueRegistryEntryId is { } baseId)
            {
                headerItems.Add((field, baseId, group.ToList()));
            }
        }

        // Rows with history, visible to the reader, present in the period.
        var tables = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);
        var rowGroups = segments
            .Where(s => s.Segment.TableDefId is { } t && tables.ContainsKey(t)
                        && readable.CanReadTable(t) && readable.CanReadColumn(s.Segment.ColumnDefId!.Value))
            .GroupBy(s => (s.Segment.TableDefId!.Value, s.Segment.SheetCopyId, s.Segment.RowKey!, s.Segment.ColumnDefId!.Value))
            .ToList();

        var instances = rowGroups.Count == 0
            ? []
            : (await rows.GetTableInstancesAsync(documentId, period, ct).ConfigureAwait(false))
                .Where(i => rowGroups.Any(g => g.Key.Item1 == i.TableDefId && g.Key.SheetCopyId == i.SheetCopyId))
                .ToList();
        var ids = instances.ConvertAll(i => i.TableInstanceId);
        var slices = ids.Count == 0 ? new Dictionary<long, IReadOnlyList<CellRecord>>() : await cells.ReadSlicesAsync(ids, period, ct).ConfigureAwait(false);
        var keys = ids.Count == 0
            ? new Dictionary<long, IReadOnlyDictionary<string, long>>()
            : await rows.GetRowIdsBatchAsync(ids, period, ct).ConfigureAwait(false);

        var rowTargets = new List<PermitHistoryReader.RowTarget>();
        foreach (var group in rowGroups)
        {
            var (tableDefId, sheetCopyId, rowKey, columnDefId) = group.Key;
            var instance = instances.FirstOrDefault(i => i.TableDefId == tableDefId && i.SheetCopyId == sheetCopyId);
            if (instance is null || !keys.TryGetValue(instance.TableInstanceId, out var map) || !map.TryGetValue(rowKey, out var rowId))
            {
                continue;
            }

            var rowCells = (slices.GetValueOrDefault(instance.TableInstanceId) ?? [])
                .Where(c => c.Address.TableRowId == rowId)
                .ToDictionary(c => c.Address.ColumnDefId, c => c.Value);
            if (rowCells.GetValueOrDefault(columnDefId)?.ValueRegistryEntryId is not { } baseId)
            {
                continue;
            }

            var table = tables[tableDefId];
            var column = table.Columns.First(c => c.Id == columnDefId);
            rowTargets.Add(new PermitHistoryReader.RowTarget(
                table, column, sheetCopyId, rowKey, period, baseId, rowCells, [.. group.Select(g => g.Segment)],
                group.ToDictionary(g => g.Segment.Id, g => g.AddedBy)));
        }

        var entryIds = headerItems.SelectMany(h => h.Segments.Select(s => s.Segment.RegistryEntryId).Append(h.BaseId))
            .Concat(rowTargets.SelectMany(r => r.Segments.Select(s => s.RegistryEntryId).Append(r.BaseEntryId)))
            .Distinct()
            .ToList();
        var entries = (await registries.FindEntriesByIdsAsync(entryIds, ct).ConfigureAwait(false)).ToDictionary(e => e.Id);

        var months = rowTargets.SelectMany(r => PermitTimeline.SharedMonths(r.Spans))
            .Select(m => (m.Year * 100) + m.Month)
            .Distinct()
            .ToList();
        var parts = await store.PartsAsync(documentId, months, ct).ConfigureAwait(false);

        var headerDtos = headerItems
            .OrderBy(h => h.Field.Code, StringComparer.Ordinal)
            .Select(h => new HeaderPermitHistoryDto(
                h.Field.Code,
                PermitHistoryReader.Items(
                    PermitTimeline.Spans(h.BaseId, h.Segments.Select(s => (s.Segment.ValidFrom, s.Segment.RegistryEntryId))),
                    [.. h.Segments.Select(s => s.Segment)], h.Segments.ToDictionary(s => s.Segment.Id, s => s.AddedBy), entries, language)))
            .ToList();

        var rowDtos = rowTargets
            .OrderBy(r => r.Table.Code, StringComparer.Ordinal).ThenBy(r => r.RowKey, StringComparer.Ordinal)
            .Select(r => PermitHistoryReader.RowDto(r, entries, parts, language))
            .ToList();

        return new DocumentPermitHistoryDto(headerDtos, rowDtos, rowDtos.Sum(r => r.SplitMonths.Sum(m => m.ColumnCodes.Count)));
    }
}
