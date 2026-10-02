using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
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

    /// <summary>Скільки кодів чи ідентифікаторів іде в один пакетний запит.</summary>
    private const int LookupChunkSize = 1000;

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
    public async Task<bool> LockDefinitionIsStaleAsync(int registryDefId, int loadedVersion, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Блокування опису довідника береться лише всередині транзакції: поза нею воно звільнилося б одразу.");
        }

        var current = await db.Database
            .SqlQuery<int>($"SELECT DefinitionVersion AS Value FROM cfg.RegistryDef WITH (UPDLOCK, HOLDLOCK) WHERE Id = {registryDefId}")
            .Take(1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return current.Count == 0 || current[0] != loadedVersion;
    }

    /// <inheritdoc />
    public async Task<int?> FindFieldHoldingReferenceAsync(IReadOnlyCollection<int> registryFieldDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryFieldDefIds);

        if (registryFieldDefIds.Count == 0)
        {
            return null;
        }

        var fieldIds = registryFieldDefIds.ToList();
        var inTransaction = db.Database.CurrentTransaction is not null;
        if (inTransaction)
        {
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;", ct).ConfigureAwait(false);
        }

        // `FirstOrDefault` по проєкції без `Take`-стелі: SQL Server зупиняється на першому рядку
        // (`TOP 1`), тобто це і є EXISTS; значення видалених записів теж враховуються.
        var holder = await db.RegistryValues
            .AsNoTracking()
            .Where(v => v.ValueRefEntryId != null && fieldIds.Contains(v.RegistryFieldDefId))
            .Select(v => (int?)v.RegistryFieldDefId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (inTransaction)
        {
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", ct).ConfigureAwait(false);
        }

        return holder;
    }

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
    /// <remarks>
    /// ⚠ Проєкція, а не сутності: шлях запису комірок не змінює записів
    /// довідника, і тягнути назви (<c>DisplayL10n</c>) заради п'яти полів
    /// немає сенсу. Темпорального фільтра в SQL немає навмисно — чинність
    /// рахує <see cref="RegistryEntryStanding.IsValidOn"/> тим самим вікном,
    /// що й пікер.
    /// </remarks>
    public async Task<IReadOnlyList<RegistryEntryStanding>> FindEntryStandingsAsync(
        IReadOnlyCollection<long> registryEntryIds, CancellationToken ct)
    {
        if (registryEntryIds.Count == 0)
        {
            return [];
        }

        return await db.RegistryEntries
            .AsNoTracking()
            .Where(e => registryEntryIds.Contains(e.Id))
            .Select(e => new RegistryEntryStanding(
                e.Id, e.RegistryDefId, e.IsActive, e.IsDeleted, e.ValidFrom, e.ValidTo))
            .ToListAsync(ct)
            .ConfigureAwait(false);
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
    /// <remarks>
    /// Ті самі сім видів, що в <see cref="CountReferencesAsync"/>, але власником посилання, який
    /// сам у наборі, нехтується: набір видаляється разом (RT-12, каскад композиції). Набір —
    /// одиниці-сотні записів (кейс і його склад), тому <c>IN (…)</c> одним списком.
    /// </remarks>
    public async Task<RegistryEntryReferences> CountReferencesFromOutsideAsync(
        IReadOnlyCollection<long> registryEntryIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryEntryIds);

        if (registryEntryIds.Count == 0)
        {
            return RegistryEntryReferences.None;
        }

        var ids = registryEntryIds.Distinct().ToList();

        var cells = await db.CellValues
            .CountAsync(c => c.ValueRegistryEntryId != null && ids.Contains(c.ValueRegistryEntryId.Value), ct)
            .ConfigureAwait(false);

        var headers = await db.DocumentHeaderValues
            .CountAsync(h => h.ValueRegistryEntryId != null && ids.Contains(h.ValueRegistryEntryId.Value), ct)
            .ConfigureAwait(false);

        // ⛔ Саме тут частини композиції перестають блокувати видалення батька: їхнє значення
        // поля композиції посилається на батька, але власник значення — у наборі.
        var values = await (
                from value in db.RegistryValues.AsNoTracking()
                join owner in db.RegistryEntries.AsNoTracking() on value.RegistryEntryId equals owner.Id
                where value.ValueRefEntryId != null
                      && ids.Contains(value.ValueRefEntryId.Value)
                      && !ids.Contains(owner.Id)
                      && !owner.IsDeleted
                select value.Id)
            .CountAsync(ct).ConfigureAwait(false);

        var children = await db.RegistryEntries
            .CountAsync(
                e => e.ParentEntryId != null && ids.Contains(e.ParentEntryId.Value) && !ids.Contains(e.Id) && !e.IsDeleted,
                ct)
            .ConfigureAwait(false);

        var links = await db.RegistryEntryLinks
            .CountAsync(
                l => (ids.Contains(l.LeftEntryId) || ids.Contains(l.RightEntryId))
                     && !(ids.Contains(l.LeftEntryId) && ids.Contains(l.RightEntryId)),
                ct)
            .ConfigureAwait(false);

        var constants = await db.MethodologyConstants
            .CountAsync(c => c.SubstanceEntryId != null && ids.Contains(c.SubstanceEntryId.Value), ct)
            .ConfigureAwait(false);

        var substances = await db.MethodologySubstances
            .CountAsync(m => ids.Contains(m.SubstanceEntryId), ct).ConfigureAwait(false);

        return new RegistryEntryReferences(cells, headers, values, children, links, constants, substances);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryCompositionChild>> ListCompositionChildrenAsync(
        IReadOnlyCollection<long> parentEntryIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parentEntryIds);

        if (parentEntryIds.Count == 0)
        {
            return [];
        }

        var parents = parentEntryIds.Distinct().ToList();

        // ⚠ Жодного AsNoTracking у джерелах: воно діє на ВЕСЬ запит, і частини повернулися б
        // невідстежуваними — каскад «видаляв» би їх лише в пам'яті. Відстежуються лише записи:
        // значення й поля в проєкцію не потрапляють.
        var rows = await (
                from value in db.RegistryValues
                join field in db.RegistryFieldDefs on value.RegistryFieldDefId equals field.Id
                join child in db.RegistryEntries on value.RegistryEntryId equals child.Id
                where field.RelationKind == RegistryRelationKind.Composition
                      && value.ValueRefEntryId != null
                      && parents.Contains(value.ValueRefEntryId.Value)
                      && !child.IsDeleted
                orderby child.Id
                select new { Child = child, Parent = value.ValueRefEntryId!.Value, field.OnParentDelete })
            .Take(MaxEntries)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => new RegistryCompositionChild(r.Child, r.Parent, r.OnParentDelete))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<long> NextEntryCodesAsync(int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // Той самий спосіб, що `CalculationResultStore.SequenceRangeAsync`: один виклик
        // sp_sequence_get_range на весь пакет замість NEXT VALUE FOR на кожен запис.
        var range = await db.Database
            .SqlQuery<long>($"""
                DECLARE @first sql_variant;
                EXEC sys.sp_sequence_get_range
                    @sequence_name = N'dic.RegistryEntryCodeSeq',
                    @range_size = {(long)count},
                    @range_first_value = @first OUTPUT;
                SELECT CONVERT(bigint, @first) AS Value;
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return range.Single();
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
                kind, h.Id.ToString(CultureInfo.InvariantCulture), h.Label, h.Route, h.DisplayName())));
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
                "/admin/templates/" + version.TemplateId + "/versions/" + version.Id,
                column.HeaderL10n,
                null))
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
                "/admin/registries/" + owner.Code + "/definition",
                field.NameL10n,
                null))
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
            join methodology in db.Methodologies.AsNoTracking()
                on version.MethodologyId equals methodology.Id
            orderby substance.Id
            select new UsageHit(
                substance.Id,
                entry.Code,
                "/admin/methodologies/" + version.MethodologyId + "/versions",
                methodology.NameL10n,
                null))
            .ConfigureAwait(false);

        await AddAsync(
            UsageKinds.SourceEntity,
            db.SourceEntities
                .AsNoTracking()
                .Where(entity => entity.RegistryDefId == registryDefId)
                .OrderBy(entity => entity.Id)
                .Select(entity => new UsageHit(
                    entity.Id, entity.Code, "/admin/sources", null, entity.DisplayName)))
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
    public async Task<UsageResponse> FindFieldChainConsumersAsync(
        int registryDefId, IReadOnlyCollection<string> fieldCodes, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fieldCodes);

        if (fieldCodes.Count == 0)
        {
            return new UsageResponse(0, []);
        }

        var prefixes = fieldCodes.Select(c => c + ".").ToList();
        var edges = await db.RegistryUses
            .AsNoTracking()
            .Where(u => u.RegistryDefId == registryDefId && u.FieldPath != null)
            .Select(u => new { u.SourceKind, u.SourceId, u.FormulaCode, u.FieldPath })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Фільтр за префіксом — у пам'яті: ребер довідника небагато, а регістронезалежне
        // порівняння тут має збігатися з тим, як шлях розбирає компілятор виразів.
        var hits = edges
            .Where(e => prefixes.Exists(p => e.FieldPath!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(e => (e.SourceKind, e.SourceId, e.FormulaCode))
            .Distinct()
            .ToList();

        var items = new List<UsageItemDto>();

        var ruleIds = hits.Where(h => h.SourceKind == 2).Select(h => h.SourceId).ToList();
        if (ruleIds.Count > 0)
        {
            var rules = await (
                    from rule in db.RegistryRuleDefs.AsNoTracking()
                    where ruleIds.Contains(rule.Id)
                    join owner in db.RegistryDefs.AsNoTracking() on rule.RegistryDefId equals owner.Id
                    orderby owner.Code, rule.Code
                    select new { rule.Id, Owner = owner.Code, Rule = rule.Code })
                .ToListAsync(ct).ConfigureAwait(false);
            // Власного виду для правила довідника в `UsageKinds` немає (каталог клієнта, сторож
            // UsageKindsTests): це лише деталі відмови, тож вид — найближчий, `registryField`,
            // а правило однозначно читається з підпису «ВЛАСНИК.правило».
            items.AddRange(rules.Select(r => new UsageItemDto(
                UsageKinds.RegistryField,
                r.Id.ToString(CultureInfo.InvariantCulture),
                r.Owner + "." + r.Rule,
                "/admin/registries/" + r.Owner + "/definition")));
        }

        var formulaIds = hits.Where(h => h.SourceKind == 0).Select(h => h.SourceId).ToList();
        if (formulaIds.Count > 0)
        {
            var formulas = await (
                    from formula in db.FormulaDefs.AsNoTracking()
                    where formulaIds.Contains(formula.Id)
                    join table in db.TableDefs.AsNoTracking() on formula.TableDefId equals table.Id
                    orderby table.Code, formula.Id
                    select new { formula.Id, Table = table.Code })
                .ToListAsync(ct).ConfigureAwait(false);
            items.AddRange(formulas.Select(f => new UsageItemDto(
                UsageKinds.TemplateFormula,
                f.Id.ToString(CultureInfo.InvariantCulture),
                f.Table + "#" + f.Id.ToString(CultureInfo.InvariantCulture),
                null)));
        }

        var methodologyHits = hits.Where(h => h.SourceKind == 1).ToList();
        if (methodologyHits.Count > 0)
        {
            var versionIds = methodologyHits.Select(h => h.SourceId).Distinct().ToList();
            var versions = await (
                    from version in db.MethodologyVersions.AsNoTracking()
                    where versionIds.Contains(version.Id)
                    join methodology in db.Methodologies.AsNoTracking() on version.MethodologyId equals methodology.Id
                    select new { version.Id, version.MethodologyId, Methodology = methodology.Code, version.Version })
                .ToListAsync(ct).ConfigureAwait(false);
            items.AddRange(methodologyHits
                .Join(versions, h => h.SourceId, v => v.Id, (h, v) => new UsageItemDto(
                    UsageKinds.MethodologyFormula,
                    v.Id.ToString(CultureInfo.InvariantCulture) + ":" + h.FormulaCode,
                    v.Methodology + " v" + v.Version + (h.FormulaCode is null ? string.Empty : "." + h.FormulaCode),
                    "/admin/methodologies/" + v.MethodologyId + "/versions"))
                .OrderBy(i => i.Label, StringComparer.Ordinal));
        }

        return new UsageResponse(items.Count, items.Take(take).ToList());
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

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Набір ріжеться на шматки <see cref="LookupChunkSize"/>: імпорт на
    /// 1 МБ — це десятки тисяч кодів, а параметрів у запиті не більше 2100.
    /// На звичайному файлі (до тисячі рядків) це рівно один запит.
    /// </remarks>
    public async Task<IReadOnlyList<RegistryEntry>> FindEntriesByCodesAsync(
        int registryDefId, IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(codes);

        var found = new List<RegistryEntry>();
        foreach (var chunk in codes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(LookupChunkSize))
        {
            var wanted = chunk.ToList();
            found.AddRange(await db.RegistryEntries
                .Where(e => e.RegistryDefId == registryDefId && wanted.Contains(e.Code))
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryValue>> ListValuesForEntriesAsync(
        IReadOnlyCollection<long> registryEntryIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registryEntryIds);

        var found = new List<RegistryValue>();
        foreach (var chunk in registryEntryIds.Distinct().Chunk(LookupChunkSize))
        {
            var wanted = chunk.ToList();
            found.AddRange(await db.RegistryValues
                .Where(v => wanted.Contains(v.RegistryEntryId))
                .OrderBy(v => v.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        return found;
    }

    /// <inheritdoc />
    public async Task ReplaceRuleUsesAsync(
        int ruleRegistryDefId, IReadOnlyCollection<RegistryUse> uses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uses);

        // ⚠ Ребра шукаються за ПРАВИЛАМИ довідника, а не за RegistryDefId ребра: правило читає й
        // ІНШІ довідники (склад кейсу, ціль CrossRegistry), і їхні ребра — теж цього правила.
        var ruleIds = db.RegistryRuleDefs
            .Where(r => r.RegistryDefId == ruleRegistryDefId)
            .Select(r => r.Id);
        var stale = await db.RegistryUses
            .Where(u => u.SourceKind == RegistryUse.RegistryRuleSource && ruleIds.Contains(u.SourceId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.RegistryUses.RemoveRange(stale);
        db.RegistryUses.AddRange(uses);
    }

    /// <summary>Проміжний рядок пошуку посилань на визначення довідника.</summary>
    /// <summary>Проєкція одного посилання; <c>Title</c>/<c>Plain</c> — джерела читабельної назви (ФВ-8.14).</summary>
    private sealed record UsageHit(
        int Id, string Label, string? Route, LocalizedText? Title, string? Plain)
    {
        /// <summary>Назва для людини; <c>null</c> — назви немає (клієнт показує код).</summary>
        public string? DisplayName()
        {
            var name = Plain ?? Title?.Get("uk");
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }
}



