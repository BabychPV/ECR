using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IPermitBindingStore"/> над <see cref="EcrDbContext"/> (Permits, PM-09).</summary>
public sealed class PermitBindingStore(EcrDbContext db) : IPermitBindingStore
{
    // Прив'язок на версію - одиниці (по одній на таблицю аркуша); стеля лише захищає від вибірки без меж.
    private const int MaxRows = 5_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitBindingView>> ListAsync(int templateVersionId, CancellationToken ct)
    {
        var bindings = await db.PermitSheetBindings
            .AsNoTracking()
            .Include(b => b.Columns)
            .Where(b => b.TemplateVersionId == templateVersionId)
            .OrderBy(b => b.Code)
            .Take(MaxRows)
            .ToListAsync(ct).ConfigureAwait(false);

        return await ViewsAsync(bindings, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PermitBindingView?> GetAsync(int templateVersionId, string code, CancellationToken ct)
    {
        var binding = await db.PermitSheetBindings
            .AsNoTracking()
            .Include(b => b.Columns)
            .FirstOrDefaultAsync(b => b.TemplateVersionId == templateVersionId && b.Code == code, ct)
            .ConfigureAwait(false);

        return binding is null ? null : (await ViewsAsync([binding], ct).ConfigureAwait(false))[0];
    }

    /// <inheritdoc />
    public Task<PermitSheetBinding?> FindForUpdateAsync(int templateVersionId, string code, CancellationToken ct)
        => db.PermitSheetBindings.FirstOrDefaultAsync(b => b.TemplateVersionId == templateVersionId && b.Code == code, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitSheetBindingColumn>> ListColumnsForUpdateAsync(int bindingId, CancellationToken ct)
        => await db.PermitSheetBindingColumns
            .Where(c => c.BindingId == bindingId)
            .OrderBy(c => c.Ordinal)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<string?> FindCodeBySheetTableAsync(int templateVersionId, int sheetTableDefId, CancellationToken ct)
        => await db.PermitSheetBindings
            .AsNoTracking()
            .Where(b => b.TemplateVersionId == templateVersionId && b.SheetTableDefId == sheetTableDefId)
            .Select(b => b.Code)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<bool> CodeExistsAsync(string code, CancellationToken ct)
        => db.PermitSheetBindings.AsNoTracking().AnyAsync(b => b.Code == code, ct);

    /// <inheritdoc />
    public async Task<int?> MaxPermitRowsAsync(int tableRegistryDefId, int compositionFieldDefId, CancellationToken ct)
    {
        // Рядок таблиці дозволу - живий запис її реєстру, чиє поле композиції вказує на дозвіл-батька.
        var perPermit = await db.RegistryValues
            .AsNoTracking()
            .Where(v => v.RegistryFieldDefId == compositionFieldDefId
                        && v.ValueRefEntryId != null
                        && db.RegistryEntries.Any(e => e.Id == v.RegistryEntryId
                                                       && e.RegistryDefId == tableRegistryDefId
                                                       && !e.IsDeleted))
            .GroupBy(v => v.ValueRefEntryId)
            .Select(g => g.Count())
            .ToListAsync(ct).ConfigureAwait(false);

        return perPermit.Count == 0 ? null : perPermit.Max();
    }

    /// <inheritdoc />
    public void Add(PermitSheetBinding binding) => db.PermitSheetBindings.Add(binding);

    /// <inheritdoc />
    public void Remove(PermitSheetBinding binding) => db.PermitSheetBindings.Remove(binding);

    /// <inheritdoc />
    public void RemoveColumns(IEnumerable<PermitSheetBindingColumn> columns)
        => db.PermitSheetBindingColumns.RemoveRange(columns);

    private async Task<IReadOnlyList<PermitBindingView>> ViewsAsync(List<PermitSheetBinding> bindings, CancellationToken ct)
    {
        if (bindings.Count == 0)
        {
            return [];
        }

        var sheetTables = bindings.Select(b => b.SheetTableDefId).Distinct().ToList();
        var typeIds = bindings.Select(b => b.PermitTypeDefId).Distinct().ToList();
        var permitTables = bindings.Select(b => b.PermitTypeTableId).Distinct().ToList();
        var headers = bindings.Where(b => b.HeaderFieldDefId != null).Select(b => b.HeaderFieldDefId!.Value).Distinct().ToList();
        var columnIds = bindings.SelectMany(b => b.Columns).Select(c => c.SheetColumnDefId).Distinct().ToList();

        var tableCodes = await db.TableDefs.AsNoTracking()
            .Where(t => sheetTables.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, ct).ConfigureAwait(false);
        var typeCodes = await db.PermitTypeDefs.AsNoTracking()
            .Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, ct).ConfigureAwait(false);
        var permitTableCodes = await db.PermitTypeTables.AsNoTracking()
            .Where(t => permitTables.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Code, ct).ConfigureAwait(false);
        var headerCodes = await db.HeaderFieldDefs.AsNoTracking()
            .Where(h => headers.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Code, ct).ConfigureAwait(false);
        var columnCodes = await db.ColumnDefs.AsNoTracking()
            .Where(c => columnIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct).ConfigureAwait(false);

        return
        [
            .. bindings.Select(b => new PermitBindingView(
                b,
                [.. b.Columns.OrderBy(c => c.Ordinal)],
                tableCodes.GetValueOrDefault(b.SheetTableDefId, b.SheetTableDefId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                typeCodes.GetValueOrDefault(b.PermitTypeDefId, string.Empty),
                permitTableCodes.GetValueOrDefault(b.PermitTypeTableId, string.Empty),
                b.HeaderFieldDefId is { } h ? headerCodes.GetValueOrDefault(h) : null,
                columnCodes)),
        ];
    }
}
