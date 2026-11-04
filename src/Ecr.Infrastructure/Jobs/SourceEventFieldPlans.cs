// src/Ecr.Infrastructure/Jobs/SourceEventFieldPlans.cs
using Ecr.Application.Integration.SourceEvents;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Поля мапінгу подій, готові для <see cref="SourceEventRowBuilder"/>, і їхні колонки.</summary>
/// <param name="Plans">Поля з колонками, записами довідників і явними відповідностями (видалені колонки — без поля).</param>
/// <param name="Columns">Живі колонки полів за Id.</param>
public sealed record SourceEventFieldPlanSet(
    IReadOnlyList<SourceEventFieldPlan> Plans, IReadOnlyDictionary<int, ColumnDef> Columns);

/// <summary>
/// Завантаження полів мапінгу подій — ОДНЕ місце для запису (<see cref="SourceEventSyncJob"/>) і для перегляду
/// (<c>D-465</c>: «Дані з PI» і «Звідки це значення» рахують «що записав би синк» тим самим
/// <see cref="SourceEventRowBuilder"/> над тими самими полями).
/// </summary>
/// <remarks>
/// ⚠ Звернень до БД — сталий набір на мапінг (колонки, за потреби знімок груп, записи довідників), не на подію.
/// </remarks>
public static class SourceEventFieldPlans
{
    /// <summary>Поля мапінгу з колонками, записами довідників і явними відповідностями.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="map">Мапінг із полями та відповідностями.</param>
    /// <param name="groups">PRMG: склад колонок-груп; <c>null</c> — груп немає.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<SourceEventFieldPlanSet> LoadAsync(
        EcrDbContext db,
        SourceEventMap map,
        Application.Permits.Groups.ILookupTargetResolver? groups,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(map);

        var columnIds = map.Fields.Select(f => f.TargetColumnDefId).ToList();
        var columns = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => columnIds.Contains(c.Id) && !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id, ct)
            .ConfigureAwait(false);

        var targetIds = columns.Values
            .Where(c => c.DataType == CellDataType.Lookup && c.LookupRegistryDefId is not null)
            .Select(c => c.LookupRegistryDefId!.Value)
            .Distinct()
            .ToList();

        // Permits PRMG: ціль-група не має власних записів - читаються записи реєстрів її складу
        // (`ExpandRegistryIds`: для не-групи це [ціль], тож Land читає рівно ті самі записи).
        if (groups is not null && targetIds.Count > 0)
        {
            await groups.EnsureFreshAsync(ct).ConfigureAwait(false);
        }

        List<int> registryIds = [.. targetIds.SelectMany(id => groups?.ExpandRegistryIds(id) ?? [id]).Distinct()];

        var entries = registryIds.Count == 0
            ? Enumerable.Empty<Domain.Entities.Dictionaries.RegistryEntry>().ToLookup(e => e.RegistryDefId, e => new SourceEventLookupEntry(e.Id, e.Code, []))
            : (await db.RegistryEntries
                    .AsNoTracking()
                    .Where(e => registryIds.Contains(e.RegistryDefId) && !e.IsDeleted && e.IsActive)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToLookup(
                    e => e.RegistryDefId,
                    e => new SourceEventLookupEntry(e.Id, e.Code, [.. e.DisplayL10n.Values.Values]));

        List<SourceEventFieldPlan> plans = [.. map.Fields
            .Where(f => columns.ContainsKey(f.TargetColumnDefId))
            .Select(f =>
            {
                var column = columns[f.TargetColumnDefId];
                return new SourceEventFieldPlan(
                    column.Id,
                    column.Code,
                    column.DataType,
                    f.SourceAttribute,
                    f.AttributeScope,
                    f.ValueKind,
                    f.SourceUnitId,
                    f.TargetUnitId,
                    column.LookupRegistryDefId is { } registryId
                        ? [.. (groups?.ExpandRegistryIds(registryId) ?? [registryId]).SelectMany(member => entries[member])]
                        : [],
                    f.Values.ToDictionary(v => v.SourceValue, v => v.RegistryEntryId, StringComparer.OrdinalIgnoreCase));
            })];

        return new SourceEventFieldPlanSet(plans, columns);
    }
}
