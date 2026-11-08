// src/Ecr.Application/Validation/PermitCoverageIssues.cs
using Ecr.Application.Permits.Validity;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Validation;

/// <summary>
/// Rule (h4), human 08.11: EXISTING violations of «permit validity covers the months with data» are listed in Issues
/// («Validate») with the reason and the fix — rows whose permit misses a month with data, and a header permit that misses
/// a month the document holds data in.
/// </summary>
/// <remarks>
/// <para>
/// Same rule as the write path (<see cref="PermitMonthCoverage"/>, <see cref="PermitTargets"/>), read on the LOGICAL slice
/// (<c>ICellStore.ReadSlicesAsync</c>: with Q-17 routing the month cells come from their owner instances).
/// </para>
/// <para>
/// Severity <c>Error</c>: the data contradicts the permit and must be fixed. Months go to the parameters as <c>01.2026</c>
/// — the text is assembled in the READER's language on reading (<see cref="ValidationMessageTemplates.Localize"/>).
/// </para>
/// <para>
/// ⚠ Cost: nothing unless the document has a permit-type column or header field (in-memory test,
/// <see cref="IPermitRegistryIndex"/>); then one batched slice read of the permit tables only, and one entries query.
/// </para>
/// </remarks>
public static class PermitCoverageIssues
{
    /// <summary>Rule code of a row finding.</summary>
    public const string RowRuleCode = "PERMIT-VALIDITY";

    /// <summary>Runs the check over the period's original instances and the header.</summary>
    /// <param name="permitTargets">Permit-type Lookup targets; <c>null</c> — the rule is off.</param>
    /// <param name="registries">Registry entries.</param>
    /// <param name="cells">Cell store (logical slices).</param>
    /// <param name="rows">Row store.</param>
    /// <param name="periods">Period bounds.</param>
    /// <param name="headers">Header values.</param>
    /// <param name="dataMonths">Document months with data (header permit); <c>null</c> — header not checked.</param>
    /// <param name="snapshot">Template version of the document.</param>
    /// <param name="documentId">Document.</param>
    /// <param name="instances">Original table instances of the period.</param>
    /// <param name="periodKey">Period.</param>
    /// <param name="language">Language of the fallback text.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<IReadOnlyList<ValidationMessage>> RunAsync(
        IPermitRegistryIndex? permitTargets,
        IRegistryStore registries,
        ICellStore cells,
        IRowStore rows,
        IPeriodStore periods,
        IDocumentHeaderStore headers,
        IPermitDataMonthsReader? dataMonths,
        TemplateVersionSnapshot snapshot,
        long documentId,
        IReadOnlyList<TableInstanceRef> instances,
        PeriodKey periodKey,
        string language,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(instances);

        var tables = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);
        var anyLookup = tables.Values.Any(t => t.Columns.Any(c => !c.IsDeleted && c.DataType == CellDataType.Lookup))
                        || snapshot.HeaderFields.Any(f => !f.IsDeleted && f.LookupRegistryDefId is not null);
        if (permitTargets is null || !anyLookup)
        {
            return [];
        }

        var isPermitTarget = await PermitTargets.LoadAsync(permitTargets, ct).ConfigureAwait(false);

        var permitColumnsOf = tables.Values
            .Select(t => (Table: t, Columns: t.Columns
                .Where(c => !c.IsDeleted && c.DataType == CellDataType.Lookup && c.LookupRegistryDefId is { } target && isPermitTarget(target))
                .ToList()))
            .Where(x => x.Columns.Count > 0)
            .ToDictionary(x => x.Table.Id, x => x.Columns);

        var messages = new List<ValidationMessage>();
        messages.AddRange(await RowFindingsAsync(
                registries, cells, rows, periods, tables, permitColumnsOf, documentId, instances, periodKey, language, ct)
            .ConfigureAwait(false));

        if (dataMonths is not null)
        {
            messages.AddRange(await HeaderFindingsAsync(
                    registries, headers, dataMonths, snapshot, isPermitTarget, [.. permitColumnsOf.Keys], documentId, language, ct)
                .ConfigureAwait(false));
        }

        return messages;
    }

    private static async Task<List<ValidationMessage>> RowFindingsAsync(
        IRegistryStore registries,
        ICellStore cells,
        IRowStore rows,
        IPeriodStore periods,
        Dictionary<int, TableDef> tables,
        Dictionary<int, List<ColumnDef>> permitColumnsOf,
        long documentId,
        IReadOnlyList<TableInstanceRef> instances,
        PeriodKey periodKey,
        string language,
        CancellationToken ct)
    {
        var permitInstances = instances.Where(i => permitColumnsOf.ContainsKey(i.TableDefId)).ToList();
        if (permitInstances.Count == 0)
        {
            return [];
        }

        var ids = permitInstances.ConvertAll(i => i.TableInstanceId);
        var slices = await cells.ReadSlicesAsync(ids, periodKey, ct).ConfigureAwait(false);
        var rowIdsByInstance = await rows.GetRowIdsBatchAsync(ids, periodKey, ct).ConfigureAwait(false);

        // Rows with a permit: instance → row → its cells.
        var byRow = new List<(TableInstanceRef Instance, long RowId, Dictionary<int, CellValueData> Cells)>();
        foreach (var instance in permitInstances)
        {
            if (!slices.TryGetValue(instance.TableInstanceId, out var slice))
            {
                continue;
            }

            var permitIds = permitColumnsOf[instance.TableDefId].Select(c => c.Id).ToHashSet();
            foreach (var row in slice.GroupBy(c => c.Address.TableRowId))
            {
                var rowCells = row.ToDictionary(c => c.Address.ColumnDefId, c => c.Value);
                if (rowCells.Any(p => permitIds.Contains(p.Key) && p.Value.ValueRegistryEntryId is not null))
                {
                    byRow.Add((instance, row.Key, rowCells));
                }
            }
        }

        if (byRow.Count == 0)
        {
            return [];
        }

        var entryIds = byRow
            .SelectMany(r => r.Cells.Values.Select(v => v.ValueRegistryEntryId))
            .OfType<long>()
            .Distinct()
            .ToList();
        var entries = (await registries.FindEntriesByIdsAsync(entryIds, ct).ConfigureAwait(false)).ToDictionary(e => e.Id);
        if (!entries.Values.Any(e => e.ValidFrom is not null || e.ValidTo is not null))
        {
            return [];
        }

        var bounds = await periods.FindPeriodBoundsAsync(documentId, periodKey.Value, ct).ConfigureAwait(false);
        var messages = new List<ValidationMessage>();

        foreach (var (instance, rowId, rowCells) in byRow)
        {
            var table = tables[instance.TableDefId];
            var monthOf = PermitMonthCoverage.MonthColumns(table).ToDictionary(c => c.Id, c => (int)c.MonthNumber!.Value);
            var permitColumns = permitColumnsOf[table.Id];
            var permitIds = permitColumns.Select(c => c.Id).ToHashSet();
            var periodMonths = bounds is { } b ? PermitMonthCoverage.MonthsOfPeriod(b.PeriodStart, b.PeriodEnd) : [];
            var months = PermitMonthCoverage.RowMonthsWithData(monthOf, periodKey.Year, rowCells, permitIds, periodMonths);
            var calculatedOnly = PermitMonthCoverage.RowCalculatedOnlyMonths(monthOf, periodKey.Year, rowCells, permitIds, periodMonths);
            if (months.Count == 0 && calculatedOnly.Count == 0)
            {
                continue;
            }

            var rowKey = rowIdsByInstance.TryGetValue(instance.TableInstanceId, out var keys)
                ? keys.FirstOrDefault(p => p.Value == rowId).Key
                : null;

            foreach (var column in permitColumns)
            {
                if (rowCells.GetValueOrDefault(column.Id)?.ValueRegistryEntryId is not { } entryId
                    || !entries.TryGetValue(entryId, out var entry))
                {
                    continue;
                }

                var uncovered = PermitMonthCoverage.Uncovered(entry.ValidFrom, entry.ValidTo, months);
                if (uncovered.Count > 0)
                {
                    messages.Add(RowMessage(table.Id, rowKey, column, entry, uncovered, language));
                }

                // Human 08.11 («Попередження», F13-p210): months with ONLY calculated values outside the permit — a Warning
                // at the permit cell; Save, the header and Submit are never blocked by them.
                var calculatedUncovered = PermitMonthCoverage.Uncovered(entry.ValidFrom, entry.ValidTo, calculatedOnly);
                if (calculatedUncovered.Count > 0)
                {
                    messages.Add(CalculatedMessage(table.Id, rowKey, column, entry, calculatedUncovered, language));
                }

                // Human 08.11: a permit starting/ending mid-month still covers the month (never blocked) — the partial
                // days are named in a non-blocking Warning at the month cell (or the permit cell without month columns).
                foreach (var month in months.Except(uncovered))
                {
                    if (PermitMonthCoverage.PartialDays(entry.ValidFrom, entry.ValidTo, month) is not { } days)
                    {
                        continue;
                    }

                    var monthColumn = table.Columns.FirstOrDefault(c => monthOf.TryGetValue(c.Id, out var m) && m == month.Month);
                    messages.Add(PartialMessage(table.Id, rowKey, column, monthColumn ?? column, entry, month, days, language));
                }
            }
        }

        return messages;
    }

    private static ValidationMessage PartialMessage(
        int tableDefId, string? rowKey, ColumnDef permitColumn, ColumnDef at, RegistryEntry entry, DateOnly month,
        (DateOnly First, DateOnly Last) days, string language)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["permit"] = entry.DisplayL10n.Get("en") is { Length: > 0 } shown ? shown : entry.Code,
            ["column"] = permitColumn.HeaderL10n.Get("en") ?? permitColumn.Code,
            ["month"] = month.ToString("MM.yyyy", System.Globalization.CultureInfo.InvariantCulture),
            ["days"] = PermitMonthCoverage.DescribeDays(days),
        };

        return new ValidationMessage(
            ValidationSeverity.Warning,
            RowRuleCode,
            ValidationMessageTemplates.Render(ValidationMessageTemplates.PermitPartialMonth, language, parameters),
            tableDefId,
            rowKey,
            at.Code,
            BlocksSave: false,
            MessageKey: ValidationMessageTemplates.PermitPartialMonth,
            Params: parameters);
    }

    private static async Task<List<ValidationMessage>> HeaderFindingsAsync(
        IRegistryStore registries,
        IDocumentHeaderStore headers,
        IPermitDataMonthsReader dataMonths,
        TemplateVersionSnapshot snapshot,
        Func<int, bool> isPermitTarget,
        IReadOnlyCollection<int> ownPermitTables,
        long documentId,
        string language,
        CancellationToken ct)
    {
        var fields = snapshot.HeaderFields
            .Where(f => !f.IsDeleted && f.LookupRegistryDefId is { } target && isPermitTarget(target))
            .ToList();
        if (fields.Count == 0)
        {
            return [];
        }

        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        var picked = fields
            .Where(f => values.TryGetValue(f.Id, out var v) && v.ValueRegistryEntryId is not null)
            .Select(f => (Field: f, EntryId: values[f.Id].ValueRegistryEntryId!.Value))
            .ToList();
        if (picked.Count == 0)
        {
            return [];
        }

        var entries = (await registries.FindEntriesByIdsAsync([.. picked.Select(p => p.EntryId).Distinct()], ct)
                .ConfigureAwait(false))
            .ToDictionary(e => e.Id);
        var windowed = picked
            .Where(p => entries.TryGetValue(p.EntryId, out var e) && (e.ValidFrom is not null || e.ValidTo is not null))
            .ToList();
        if (windowed.Count == 0)
        {
            return [];
        }

        var byKind = await dataMonths.DocumentMonthsByKindAsync(documentId, ownPermitTables, ct).ConfigureAwait(false);
        var messages = new List<ValidationMessage>();

        foreach (var (field, entryId) in windowed.OrderBy(p => p.Field.Code, StringComparer.Ordinal))
        {
            var entry = entries[entryId];

            // Human 08.11 («Попередження»): months of the covered tables with ONLY calculated values outside the permit — a
            // Warning at the header field; the header save (ECR-HDR-4223) and Submit are never blocked by them.
            var calculatedUncovered = PermitMonthCoverage.Uncovered(entry.ValidFrom, entry.ValidTo, byKind.CalculatedOnly);
            if (calculatedUncovered.Count > 0)
            {
                messages.Add(HeaderCalculatedMessage(field.Code, entry, calculatedUncovered, language));
            }

            var uncovered = PermitMonthCoverage.Uncovered(entry.ValidFrom, entry.ValidTo, byKind.WithData);
            if (uncovered.Count == 0)
            {
                continue;
            }

            var parameters = Parameters(entry, uncovered, field.Code);
            messages.Add(new ValidationMessage(
                ValidationSeverity.Error,
                ErrorCodes.HeaderValueInvalid,
                ValidationMessageTemplates.Render(ValidationMessageTemplates.PermitHeaderMonthsNotCovered, language, parameters),
                TableDefId: 0,
                RowKey: null,
                ColumnCode: null,
                BlocksSave: false,
                MessageKey: ValidationMessageTemplates.PermitHeaderMonthsNotCovered,
                Params: parameters));
        }

        return messages;
    }

    private static ValidationMessage RowMessage(
        int tableDefId, string? rowKey, ColumnDef column, RegistryEntry entry, IReadOnlyList<DateOnly> uncovered, string language)
    {
        // ⚠ Stored parameters are read in any language: the column by its default (en) header, as the permit number.
        var parameters = Parameters(entry, uncovered, column.HeaderL10n.Get("en") ?? column.Code);
        return new ValidationMessage(
            ValidationSeverity.Error,
            RowRuleCode,
            ValidationMessageTemplates.Render(ValidationMessageTemplates.PermitMonthsNotCovered, language, parameters),
            tableDefId,
            rowKey,
            column.Code,
            BlocksSave: false,
            MessageKey: ValidationMessageTemplates.PermitMonthsNotCovered,
            Params: parameters);
    }

    private static ValidationMessage CalculatedMessage(
        int tableDefId, string? rowKey, ColumnDef column, RegistryEntry entry, IReadOnlyList<DateOnly> uncovered, string language)
    {
        var parameters = Parameters(entry, uncovered, column.HeaderL10n.Get("en") ?? column.Code);
        var key = uncovered.Count == 1
            ? ValidationMessageTemplates.PermitCalculatedNotCoveringOne
            : ValidationMessageTemplates.PermitCalculatedNotCovering;
        return new ValidationMessage(
            ValidationSeverity.Warning,
            RowRuleCode,
            ValidationMessageTemplates.Render(key, language, parameters),
            tableDefId,
            rowKey,
            column.Code,
            BlocksSave: false,
            MessageKey: key,
            Params: parameters);
    }

    private static ValidationMessage HeaderCalculatedMessage(
        string fieldCode, RegistryEntry entry, IReadOnlyList<DateOnly> uncovered, string language)
    {
        var parameters = Parameters(entry, uncovered, fieldCode);
        var key = uncovered.Count == 1
            ? ValidationMessageTemplates.PermitHeaderCalculatedNotCoveringOne
            : ValidationMessageTemplates.PermitHeaderCalculatedNotCovering;
        return new ValidationMessage(
            ValidationSeverity.Warning,
            ErrorCodes.HeaderValueInvalid,
            ValidationMessageTemplates.Render(key, language, parameters),
            TableDefId: 0,
            RowKey: null,
            ColumnCode: null,
            BlocksSave: false,
            MessageKey: key,
            Params: parameters);
    }

    private static Dictionary<string, string> Parameters(RegistryEntry entry, IReadOnlyList<DateOnly> uncovered, string column)
        => new(StringComparer.Ordinal)
        {
            // ⚠ Permit number in the default language: the parameters are stored and shown to readers of any language.
            ["permit"] = entry.DisplayL10n.Get("en") is { Length: > 0 } shown ? shown : entry.Code,
            ["validity"] = PermitMonthCoverage.DescribeValidity(entry.ValidFrom, entry.ValidTo),
            ["months"] = PermitMonthCoverage.DescribeMonthsNumeric(uncovered),
            ["column"] = column,
        };
}
