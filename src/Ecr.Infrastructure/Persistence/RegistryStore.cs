using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRegistryStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class RegistryStore(EcrDbContext db) : IRegistryStore
{
    /// <summary>
    /// Стеля вибірки записів одного довідника.
    /// </summary>
    /// <remarks>
    /// ⚠ Межа є навіть там, де «стільки не буває»: без неї помилка в даних
    /// (довідник, у який синхронізація влила мільйон рядків) виглядала б як
    /// повільність, а не як помилка. Найбільший реальний довідник — `Permit`,
    /// тисячі записів.
    /// </remarks>
    private const int MaxEntries = 50_000;

    /// <summary>Стеля вибірки зв'язків каскаду.</summary>
    private const int MaxLinks = 200_000;

    /// <inheritdoc />
    public Task<RegistryDef?> FindDefinitionAsync(string code, CancellationToken ct)
        => db.RegistryDefs
             .Include(d => d.Fields)
             .FirstOrDefaultAsync(d => d.Code == code, ct);

    /// <inheritdoc />
    public Task<RegistryDef?> FindDefinitionByIdAsync(int registryDefId, CancellationToken ct)
        => db.RegistryDefs
             .Include(d => d.Fields)
             .FirstOrDefaultAsync(d => d.Id == registryDefId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryDef>> ListDefinitionsAsync(CancellationToken ct)
        => await db.RegistryDefs
                   .AsNoTracking()
                   .Include(d => d.Fields)
                   .Where(d => d.IsActive)
                   .OrderBy(d => d.Code)
                   .Take(MaxEntries)
                   .ToListAsync(ct)
                   .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Темпорального фільтра тут немає навмисно: чинність рахує
    /// <c>RegistryResolver</c>. Продублювати умову в SQL означало б мати два
    /// визначення «чинний», які розійдуться на межах вікна першої ж правки.
    /// </remarks>
    public async Task<IReadOnlyList<RegistryEntry>> ListEntriesAsync(
        int registryDefId, CancellationToken ct)
        => await db.RegistryEntries
                   .AsNoTracking()
                   .Where(e => e.RegistryDefId == registryDefId)
                   .OrderBy(e => e.Ordinal)
                   .ThenBy(e => e.Code)
                   .Take(MaxEntries)
                   .ToListAsync(ct)
                   .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<RegistryEntry?> FindEntryAsync(long registryEntryId, CancellationToken ct)
        => db.RegistryEntries.FirstOrDefaultAsync(e => e.Id == registryEntryId, ct);

    /// <inheritdoc />
    public Task<RegistryEntry?> FindEntryByCodeAsync(
        int registryDefId, string code, CancellationToken ct)
        => db.RegistryEntries
             .FirstOrDefaultAsync(e => e.RegistryDefId == registryDefId && e.Code == code, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryEntryLink>> ListInboundLinksAsync(
        int registryDefId, CancellationToken ct)
    {
        // Зв'язки, у яких ПРАВОРУЧ стоїть запис цього довідника: саме вони
        // звужують його список обраним записом іншого довідника (ФВ-8.4).
        var query =
            from link in db.RegistryEntryLinks.AsNoTracking()
            join right in db.RegistryEntries.AsNoTracking()
                on link.RightEntryId equals right.Id
            where right.RegistryDefId == registryDefId
            select link;

        return await query.Take(MaxLinks).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Рахуються комірки, а не рядки: питання «чи можна видалити» ставиться до
    /// посилань, і рядок із двома комірками на той самий запис — два посилання.
    /// Точне число потрапляє в текст помилки, тому воно має бути чесним.
    /// </remarks>
    public Task<int> CountReferencesAsync(long registryEntryId, CancellationToken ct)
        => db.CellValues.CountAsync(c => c.ValueRegistryEntryId == registryEntryId, ct);

    /// <inheritdoc />
    public Task<bool> HasOpenPeriodAsync(CancellationToken ct)
        => db.Periods.AnyAsync(
            p => p.State == PeriodState.Open || p.State == PeriodState.Grace, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryValue>> ListValuesAsync(
        long registryEntryId, CancellationToken ct)
        => await db.RegistryValues
                   .Where(v => v.RegistryEntryId == registryEntryId)
                   .Take(MaxEntries)
                   .ToListAsync(ct)
                   .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(RegistryEntry entry) => db.RegistryEntries.Add(entry);

    /// <inheritdoc />
    public void AddValue(RegistryValue value) => db.RegistryValues.Add(value);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Без <c>AsNoTracking</c>: правила читає й обробник збереження, і
    /// відстеження тут потрібне, щоб правку наявного правила було чим записати.
    /// Правил на довідник одиниці — ціна відстеження нульова.
    /// </remarks>
    public async Task<IReadOnlyList<RegistryRuleDef>> ListRulesAsync(
        int registryDefId, CancellationToken ct)
        => await db.RegistryRuleDefs
                   .Where(r => r.RegistryDefId == registryDefId)
                   .OrderBy(r => r.RuleKind)
                   .ThenBy(r => r.Code)
                   .ToListAsync(ct)
                   .ConfigureAwait(false);

    /// <inheritdoc />
    public void AddRule(RegistryRuleDef rule) => db.RegistryRuleDefs.Add(rule);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryFieldMapping>> ListFieldMappingsAsync(
        int registryDefId, CancellationToken ct)
    {
        // ⛔ Ціль мапінгу — ПОЛЕ довідника (`TargetKind = 1`), і саме через
        // поле він зв'язується з довідником: у `ext.EntityFieldMap` немає
        // колонки `RegistryDefId`, а `ext.SourceEntity.RegistryDefId` каже
        // лише, звідки збирають — не куди лягає значення.
        var query =
            from map in db.EntityFieldMaps.AsNoTracking()
            join field in db.RegistryFieldDefs.AsNoTracking()
                on map.TargetRegistryFieldDefId equals field.Id
            join entity in db.SourceEntities.AsNoTracking()
                on map.SourceEntityId equals entity.Id
            where field.RegistryDefId == registryDefId
            join sourceUnit in db.Units.AsNoTracking()
                on map.SourceUnitId equals sourceUnit.Id into sourceUnits
            from sourceUnit in sourceUnits.DefaultIfEmpty()
            join targetUnit in db.Units.AsNoTracking()
                on map.TargetUnitId equals targetUnit.Id into targetUnits
            from targetUnit in targetUnits.DefaultIfEmpty()
            orderby entity.Code, map.SourceField
            select new RegistryFieldMapping(
                map.Id,
                field.Id,
                entity.Code,
                map.SourceField,
                map.TransformCode,
                sourceUnit == null ? null : sourceUnit.Code,
                targetUnit == null ? null : targetUnit.Code,
                map.IsActive);

        return await query.Take(MaxEntries).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryLinkKindStat>> ListLinkKindsAsync(
        int registryDefId, CancellationToken ct)
    {
        // ⚠ Зв'язок зараховується довіднику з ОБОХ боків: `Permit → Pollutant`
        // для довідника речовин так само його зв'язок, як і для дозволів.
        // Дивитися лише праворуч (як `ListInboundLinksAsync`) тут не можна:
        // конструктор дозволів показав би порожній перелік зв'язків саме там,
        // де їх найбільше.
        var query =
            from link in db.RegistryEntryLinks.AsNoTracking()
            join left in db.RegistryEntries.AsNoTracking() on link.LeftEntryId equals left.Id
            join right in db.RegistryEntries.AsNoTracking() on link.RightEntryId equals right.Id
            where left.RegistryDefId == registryDefId || right.RegistryDefId == registryDefId
            group link by link.LinkKind into kinds
            orderby kinds.Key
            select new RegistryLinkKindStat(kinds.Key, kinds.Count());

        return await query.ToListAsync(ct).ConfigureAwait(false);
    }
}



