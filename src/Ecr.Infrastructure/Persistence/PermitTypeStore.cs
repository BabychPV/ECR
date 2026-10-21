using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IPermitTypeStore"/> над <see cref="EcrDbContext"/> (Permits, PM-03).</summary>
public sealed class PermitTypeStore(EcrDbContext db) : IPermitTypeStore
{
    // Типи дозволів, їхні таблиці й поля - метадані (десятки-сотні рядків); стеля захищає від вибірки без меж.
    private const int MaxRows = 20_000;

    /// <inheritdoc />
    public Task<PermitTypeDef?> FindByCodeAsync(string code, CancellationToken ct)
        => db.PermitTypeDefs.FirstOrDefaultAsync(t => t.Code == code, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitTypeSummary>> ListSummariesAsync(CancellationToken ct)
    {
        const int maxChars = Ecr.Application.Permits.PermitTypeLimits.ListDraftSummaryMaxChars;

        // ⚠ PA2-S-01: проєкція без стовпця DraftJson як такого - текст віддається лише за умови DATALENGTH <= межі (байти,
        // 2 на символ), тож великі чернетки з бази не виходять взагалі.
        return await db.PermitTypeDefs.AsNoTracking()
            .OrderBy(t => t.Code)
            .Take(MaxRows)
            .Select(t => new PermitTypeSummary(
                t.Id, t.Code, t.NameL10n, t.Category, t.HeaderRegistryDefId, t.Version, t.Status,
                t.UpdatedAt, t.UpdatedByUserId,
                t.DraftJson != null,
                t.DraftJson != null && EF.Functions.DataLength(t.DraftJson) <= maxChars * 2 ? t.DraftJson : null))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitTypeTable>> ListTablesAsync(int permitTypeDefId, CancellationToken ct)
        => await db.PermitTypeTables
            .Where(t => t.PermitTypeDefId == permitTypeDefId)
            .OrderBy(t => t.Ordinal).ThenBy(t => t.Id)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitTypeTable>> ListAllTablesAsync(CancellationToken ct)
        => await db.PermitTypeTables.AsNoTracking()
            .OrderBy(t => t.PermitTypeDefId).ThenBy(t => t.Ordinal).ThenBy(t => t.Id)
            .Take(MaxRows).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitFieldMeta>> ListFieldMetasAsync(int permitTypeDefId, CancellationToken ct)
        => await db.PermitFieldMetas
            .Where(m => m.PermitTypeDefId == permitTypeDefId)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(PermitTypeDef type) => db.PermitTypeDefs.Add(type);

    /// <inheritdoc />
    public void Add(PermitTypeTable table) => db.PermitTypeTables.Add(table);

    /// <inheritdoc />
    public void Add(PermitFieldMeta meta) => db.PermitFieldMetas.Add(meta);

    /// <inheritdoc />
    public void Remove(PermitFieldMeta meta) => db.PermitFieldMetas.Remove(meta);

    /// <inheritdoc />
    public async Task<IReadOnlySet<int>> ListHiddenRegistryIdsAsync(CancellationToken ct)
    {
        var headers = await db.PermitTypeDefs.AsNoTracking().Select(t => t.HeaderRegistryDefId).OrderBy(id => id).Take(MaxRows)
            .ToListAsync(ct).ConfigureAwait(false);
        var tables = await db.PermitTypeTables.AsNoTracking().Select(t => t.RegistryDefId).OrderBy(id => id).Take(MaxRows)
            .ToListAsync(ct).ConfigureAwait(false);
        var lists = await db.PermitFieldMetas.AsNoTracking()
            .Where(m => m.ListScope == PermitListScope.TypeOnly && m.ListRegistryDefId != null)
            .Select(m => m.ListRegistryDefId!.Value)
            .ToListAsync(ct).ConfigureAwait(false);

        return headers.Concat(tables).Concat(lists).ToHashSet();
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<int>> ListPublishedHeaderRegistryIdsAsync(CancellationToken ct)
        => (await db.PermitTypeDefs.AsNoTracking()
            .Where(t => t.Status == PermitTypeStatus.Published)
            .Select(t => t.HeaderRegistryDefId).OrderBy(id => id).Take(MaxRows)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> CountPermitsAsync(
        IReadOnlyCollection<int> headerRegistryDefIds, CancellationToken ct)
    {
        if (headerRegistryDefIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        return await db.RegistryEntries.AsNoTracking()
            .Where(e => headerRegistryDefIds.Contains(e.RegistryDefId) && !e.IsDeleted)
            .GroupBy(e => e.RegistryDefId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveDraftTypeAsync(PermitTypeDef type, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);

        var metas = await db.PermitFieldMetas.Where(m => m.PermitTypeDefId == type.Id).ToListAsync(ct).ConfigureAwait(false);
        var tables = await db.PermitTypeTables.Where(t => t.PermitTypeDefId == type.Id).ToListAsync(ct).ConfigureAwait(false);
        var header = await db.RegistryDefs.FirstOrDefaultAsync(d => d.Id == type.HeaderRegistryDefId, ct).ConfigureAwait(false);

        db.PermitFieldMetas.RemoveRange(metas);
        db.PermitTypeTables.RemoveRange(tables);
        db.PermitTypeDefs.Remove(type);

        if (header is not null)
        {
            db.RegistryDefs.Remove(header);
        }
    }
}
