// src/Ecr.Infrastructure/Persistence/RegistryExternalKeyStore.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRegistryExternalKeyStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⚠ Код запису й код джерела приєднуються в ТОМУ САМОМУ запиті: сторінка — один
/// похід у базу. Курсор — за <c>Id</c> угору (<see cref="Cursor"/>).
/// </remarks>
public sealed class RegistryExternalKeyStore(EcrDbContext db) : IRegistryExternalKeyStore
{
    /// <inheritdoc />
    public async Task<PagedResult<RegistryExternalKeyView>> ListAsync(
        RegistryExternalKeyFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        var after = Cursor.Decode(page.Cursor);

        // Локальні змінні — щоб умови стали параметрами запиту.
        var registryDefId = filter.RegistryDefId;
        var entryId = filter.RegistryEntryId;
        var dataSourceId = filter.DataSourceId;

        var rows = await Views()
            .Where(v => v.RegistryDefId == registryDefId)
            .Where(v => v.Id > after)
            .Where(v => entryId == null || v.RegistryEntryId == entryId)
            .Where(v => dataSourceId == null || v.DataSourceId == dataSourceId)
            .OrderBy(v => v.Id)
            .Take(page.Limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = rows.Take(page.Limit).Select(ToView).ToList();

        return new PagedResult<RegistryExternalKeyView>(
            items, rows.Count > page.Limit ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    /// <inheritdoc />
    public async Task<RegistryExternalKeyView?> FindByExternalIdAsync(
        int dataSourceId, string externalId, CancellationToken ct)
    {
        var row = await Views()
            .FirstOrDefaultAsync(v => v.DataSourceId == dataSourceId && v.ExternalId == externalId, ct)
            .ConfigureAwait(false);

        return row is null ? null : ToView(row);
    }

    /// <inheritdoc />
    public Task<RegistryExternalKey?> FindAsync(long id, CancellationToken ct)
        => db.RegistryExternalKeys.FirstOrDefaultAsync(k => k.Id == id, ct);

    /// <inheritdoc />
    public async Task AddAsync(RegistryExternalKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        db.RegistryExternalKeys.Add(key);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (SqlConflict.IsUniqueConstraintViolation(ex))
        {
            // ⚠ Гонка двох одночасних прив'язок: обидві пройшли перевірку обробника,
            // розвів їх лише UQ_RegistryExternalKey. Той самий 409, що й перевірка до
            // запису, — але без коду запису-переможця: база його не називає.
            db.Entry(key).State = EntityState.Detached;

            throw new BusinessRuleException(
                ErrorCodes.RegistryEntryInUse,
                $"Ідентифікатор «{key.ExternalId}» щойно прив'язав інший запит.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0409.externalKeyTakenConcurrently",
                    ["externalId"] = key.ExternalId,
                });
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(RegistryExternalKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        db.RegistryExternalKeys.Remove(key);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private IQueryable<Row> Views()
        => from key in db.RegistryExternalKeys.AsNoTracking()
           join entry in db.RegistryEntries.AsNoTracking() on key.RegistryEntryId equals entry.Id
           join source in db.DataSources.AsNoTracking() on key.DataSourceId equals source.Id
           select new Row
           {
               Id = key.Id,
               RegistryDefId = entry.RegistryDefId,
               RegistryEntryId = key.RegistryEntryId,
               EntryCode = entry.Code,
               DataSourceId = key.DataSourceId,
               DataSourceCode = source.Code,
               ExternalId = key.ExternalId,
               ExternalPath = key.ExternalPath,
               LastSyncedAt = key.LastSyncedAt,
               MissingInSourceSince = key.MissingInSourceSince,
           };

    private static RegistryExternalKeyView ToView(Row r)
        => new(
            r.Id, r.RegistryEntryId, r.EntryCode, r.DataSourceId, r.DataSourceCode, r.ExternalId, r.ExternalPath,
            r.LastSyncedAt, r.MissingInSourceSince);

    /// <summary>Проєкція рядка: іменований тип, щоб умови й сортування йшли в SQL.</summary>
    private sealed class Row
    {
        public long Id { get; init; }

        public int RegistryDefId { get; init; }

        public long RegistryEntryId { get; init; }

        public string EntryCode { get; init; } = null!;

        public int DataSourceId { get; init; }

        public string DataSourceCode { get; init; } = null!;

        public string ExternalId { get; init; } = null!;

        public string? ExternalPath { get; init; }

        public DateTime? LastSyncedAt { get; init; }

        public DateTime? MissingInSourceSince { get; init; }
    }
}
