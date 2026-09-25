using System.Globalization;
using Ecr.Application.Common;
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

    /// <summary>
    /// Мітка SQL-запиту «чи є комірки з посиланням на довідник» (<c>R-05</c>).
    /// </summary>
    /// <remarks>
    /// Коментар у тексті запиту: за ним тест знаходить план у кеші й перевіряє,
    /// що комірки читаються індексом, а не сканом.
    /// </remarks>
    public const string WhereUsedCellsTag = "R-05 registry where-used: cells";

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
    /// <remarks>
    /// ⚠ БЕЗ <c>AsNoTracking</c> навмисно — дзеркально до
    /// <see cref="FindDefinitionAsync"/>, який цей метод і замінює в циклі.
    /// Єдиний споживач змінює знайдене (<c>SwitchSource</c>) і зберігає;
    /// невідстежуваний результат зробив би перемикання порожньою операцією.
    /// </remarks>
    public async Task<IReadOnlyList<RegistryDef>> FindDefinitionsAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codes);

        if (codes.Count == 0)
        {
            return [];
        }

        // Матеріалізуємо набір у список: EF перекладає `Contains` по списку в
        // `IN (…)`, і колація порівняння лишається тією самою, що й у
        // `FindDefinitionAsync` (`d.Code == code`) — тобто регістронезалежною.
        var wanted = codes.ToList();

        return await db.RegistryDefs
                       .Include(d => d.Fields)
                       .Where(d => wanted.Contains(d.Code))
                       .OrderBy(d => d.Code)
                       .Take(MaxEntries)
                       .ToListAsync(ct)
                       .ConfigureAwait(false);
    }

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
    public void AddDefinition(RegistryDef definition) => db.RegistryDefs.Add(definition);

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
    public async Task<IReadOnlySet<long>> FindExistingEntryIdsAsync(
        IReadOnlyCollection<long> registryEntryIds, CancellationToken ct)
    {
        if (registryEntryIds.Count == 0)
        {
            return new HashSet<long>();
        }

        var found = await db.RegistryEntries
            .Where(e => registryEntryIds.Contains(e.Id))
            .Select(e => e.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found.ToHashSet();
    }

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
            orderby link.Id
            select link;

        return await query.Take(MaxLinks).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Рахуються комірки, а не рядки: питання «чи можна видалити» ставиться до
    /// посилань, і рядок із двома комірками на той самий запис — два посилання.
    /// Точне число потрапляє в текст помилки, тому воно має бути чесним.
    ///
    /// ⛔ <c>PeriodKey</c> сюди НЕ додається, хоча <c>doc.CellValue</c>
    /// партиціонована, і директива №14 (<c>WR-05</c>) називає цей рядок серед
    /// кандидатів. Запит глобальний ЗА ЗМІСТОМ: питання не «чи посилається на
    /// запис цей період», а «чи посилається на нього хоч хтось у системі». З
    /// ключем партиції видалення запису довідника перестало б бачити посилання
    /// з інших періодів — тобто прискорення обернулося б тихою втратою даних,
    /// а не оптимізацією. Ціна чесної відповіді тут — повний прохід по
    /// партиціях, і він виправданий: викликач один
    /// (<c>RegistryAdminHandlers</c>, видалення запису), і це не гарячий шлях.
    /// <para>
    /// ⛔ V-08: решта видів рахується тут само, одним викликом. Посилання з
    /// записів, які самі видалені логічно, не блокують: такий запис поза обігом
    /// і сам нічого не показує.
    /// </para>
    /// </remarks>
    public async Task<RegistryEntryReferences> CountReferencesAsync(long registryEntryId, CancellationToken ct)
    {
        var cells = await db.CellValues
            .CountAsync(c => c.ValueRegistryEntryId == registryEntryId, ct).ConfigureAwait(false);

        var headers = await db.DocumentHeaderValues
            .CountAsync(h => h.ValueRegistryEntryId == registryEntryId, ct).ConfigureAwait(false);

        var values = await (
                from value in db.RegistryValues.AsNoTracking()
                join owner in db.RegistryEntries.AsNoTracking() on value.RegistryEntryId equals owner.Id
                where value.ValueRefEntryId == registryEntryId
                      && owner.Id != registryEntryId
                      && !owner.IsDeleted
                select value.Id)
            .CountAsync(ct).ConfigureAwait(false);

        var children = await db.RegistryEntries
            .CountAsync(e => e.ParentEntryId == registryEntryId && !e.IsDeleted, ct).ConfigureAwait(false);

        var links = await db.RegistryEntryLinks
            .CountAsync(l => l.LeftEntryId == registryEntryId || l.RightEntryId == registryEntryId, ct)
            .ConfigureAwait(false);

        var constants = await db.MethodologyConstants
            .CountAsync(c => c.SubstanceEntryId == registryEntryId, ct).ConfigureAwait(false);

        var substances = await db.MethodologySubstances
            .CountAsync(m => m.SubstanceEntryId == registryEntryId, ct).ConfigureAwait(false);

        return new RegistryEntryReferences(cells, headers, values, children, links, constants, substances);
    }

    /// <inheritdoc />
    public async Task<UsageResponse> FindDefinitionUsageAsync(
        int registryDefId, int take, CancellationToken ct)
    {
        var total = 0;
        var items = new List<UsageItemDto>();

        // Рахує джерело повністю, а в перелік бере лише те, що ще вміщається.
        // ⚠ Джерело приходить уже ВПОРЯДКОВАНИМ: `OrderBy` після проєкції в
        // конструктор запису EF не перекладає.
        async Task AddAsync(string kind, IQueryable<UsageHit> source)
        {
            total += await source.CountAsync(ct).ConfigureAwait(false);
            if (items.Count >= take)
            {
                return;
            }

            var page = await source.Take(take - items.Count).ToListAsync(ct).ConfigureAwait(false);
            items.AddRange(page.Select(h => new UsageItemDto(
                kind, h.Id.ToString(CultureInfo.InvariantCulture), h.Label, h.Route)));
        }

        await AddAsync(
            UsageKinds.TemplateColumn,
            from column in db.ColumnDefs.AsNoTracking()
            where column.LookupRegistryDefId == registryDefId
            join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
            join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
            join version in db.TemplateVersions.AsNoTracking()
                on sheet.TemplateVersionId equals version.Id
            orderby column.Id
            select new UsageHit(
                column.Id,
                table.Code + "." + column.Code,
                "/admin/templates/" + version.TemplateId + "/versions/" + version.Id))
            .ConfigureAwait(false);

        // ⚠ Поле ВЛАСНОГО довідника, що вказує на нього ж (ієрархія), теж
        // рахується: воно так само перестане резолвитися, якщо довідник
        // перевипустити. Підпис показує довідник-власника, тож рядок читається.
        await AddAsync(
            UsageKinds.RegistryField,
            from field in db.RegistryFieldDefs.AsNoTracking()
            where field.RefRegistryDefId == registryDefId
            join owner in db.RegistryDefs.AsNoTracking() on field.RegistryDefId equals owner.Id
            orderby field.Id
            select new UsageHit(
                field.Id,
                owner.Code + "." + field.Code,
                "/admin/registries/" + owner.Code + "/definition"))
            .ConfigureAwait(false);

        // Методологія посилається не на довідник, а на його ЗАПИС
        // (`MethodologySubstance.SubstanceEntryId`, ФВ-8.8) — тому join через
        // `dic.RegistryEntry`, а не колонка з `RegistryDefId`.
        await AddAsync(
            UsageKinds.MethodologySubstance,
            from substance in db.MethodologySubstances.AsNoTracking()
            join entry in db.RegistryEntries.AsNoTracking()
                on substance.SubstanceEntryId equals entry.Id
            where entry.RegistryDefId == registryDefId
            join version in db.MethodologyVersions.AsNoTracking()
                on substance.MethodologyVersionId equals version.Id
            orderby substance.Id
            select new UsageHit(
                substance.Id,
                entry.Code,
                "/admin/methodologies/" + version.MethodologyId + "/versions"))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.SourceEntity,
            db.SourceEntities
                .AsNoTracking()
                .Where(entity => entity.RegistryDefId == registryDefId)
                .OrderBy(entity => entity.Id)
                .Select(entity => new UsageHit(entity.Id, entity.Code, "/admin/sources")))
            .ConfigureAwait(false);

        // Дані: один рядок на таблицю, без підрахунку (див. порт).
        //
        // ⛔ R-05: запит іде ВІД записів довідника до комірок, а не навпаки, і
        // `IS NOT NULL` стоїть явно. Доти EXISTS по doc.CellValue з корельованим
        // підзапитом до записів оптимізатор виконував як скан усіх комірок із
        // пошуком запису на кожну: на стенді (2.06 млн комірок) — 7.4 с і 3 млн
        // читань dic.RegistryEntry, навіть для довідника, на який не посилається
        // ніхто. Тепер вартість обмежена записами ЦЬОГО довідника, а кожен
        // пошук комірки — seek по IX_CellValue_RegistryEntry (фільтр
        // `IS NOT NULL`: явний предикат гарантує, що фільтрований індекс
        // зіставиться за будь-якої форми плану). PeriodKey не додається — див.
        // CountReferencesAsync: питання глобальне за змістом.
        var inCells = await db.RegistryEntries
            .AsNoTracking()
            .TagWith(WhereUsedCellsTag)
            .AnyAsync(
                entry => entry.RegistryDefId == registryDefId
                         && db.CellValues.Any(
                             cell => cell.ValueRegistryEntryId != null
                                     && cell.ValueRegistryEntryId == entry.Id),
                ct)
            .ConfigureAwait(false);

        if (inCells)
        {
            total++;
            if (items.Count < take)
            {
                // ⛔ X-11: тут стояло ім'я таблиці сховища (`doc.CellValue`) і як
                // ідентифікатор, і як підпис — людина читала «STORED DATA ·
                // doc.CellValue». Вид `data` клієнт підписує сам; підпис тут —
                // лише людський запасний текст для інших споживачів відповіді.
                items.Add(new UsageItemDto(UsageKinds.Data, "cells", "Values in document cells", null));
            }
        }

        return new UsageResponse(total, items);
    }

    /// <inheritdoc />
    public Task<bool> HasOpenPeriodAsync(CancellationToken ct)
        => db.Periods.AnyAsync(
            p => p.State == PeriodState.Open || p.State == PeriodState.Grace, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryValue>> ListValuesAsync(
        long registryEntryId, CancellationToken ct)
        => await db.RegistryValues
                   .Where(v => v.RegistryEntryId == registryEntryId)
                   .OrderBy(v => v.Id)
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

    /// <summary>Проміжний рядок пошуку посилань на визначення довідника.</summary>
    private sealed record UsageHit(int Id, string Label, string? Route);
}



