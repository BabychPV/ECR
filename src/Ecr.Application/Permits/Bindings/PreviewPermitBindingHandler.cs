// src/Ecr.Application/Permits/Bindings/PreviewPermitBindingHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Параметри перегляду прив'язки.</summary>
/// <param name="DocumentId">Документ: з нього беруться об'єкт, поле шапки й фактичні комірки. Необов'язковий.</param>
/// <param name="PeriodKey">Період документа (<c>РРРРММ</c>); обов'язковий разом із документом.</param>
/// <param name="FacilityName">Об'єкт вручну (<c>ByFacilityPeriodEnd</c>); перекриває об'єкт документа.</param>
/// <param name="PeriodEnd">Кінець періоду вручну; без нього - кінець періоду документа, а без документа - сьогодні.</param>
/// <param name="PermitEntryId">Дозвіл вручну (<c>FromHeaderLookup</c>); перекриває поле шапки документа.</param>
public sealed record PermitPreviewQuery(
    long? DocumentId, int? PeriodKey, string? FacilityName, DateOnly? PeriodEnd, long? PermitEntryId);

/// <summary>
/// Перегляд прив'язки очима користувача: який дозвіл обрано, які рядки створить прив'язка, які колонки заблоковані
/// (Permits, PM-09). Нічого не пише.
/// </summary>
/// <remarks>
/// <para>
/// Звідки береться дозвіл: з документа (<c>documentId</c> + <c>periodKey</c>) - об'єкт з поля шапки
/// <see cref="PermitBindingFacility.HeaderFieldCode"/> або дозвіл з поля шапки прив'язки; без документа - з
/// <c>facilityName</c> / <c>permitEntryId</c> і <c>periodEnd</c> (типово сьогодні). Явні параметри перекривають документ.
/// Дозвіл не знайдено - це відповідь <c>200</c> з <c>permit.reasonIfNone</c>, а не помилка: саме це і треба показати.
/// </para>
/// <para>
/// ⚠ Рядки будує той самий <see cref="PermitRowPlanner"/> над тим самим знімком довідників, що й рантайм (PM-10), тож
/// перегляд не розійдеться з тим, що створить документ. Фактичні комірки існуючого екземпляра накладаються лише на
/// незіставлені колонки.
/// </para>
/// </remarks>
public sealed class PreviewPermitBindingHandler(
    IRepository<TemplateVersion, int> versions,
    IPermitBindingStore bindings,
    IPermitTypeStore permits,
    IRegistryStore registries,
    ITemplateVersionStore store,
    IPermitResolver resolver,
    IRegistrySnapshotLoader snapshots,
    IDocumentStore documents,
    IDocumentHeaderStore headers,
    IPeriodStore periods,
    IRowStore rows,
    ICellStore cells,
    IUnitStore units,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на читання прив'язок (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.View";

    /// <summary>Право на перегляд документа, якщо перегляд будується з нього.</summary>
    public const string DocumentPermission = "Document.View";

    /// <summary>Будує перегляд.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="code">Код прив'язки.</param>
    /// <param name="query">Параметри.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії, прив'язки чи документа немає.</exception>
    public async Task<PermitBindingPreviewDto> HandleAsync(
        int templateVersionId, string code, PermitPreviewQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        _ = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.VersionNotFound(templateVersionId);
        var view = await bindings.GetAsync(templateVersionId, code, ct).ConfigureAwait(false)
            ?? throw PermitBindingErrors.NotFound(code, templateVersionId);
        var binding = view.Binding;

        var warnings = new List<PermitBindingIssue>();
        if (!binding.IsActive)
        {
            warnings.Add(Warning("binding.warning.inactive", "Прив'язка вимкнена: документи її не використовують."));
        }

        var type = (await permits.FindByCodeAsync(view.PermitTypeCode, ct).ConfigureAwait(false))!;
        var table = (await permits.ListTablesAsync(type.Id, ct).ConfigureAwait(false)).First(t => t.Id == binding.PermitTypeTableId);

        var structure = await store.GetWithStructureAsync(templateVersionId, ct).ConfigureAwait(false);
        var sheetTable = structure.Sheets.SelectMany(s => s.Tables).First(t => t.Id == binding.SheetTableDefId);

        // 1. Звідки дані: документ → шапка й період; явні параметри перекривають.
        var source = await ReadSourceAsync(templateVersionId, view, query, warnings, ct).ConfigureAwait(false);

        // 2. Який дозвіл.
        var resolution = await resolver.ResolveAsync(
            new PermitResolveRequest(
                type.HeaderRegistryDefId, binding.SelectionRule, source.PeriodEnd, source.FacilityNames, source.HeaderEntryId),
            ct).ConfigureAwait(false);
        warnings.AddRange(resolution.Warnings);

        // 3. Які рядки: той самий планувальник і знімок, що й рантайм.
        var plan = new PermitRowPlan([], []);
        if (binding.Mode == PermitBindingMode.Rows && resolution.EntryId is { } permitId)
        {
            var snapshot = await snapshots
                .LoadAsync([type.HeaderRegistryDefId, table.RegistryDefId], source.PeriodEnd, null, ct)
                .ConfigureAwait(false);
            plan = PermitRowPlanner.Plan(
                snapshot,
                PermitHeaderFields.TableRegistryCode(type.Code, table.Code),
                permitId,
                [.. view.Columns.Select(c => new PlannerColumn(ColumnCode(view, c.SheetColumnDefId), c.PermitFieldCode, c.Role))]);
            warnings.AddRange(plan.Warnings);
        }
        else if (binding.Mode == PermitBindingMode.Formulas)
        {
            warnings.Add(Warning("binding.warning.formulasMode", "У режимі формул рядки не створюються: аркуш читає дозвіл формулами."));
        }

        var columns = await ColumnsAsync(view, sheetTable, ct).ConfigureAwait(false);
        var previewRows = await OverlayFactsAsync(plan.Rows, sheetTable, source, query, ct).ConfigureAwait(false);

        return new PermitBindingPreviewDto(
            PermitBindingMapper.Map(view),
            new PermitPreviewPermitDto(resolution.EntryId, resolution.Number, resolution.ValidFrom, resolution.ValidTo, resolution.ReasonIfNone),
            columns,
            previewRows,
            [.. warnings.DistinctBy(w => w.MessageKey + w.Text).Select(w => new PermitPreviewWarningDto(w.Case, w.Text))]);
    }

    private sealed record Source(DateOnly PeriodEnd, IReadOnlyList<string> FacilityNames, long? HeaderEntryId, bool HasDocument);

    private async Task<Source> ReadSourceAsync(
        int templateVersionId, PermitBindingView view, PermitPreviewQuery query, List<PermitBindingIssue> warnings, CancellationToken ct)
    {
        var periodEnd = query.PeriodEnd;
        var facilityNames = new List<string>();
        long? headerEntryId = null;

        if (query.DocumentId is { } documentId)
        {
            var profile = await PermissionCheck.RequireInAnyProjectAsync(access, currentUser, DocumentPermission, ct).ConfigureAwait(false);
            await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, DocumentPermission, ct).ConfigureAwait(false);

            var periodKey = query.PeriodKey ?? throw PermitBindingErrors.PreviewPeriodRequired();
            var bounds = await periods.FindPeriodBoundsAsync(documentId, periodKey, ct).ConfigureAwait(false)
                ?? throw PermitBindingErrors.PreviewPeriodUnknown(periodKey);
            periodEnd ??= bounds.PeriodEnd;

            if (await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false) != templateVersionId)
            {
                warnings.Add(Warning(
                    "binding.warning.documentOtherVersion",
                    "Документ належить іншій версії шаблону: фактичні комірки не накладаються."));
            }

            var header = await headers.GetExpressionValuesAsync(documentId, ct).ConfigureAwait(false);
            if (view.Binding.SelectionRule == PermitSelectionRule.FromHeaderLookup)
            {
                headerEntryId = LookupId(header, view.HeaderFieldCode);
            }
            else
            {
                facilityNames.AddRange(await FacilityNamesAsync(header, ct).ConfigureAwait(false));
            }
        }

        if (!string.IsNullOrWhiteSpace(query.FacilityName))
        {
            facilityNames = [query.FacilityName];
        }

        return new Source(
            periodEnd ?? DateOnly.FromDateTime(clock.UtcNow),
            facilityNames,
            query.PermitEntryId ?? headerEntryId,
            query.DocumentId is not null);
    }

    private static long? LookupId(IReadOnlyDictionary<string, ExpressionValue> header, string? fieldCode)
    {
        if (fieldCode is null)
        {
            return null;
        }

        var value = header.FirstOrDefault(p => string.Equals(p.Key, fieldCode, StringComparison.OrdinalIgnoreCase)).Value;
        return value.AsNumber() is { } n && n == decimal.Truncate(n) ? (long)n : null;
    }

    private async Task<IReadOnlyList<string>> FacilityNamesAsync(IReadOnlyDictionary<string, ExpressionValue> header, CancellationToken ct)
    {
        var value = header.FirstOrDefault(
            p => string.Equals(p.Key, PermitBindingFacility.HeaderFieldCode, StringComparison.OrdinalIgnoreCase)).Value;

        if (value.Type == Ecr.Expressions.Ast.ExpressionValueType.Text)
        {
            return [(string)value.Value!];
        }

        // Lookup: у документі лежить id запису довідника - порівнюється його код і назви.
        if (LookupId(header, PermitBindingFacility.HeaderFieldCode) is { } entryId
            && await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false) is { } entry)
        {
            return [entry.Code, .. entry.DisplayL10n.Values.Values];
        }

        return [];
    }

    private async Task<IReadOnlyList<PermitPreviewColumnDto>> ColumnsAsync(
        PermitBindingView view, TableDef sheetTable, CancellationToken ct)
    {
        var roles = view.Columns.ToDictionary(c => c.SheetColumnDefId, c => c.Role);
        var unitCodes = new Dictionary<int, string?>();
        var result = new List<PermitPreviewColumnDto>();

        foreach (var column in sheetTable.Columns.Where(c => !c.IsDeleted).OrderBy(c => c.Ordinal))
        {
            string? unit = null;
            if (column.UnitId is { } unitId)
            {
                if (!unitCodes.TryGetValue(unitId, out unit))
                {
                    unit = (await units.FindUnitByIdAsync(unitId, ct).ConfigureAwait(false))?.Code;
                    unitCodes[unitId] = unit;
                }
            }

            var mapped = roles.TryGetValue(column.Id, out var role);
            result.Add(new PermitPreviewColumnDto(
                column.Code,
                column.HeaderL10n.Get("en") ?? column.Code,
                mapped ? role.ToString() : column.DataType is CellDataType.Formula or CellDataType.Calculated ? "Formula" : "Fact",
                mapped,
                column.DataType.ToString(),
                unit));
        }

        return result;
    }

    private async Task<IReadOnlyList<PermitPreviewRowDto>> OverlayFactsAsync(
        IReadOnlyList<PlannedRow> planned, TableDef sheetTable, Source source, PermitPreviewQuery query, CancellationToken ct)
    {
        var result = planned
            .Select(r => new PermitPreviewRowDto(r.RowKey, new Dictionary<string, object?>(r.Cells, StringComparer.Ordinal), r.Locked))
            .ToList();

        if (!source.HasDocument || query.DocumentId is not { } documentId || query.PeriodKey is not { } periodValue || result.Count == 0)
        {
            return result;
        }

        var periodKey = PeriodKey.Parse(periodValue);
        var instance = (await rows.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false))
            .FirstOrDefault(i => i.TableDefId == sheetTable.Id);
        if (instance is null)
        {
            return result;
        }

        var rowIds = (await rows.GetRowsAsync(instance.TableInstanceId, periodKey, ct).ConfigureAwait(false))
            .ToDictionary(r => r.RowKey, r => r.Id, StringComparer.Ordinal);
        var slice = (await cells.ReadSliceAsync(instance.TableInstanceId, periodKey, ct).ConfigureAwait(false))
            .ToLookup(c => c.Address.TableRowId);
        var columnsById = sheetTable.Columns.Where(c => !c.IsDeleted).ToDictionary(c => c.Id);

        foreach (var row in result)
        {
            if (!rowIds.TryGetValue(row.RowKey, out var rowId))
            {
                continue;
            }

            foreach (var cell in slice[rowId])
            {
                // Накладаються лише незіставлені колонки: зіставлені пише машина з дозволу.
                if (columnsById.TryGetValue(cell.Address.ColumnDefId, out var column) && !row.Locked.Contains(column.Code))
                {
                    ((Dictionary<string, object?>)row.Cells)[column.Code] = FactValue(column.DataType, cell.Value);
                }
            }
        }

        return result;
    }

    private static object? FactValue(CellDataType type, CellValueData value) => type switch
    {
        CellDataType.String => value.ValueString,
        CellDataType.Int or CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated => value.ValueNumeric,
        CellDataType.Bool => value.ValueBool,
        CellDataType.Date => value.ValueDate is { } d ? DateOnly.FromDateTime(d) : null,
        CellDataType.Lookup => value.ValueRegistryEntryId,
        CellDataType.Unit => value.ValueUnitId,
        _ => null,
    };

    private static string ColumnCode(PermitBindingView view, int columnId)
        => view.SheetColumnCodes.GetValueOrDefault(columnId, columnId.ToString(CultureInfo.InvariantCulture));

    private static PermitBindingIssue Warning(string key, string text)
        => new(key, new Dictionary<string, string>(), text);
}
