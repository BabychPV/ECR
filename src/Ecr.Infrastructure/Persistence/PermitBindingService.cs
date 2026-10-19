// src/Ecr.Infrastructure/Persistence/PermitBindingService.cs
using System.Globalization;
using Ecr.Application.Documents;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Рантайм прив'язок аркуша до дозволів (Permits, PM-10): створює рядки <c>P&lt;id&gt;</c> і заблоковані значення
/// таблиць документа з дозволу.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Заблоковані колонки (лише для читання) не пройдуть через <c>PatchCells</c> - там <c>ColumnIsReadOnly</c> відхиляє
/// всіх. Тому значення пишуться напряму через <see cref="ICellStore.ApplyAsync"/> і <see cref="IAuditWriter"/> з
/// походженням <see cref="CellChangeOrigins.Permit"/> від системного користувача, за зразком
/// <c>RecalculationService</c>. Це машинне походження: рядок з таким значенням ручно не видаляється
/// (<c>RowStore.HasMachineOriginAsync</c>).
/// </para>
/// <para>
/// ⚠ Усе - у транзакції й під спільним замком <c>doc-structure</c> викликача (<c>RowStore.EnsureTableInstancesAsync</c>):
/// рядки створюються лише для щойно створених екземплярів, тож паралельне друге відкриття програє на
/// <c>UQ_TableInstance</c> ДО цього коду, а не дублює рядки.
/// </para>
/// </remarks>
public sealed partial class PermitBindingService(
    EcrDbContext db,
    BulkCellLoader bulk,
    IPermitBindingStore bindings,
    IPermitTypeStore types,
    IPermitResolver resolver,
    IRegistrySnapshotLoader snapshots,
    IDocumentHeaderStore headers,
    IRegistryStore registries,
    IPeriodStore periods,
    ICellStore cells,
    IAuditWriter audit,
    IClock clock,
    ILogger<PermitBindingService>? log = null)
    : IPermitBindingService
{
    /// <summary>Системний користувач як автор змін від дозволу (як у перерахунку).</summary>
    public const int SystemUserId = 0;

    /// <inheritdoc />
    public async Task<PermitMaterialization> MaterializeAsync(
        long documentId, PeriodKey periodKey, IReadOnlyCollection<int> createdTableDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(createdTableDefIds);

        // Охоронець: таблиці без прив'язки (Land та решта) платять одним дешевим запитом і не торкаються нічого іншого.
        if (createdTableDefIds.Count == 0
            || !await bindings.AnyActiveRowsAsync(createdTableDefIds, ct).ConfigureAwait(false))
        {
            return PermitMaterialization.None;
        }

        var views = await bindings.ListActiveRowsAsync(createdTableDefIds, ct).ConfigureAwait(false);
        var warnings = new List<PermitBindingIssue>();
        var created = 0;

        var bounds = await periods.FindPeriodBoundsAsync(documentId, periodKey.Value, ct).ConfigureAwait(false);
        if (bounds is null)
        {
            warnings.Add(Warning("binding.warning.periodUnknown", "Межі періоду невідомі: рядки з дозволу не створено."));
            return Finish(documentId, 0, warnings);
        }

        var instanceIds = await db.TableInstances
            .AsNoTracking()
            .Where(t => t.PeriodKeyValue == periodKey.Value && t.DocumentId == documentId && createdTableDefIds.Contains(t.TableDefId))
            .ToDictionaryAsync(t => t.TableDefId, t => t.Id, ct)
            .ConfigureAwait(false);

        // Знімок довідників - раз на виклик (за набором довідників і датою).
        var cache = new Dictionary<string, IRegistrySnapshot>(StringComparer.Ordinal);

        foreach (var view in views)
        {
            if (!instanceIds.TryGetValue(view.Binding.SheetTableDefId, out var instanceId))
            {
                continue;
            }

            PermitBindingPlan plan;
            try
            {
                plan = await PlanCoreAsync(view, documentId, bounds.PeriodEnd, cache, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ⛔ Збій читання дозволу не ламає відкриття документа: екземпляр лишається порожнім із попередженням.
                if (log is not null)
                {
                    LogPlanFailed(log, ex, view.Binding.Code, documentId);
                }

                warnings.Add(Warning(
                    "binding.warning.planFailed", $"Прив'язку «{view.Binding.Code}» не розраховано: {ex.GetType().Name}."));
                continue;
            }

            warnings.AddRange(plan.Warnings);
            if (plan.Resolution.EntryId is null)
            {
                warnings.Add(Warning(
                    "binding.warning.noPermit",
                    $"Дозволу для прив'язки «{view.Binding.Code}» немає ({plan.Resolution.ReasonIfNone}): рядків не створено."));
                continue;
            }

            created += await InsertRowsAsync(
                    documentId, periodKey, instanceId, view, plan.Rows, new HashSet<string>(StringComparer.Ordinal), warnings, changed: null, ct)
                .ConfigureAwait(false);
        }

        return Finish(documentId, created, warnings);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permit binding {Code} of document {DocumentId} could not be planned.")]
    private static partial void LogPlanFailed(ILogger logger, Exception exception, string code, long documentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permit binding warning for document {DocumentId}: {Warning}")]
    private static partial void LogWarning(ILogger logger, long documentId, string warning);

    private PermitMaterialization Finish(long documentId, int created, List<PermitBindingIssue> warnings)
    {
        foreach (var warning in warnings)
        {
            if (log is not null)
            {
                LogWarning(log, documentId, warning.Text);
            }
        }

        return new PermitMaterialization(created, warnings);
    }

    /// <inheritdoc />
    public Task<PermitBindingPlan> PlanAsync(PermitBindingView view, long documentId, DateOnly periodEnd, CancellationToken ct)
        => PlanCoreAsync(view, documentId, periodEnd, new Dictionary<string, IRegistrySnapshot>(StringComparer.Ordinal), ct);

    private async Task<PermitBindingPlan> PlanCoreAsync(
        PermitBindingView view, long documentId, DateOnly periodEnd, Dictionary<string, IRegistrySnapshot> cache, CancellationToken ct)
    {
        var type = await types.FindByCodeAsync(view.PermitTypeCode, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Тип дозволу «{view.PermitTypeCode}» прив'язки «{view.Binding.Code}» не знайдено.");
        var table = (await types.ListTablesAsync(type.Id, ct).ConfigureAwait(false))
            .First(t => t.Id == view.Binding.PermitTypeTableId);

        var (facilityNames, headerEntryId) = await PermitDocumentSource
            .ReadAsync(headers, registries, documentId, view.Binding.SelectionRule, view.HeaderFieldCode, ct)
            .ConfigureAwait(false);

        return await PermitPlanBuilder
            .BuildAsync(view, type, table, new PermitPlanSource(periodEnd, facilityNames, headerEntryId), resolver, snapshots, cache, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Додає в екземпляр рядки плану, яких ще немає (за ключем), з заблокованими значеннями; повертає скільки додано.
    /// </summary>
    private async Task<int> InsertRowsAsync(
        long documentId,
        PeriodKey periodKey,
        long instanceId,
        PermitBindingView view,
        PermitRowPlan plan,
        HashSet<string> existingKeys,
        List<PermitBindingIssue> warnings,
        List<(long RowId, int ColumnId)>? changed,
        CancellationToken ct)
    {
        var fresh = plan.Rows.Where(r => !existingKeys.Contains(r.RowKey)).ToList();
        if (fresh.Count == 0)
        {
            return 0;
        }

        var tableDefId = view.Binding.SheetTableDefId;
        var max = await db.TableDefs.AsNoTracking()
            .Where(t => t.Id == tableDefId)
            .Select(t => t.MaxDynamicRows)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var live = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == periodKey.Value && r.TableInstanceId == instanceId)
            .Select(r => new { r.Ordinal })
            .ToListAsync(ct).ConfigureAwait(false);

        if (max is { } limit)
        {
            var room = Math.Max(0, limit - live.Count);
            if (fresh.Count > room)
            {
                warnings.Add(Warning(
                    "binding.warning.rowsClamped",
                    $"Дозвіл має {fresh.Count} нових рядків, а в таблиці вільно {room} із межі {limit}: решту не створено."));
                fresh = [.. fresh.Take(room)];
            }
        }

        if (fresh.Count == 0)
        {
            return 0;
        }

        var columns = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.TableDefId == tableDefId && !c.IsDeleted)
            .Select(c => new { c.Id, c.Code, c.DataType })
            .ToDictionaryAsync(c => c.Code, c => (c.Id, c.DataType), StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var now = clock.UtcNow;
        var firstId = await bulk.ReserveIdsAsync("doc.TableRowSeq", fresh.Count, ct).ConfigureAwait(false);
        var ordinal = live.Count == 0 ? 0 : live.Max(r => r.Ordinal);

        var upserts = new List<CellRecord>();
        var audits = new List<CellChangeRecord>();
        for (var i = 0; i < fresh.Count; i++)
        {
            var planned = fresh[i];
            var rowId = firstId + i;
            db.TableRows.Add(new TableRow(periodKey, rowId, instanceId, RowKey.Create(planned.RowKey), ++ordinal, now));

            foreach (var (code, value) in planned.Cells)
            {
                if (value is null || !columns.TryGetValue(code, out var column))
                {
                    continue;
                }

                var data = ToCellValue(column.DataType, value);
                if (data is null)
                {
                    warnings.Add(Warning(
                        "binding.warning.valueSkipped",
                        $"Значення колонки «{code}» рядка {planned.RowKey} типу {column.DataType} не записано: тип не підтримано."));
                    continue;
                }

                var address = new CellAddress(periodKey, rowId, column.Id);
                changed?.Add((rowId, column.Id));
                upserts.Add(new CellRecord(address, tableDefId, data));
                audits.Add(new CellChangeRecord(
                    now, address, documentId, planned.RowKey, OldValue: null, NewValue: Describe(data),
                    SystemUserId, CellChangeOrigins.Permit, IsLateEdit: false, CorrelationId: null));
            }
        }

        // Рядки мають бути в базі до комірок (FK) - у цій самій транзакції викликача.
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (upserts.Count > 0)
        {
            await cells.ApplyAsync(
                new CellChangeSet(instanceId, upserts, Deletes: [], TouchedRowIds: [], SystemUserId, IsLateEdit: false), ct)
                .ConfigureAwait(false);
            await audit.WriteCellChangesAsync(audits, ct).ConfigureAwait(false);
        }

        return fresh.Count;
    }

    /// <summary>Значення плану у значення комірки за типом колонки; <c>null</c> - тип не підтримано.</summary>
    internal static CellValueData? ToCellValue(CellDataType type, object value) => type switch
    {
        CellDataType.String => value is string s ? new CellValueData { ValueString = s } : null,
        CellDataType.Int or CellDataType.Decimal => value is decimal d ? new CellValueData { ValueNumeric = d } : null,
        CellDataType.Bool => value is bool b ? new CellValueData { ValueBool = b } : null,
        CellDataType.Date => value is DateOnly day ? new CellValueData { ValueDate = day.ToDateTime(TimeOnly.MinValue) } : null,
        CellDataType.Lookup => value is decimal id && id == decimal.Truncate(id)
            ? new CellValueData { ValueRegistryEntryId = (long)id }
            : null,
        _ => null,
    };

    private static string? Describe(CellValueData v)
        => v.IsEmpty ? string.Empty
         : v.ValueNumeric?.ToString(CultureInfo.InvariantCulture)
           ?? v.ValueString
           ?? v.ValueBool?.ToString()
           ?? v.ValueDate?.ToString("O", CultureInfo.InvariantCulture)
           ?? v.ValueRegistryEntryId?.ToString(CultureInfo.InvariantCulture)
           ?? v.ValueUnitId?.ToString(CultureInfo.InvariantCulture);

    private static PermitBindingIssue Warning(string key, string text)
        => new(key, new Dictionary<string, string>(), text);
}
