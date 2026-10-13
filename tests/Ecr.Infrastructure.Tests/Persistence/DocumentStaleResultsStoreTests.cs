using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// «Результати застаріли» у переліку документів (<c>resultsStale</c>): позначка в рядку, фільтр до стелі сторінки
/// і лічильники <c>/documents/summary</c> компонують ОДИН предикат (<c>StaleResultsQuery</c>) на справжній базі.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>Origin &lt;&gt; 'Recalculation'</c> →
/// <see cref="Перерахунок_правка_до_прогону_чужий_період_і_документ_без_результатів_не_застарілі"/> червоний;
/// замінити <c>MAX(r0.Id)</c> на <c>MIN</c> → <see cref="Береться_найновіший_актуальний_прогін_документа"/> червоний;
/// прибрати <c>!narrowed.Contains</c> у <c>DocumentStore.ListAsync</c> →
/// <see cref="Проєкти_зі_звуженням_виключені_з_фільтра_в_обох_напрямках"/> червоний;
/// прибрати <c>c.ChangedByUserId = by</c> → <see cref="Фільтр_за_автором_правки_бачить_лише_його_правки"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentStaleResultsStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_входу_після_прогону_робить_застарілим_а_повторний_прогін_знімає()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var version = await VersionAsync(db);
        var run = await RunAsync(db, chain, version, Now, [chain.DocumentId]);

        Assert.False((await CardAsync(db, chain, period)).ResultsStale);

        var editedAt = Now.AddHours(1);
        await EditAsync(chain.DocumentId, period, chain.ColumnDefIds[1], editedAt);

        var stale = await CardAsync(db, chain, period);
        Assert.True(stale.ResultsStale);
        Assert.NotNull(stale.ResultsStaleSince);
        Assert.True(Math.Abs((stale.ResultsStaleSince!.Value - editedAt).TotalSeconds) < 1, $"{stale.ResultsStaleSince:O}");

        // Повторний прогін (F9): старий знято, новий стартує ПІСЛЯ правки.
        run.Supersede();
        await db.SaveChangesAsync(CancellationToken.None);
        await RunAsync(db, chain, version, Now.AddHours(2), [chain.DocumentId]);

        var fresh = await CardAsync(db, chain, period);
        Assert.False(fresh.ResultsStale);
        Assert.Null(fresh.ResultsStaleSince);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перерахунок_правка_до_прогону_чужий_період_і_документ_без_результатів_не_застарілі()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var recalculated = await AddDocumentAsync(db, chain, "ST-RECALC");
        var before = await AddDocumentAsync(db, chain, "ST-BEFORE");
        var otherPeriod = await AddDocumentAsync(db, chain, "ST-OTHERP");
        var noResults = await AddDocumentAsync(db, chain, "ST-NORES");

        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [recalculated, before, otherPeriod]);

        var column = chain.ColumnDefIds[1];
        await EditAsync(recalculated, period, column, Now.AddHours(1), origin: "Recalculation");
        await EditAsync(before, period, column, Now.AddHours(-1));
        await EditAsync(otherPeriod, period + 1, column, Now.AddHours(1));
        await EditAsync(noResults, period, column, Now.AddHours(1));

        var page = await ListAsync(db, chain, period, default);

        // ⛔ Не `null`: період відомий, результатів у документа просто немає — це «не застарілі», а не «невідомо».
        foreach (var id in new[] { recalculated, before, otherPeriod, noResults })
        {
            Assert.False(page.Single(d => d.Id == id).ResultsStale, $"документ {id}");
        }

        // Без періоду застарілість не визначена: `null`, а не `false`.
        var noPeriod = await new DocumentStore(db).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(null), default, new CursorRequest(Limit: 50),
            visibleProjectIds: null, CancellationToken.None);
        Assert.All(noPeriod.Items, d => Assert.Null(d.ResultsStale));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Береться_найновіший_актуальний_прогін_документа()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var version = await VersionAsync(db);

        // Дві актуальні області того самого проєкту й періоду: прогін усього проєкту (раніший) і прогін ЛИШЕ цього
        // документа (пізніший). Уніфікований індекс допускає обидва - різний DocumentId.
        await RunAsync(db, chain, version, Now, [chain.DocumentId]);
        await RunAsync(db, chain, version, Now.AddHours(1), [chain.DocumentId], runDocumentId: chain.DocumentId);

        // Правка між двома стартами: проти раннього прогону це «застаріло», проти найновішого - ні.
        await EditAsync(chain.DocumentId, period, chain.ColumnDefIds[1], Now.AddMinutes(30));
        Assert.False((await CardAsync(db, chain, period)).ResultsStale);

        await EditAsync(chain.DocumentId, period, chain.ColumnDefIds[1], Now.AddMinutes(90));
        Assert.True((await CardAsync(db, chain, period)).ResultsStale);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_стоїть_у_запиті_до_стелі_сторінки_і_перетинається_з_пошуком()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var staleIds = new List<long>();
        var freshIds = new List<long>();
        for (var i = 0; i < 3; i++)
        {
            freshIds.Add(await AddDocumentAsync(db, chain, $"STF{tag}-F{i}"));
            staleIds.Add(await AddDocumentAsync(db, chain, $"STF{tag}-S{i}"));
        }

        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [.. staleIds, .. freshIds]);
        foreach (var id in staleIds)
        {
            await EditAsync(id, period, chain.ColumnDefIds[1], Now.AddHours(1));
        }

        // По одному документу на сторінку: фільтр ДО `Take` проходить рівно застарілі, без порожніх сторінок.
        var seen = new List<long>();
        string? cursor = null;
        do
        {
            var page = await new DocumentStore(db).ListAsync(
                chain.ProjectId, new PeriodKeyFilter(period),
                new DocumentListFilter(null, null, ResultsStale: true, Query: $"STF{tag}"),
                new CursorRequest(Limit: 1, cursor), visibleProjectIds: null, CancellationToken.None);

            Assert.Single(page.Items);
            Assert.True(page.Items[0].ResultsStale);
            seen.AddRange(page.Items.Select(d => d.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(staleIds.Order(), seen.Order());

        var notStale = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: false, Query: $"STF{tag}"));
        Assert.Equal(freshIds.Order(), notStale.Select(d => d.Id).Order());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Проєкти_зі_звуженням_виключені_з_фільтра_в_обох_напрямках()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [chain.DocumentId]);
        await EditAsync(chain.DocumentId, period, chain.ColumnDefIds[1], Now.AddHours(1));

        // Контроль: читач без звуження бачить документ у «застарілих».
        var open = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true));
        Assert.Contains(open, d => d.Id == chain.DocumentId);

        // ⛔ Звужений: ні `true`, ні `false` не повертають документ - інакше «не застарілі» = решта розкриває його.
        foreach (var want in new[] { true, false })
        {
            var narrowed = await ListAsync(
                db, chain, period, new DocumentListFilter(null, null, ResultsStale: want, NarrowedProjectIds: [chain.ProjectId]));
            Assert.Empty(narrowed);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_за_автором_правки_бачить_лише_його_правки()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var byMe = await AddDocumentAsync(db, chain, "ST-BYME");
        var byOther = await AddDocumentAsync(db, chain, "ST-BYOTHER");
        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [byMe, byOther]);

        const int me = 41;
        const int other = 42;
        await EditAsync(byMe, period, chain.ColumnDefIds[1], Now.AddHours(1), user: me);
        await EditAsync(byOther, period, chain.ColumnDefIds[1], Now.AddHours(1), user: other);

        var mine = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: me));
        Assert.Equal(new[] { byMe }, mine.Select(d => d.Id).ToArray());

        var all = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true));
        Assert.Equal(new[] { byMe, byOther }.Order(), all.Select(d => d.Id).Order());

        var nobody = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: 43));
        Assert.Empty(nobody);
    }

    /// <summary>
    /// LP-45: сторінка в 50+ документів дає ті самі прапорці й «з якого моменту», що й кожен документ окремо
    /// (контрольний результат рахується в тесті, не запитом). Мутація: у <c>StaleResultsQuery</c> замінити
    /// <c>MIN(t.At)</c> на <c>MAX</c> або прибрати <c>ChangedAt &gt; r.StartedAt</c> — червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сторінка_з_п_ятдесяти_документів_дає_ті_самі_прапорці_і_моменти_що_очікувані()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var documents = new List<long>();
        for (var i = 0; i < 55; i++)
        {
            documents.Add(await AddDocumentAsync(db, chain, $"STP{tag}-{i:00}"));
        }

        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [.. documents]);

        // i % 5: 0 - дві правки після прогону (потрібна найраніша), 1 - лише до прогону, 2 - лише Recalculation,
        // 3 - правка в іншому періоді, 4 - без правок.
        var expected = new Dictionary<long, DateTime?>();
        for (var i = 0; i < documents.Count; i++)
        {
            var id = documents[i];
            var column = chain.ColumnDefIds[1];
            switch (i % 5)
            {
                case 0:
                    await EditAsync(id, period, column, Now.AddHours(3));
                    await EditAsync(id, period, column, Now.AddMinutes(10 + i));
                    expected[id] = Now.AddMinutes(10 + i);
                    break;
                case 1:
                    await EditAsync(id, period, column, Now.AddHours(-2));
                    break;
                case 2:
                    await EditAsync(id, period, column, Now.AddHours(1), origin: "Recalculation");
                    break;
                case 3:
                    await EditAsync(id, period + 1, column, Now.AddHours(1));
                    break;
                default:
                    break;
            }
        }

        var page = (await new DocumentStore(db).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(period), new DocumentListFilter(null, null, Query: $"STP{tag}"),
            new CursorRequest(Limit: 50), visibleProjectIds: null, CancellationToken.None)).Items;

        Assert.Equal(50, page.Count);
        foreach (var item in page)
        {
            var isStale = expected.TryGetValue(item.Id, out var since);
            Assert.Equal(isStale, item.ResultsStale);
            if (isStale)
            {
                Assert.True(
                    Math.Abs((item.ResultsStaleSince!.Value - since!.Value).TotalSeconds) < 1,
                    $"{item.Id}: {item.ResultsStaleSince:O} != {since:O}");
            }
            else
            {
                Assert.Null(item.ResultsStaleSince);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_довідника_який_читає_методологія_застарює_документ_але_не_є_правкою_користувача()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registry = new RegistryDef(EcrCode.Create($"SR{tag}"), Name("Registry"), isTemporal: false);
        registry.MarkDataChanged(Now.AddHours(1));
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = await VersionAsync(db);
        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version, "F1", registry.Id, "X"));
        await db.SaveChangesAsync(CancellationToken.None);
        await RunAsync(db, chain, version, Now, [chain.DocumentId]);

        var stale = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true));
        Assert.Contains(stale, d => d.Id == chain.DocumentId);

        // Довідник змінила не людина з журналу комірок - «моїх» це не стосується.
        var mine = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true, StaleByUserId: 1));
        Assert.DoesNotContain(mine, d => d.Id == chain.DocumentId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лічильники_зведення_збігаються_з_фільтром_і_не_рахують_звужені_проєкти()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var byMe = await AddDocumentAsync(db, chain, "SC-BYME");
        var byOther = await AddDocumentAsync(db, chain, "SC-BYOTHER");
        var fresh = await AddDocumentAsync(db, chain, "SC-FRESH");
        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [byMe, byOther, fresh]);

        const int me = 51;
        await EditAsync(byMe, period, chain.ColumnDefIds[1], Now.AddHours(1), user: me);
        await EditAsync(byOther, period, chain.ColumnDefIds[1], Now.AddHours(1), user: 52);

        var store = new DocumentListSummaryStore(db);
        var summary = await store.SummarizeAsync(
            chain.ProjectId, period, [chain.ProjectId], restrictions: null, me, CancellationToken.None);

        var filtered = await ListAsync(db, chain, period, new DocumentListFilter(null, null, ResultsStale: true));
        Assert.Equal(2, summary.StaleResultsCount);
        Assert.Equal(filtered.Count, summary.StaleResultsCount);
        Assert.Equal(1, summary.StaleResultsMineCount);

        // Без користувача «моїх» немає, а загальне число те саме.
        var anonymous = await store.SummarizeAsync(
            chain.ProjectId, period, [chain.ProjectId], restrictions: null, currentUserId: null, CancellationToken.None);
        Assert.Equal(2, anonymous.StaleResultsCount);
        Assert.Equal(0, anonymous.StaleResultsMineCount);

        // ⛔ Проєкт зі звуженням не рахується: число по всьому документу розкрило б правку схованого входу.
        var narrowed = await store.SummarizeAsync(
            chain.ProjectId, period, [chain.ProjectId], new SummaryRestrictions([], [chain.ProjectId]), me, CancellationToken.None);
        Assert.Equal(0, narrowed.StaleResultsCount);
        Assert.Equal(0, narrowed.StaleResultsMineCount);

        // Чужий проєкт з тими самими даними поза межею грантів не потрапляє в число.
        var foreign = await builder.BuildAsync(ct: CancellationToken.None);
        await RunAsync(db, foreign, version, Now, [foreign.DocumentId]);
        await EditAsync(foreign.DocumentId, foreign.PeriodKey.Value, foreign.ColumnDefIds[1], Now.AddHours(1), user: me);
        var scoped = await store.SummarizeAsync(
            null, period, [chain.ProjectId], restrictions: null, me, CancellationToken.None);
        Assert.Equal(2, scoped.StaleResultsCount);
    }

    // ── короткий кеш лічильників (staleResultsCount / staleResultsMineCount) ──────────────────

    /// <summary>Сценарій кешу: два документи застарілі (один - правкою користувача <c>Me</c>), третій свіжий.</summary>
    private sealed record CacheScenario(TestDocumentBuilder Builder, TestDocument Chain, long Fresh, int Period);

    private const int Me = 61;

    private async Task<CacheScenario> CacheScenarioAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var byMe = await AddDocumentAsync(db, chain, "SK-ME");
        var byOther = await AddDocumentAsync(db, chain, "SK-OTHER");
        var fresh = await AddDocumentAsync(db, chain, "SK-FRESH");
        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [byMe, byOther, fresh]);
        await EditAsync(byMe, period, chain.ColumnDefIds[1], Now.AddHours(1), user: Me);
        await EditAsync(byOther, period, chain.ColumnDefIds[1], Now.AddHours(1), user: 62);

        return new CacheScenario(builder, chain, fresh, period);
    }

    private static Task<Ecr.Application.Documents.Dto.DocumentListSummaryResponse> SummaryAsync(
        DocumentListSummaryStore store, CacheScenario s, SummaryRestrictions? restrictions, int? user)
        => store.SummarizeAsync(
            s.Chain.ProjectId, s.Period, [s.Chain.ProjectId], restrictions, user, CancellationToken.None);

    private static Microsoft.Extensions.Caching.Memory.MemoryCache NewCache()
        => new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторний_виклик_у_межах_TTL_не_ходить_у_БД_за_лічильниками_застарілих()
    {
        var s = await CacheScenarioAsync();
        var counter = new DbCommandCounter();
        await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(counter).Options);
        using var cache = NewCache();
        var store = new DocumentListSummaryStore(counted, cache, new TestClock(Now.AddHours(2)));

        var first = await SummaryAsync(store, s, restrictions: null, Me);
        var afterFirst = counter.Tally.Snapshot().Total;
        var second = await SummaryAsync(store, s, restrictions: null, Me);
        var afterSecond = counter.Tally.Snapshot().Total;

        Assert.Equal(2, first.StaleResultsCount);
        Assert.Equal(1, first.StaleResultsMineCount);
        Assert.Equal(first, second);

        // Перший виклик: агрегат + відбиток + дві карти (усі / мої) + два підрахунки видимих = 6 запитів; другий -
        // агрегат + відбиток + два підрахунки (4): карти періоду з кешу, журнал правок не чіпається.
        Assert.Equal(6, afterFirst);
        Assert.Equal(4, afterSecond - afterFirst);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Після_TTL_лічильник_оновлюється_без_інвалідації()
    {
        var s = await CacheScenarioAsync();
        await using var db = s.Builder.CreateContext();
        using var cache = NewCache();
        var clock = new TestClock(Now.AddHours(2));
        var store = new DocumentListSummaryStore(db, cache, clock);

        Assert.Equal(2, (await SummaryAsync(store, s, null, Me)).StaleResultsCount);

        // Третій документ застарів ПІСЛЯ першого виклику: у межах TTL лічильник його ще не бачить...
        await EditAsync(s.Fresh, s.Period, s.Chain.ColumnDefIds[1], Now.AddHours(1), user: Me);
        clock.Advance(DocumentListSummaryStore.StaleCountsTtl - TimeSpan.FromSeconds(1));
        var within = await SummaryAsync(store, s, null, Me);
        Assert.Equal(2, within.StaleResultsCount);

        // ...а після TTL (керований годинник, без sleep) - бачить.
        clock.Advance(TimeSpan.FromSeconds(2));
        var after = await SummaryAsync(store, s, null, Me);
        Assert.Equal(3, after.StaleResultsCount);
        Assert.Equal(2, after.StaleResultsMineCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "RC15-S-1")]
    public async Task З_projectId_і_без_нього_ті_самі_дані_дають_те_саме_число_а_розбіжність_лише_в_межах_TTL()
    {
        // RC15 S-1 (приймальна: `staleResultsCount` 0 з projectId і 1 без нього). Корінь - не різні предикати:
        // обидва виклики йдуть одним `StaleResultsQuery`. Розбіжність дає лічильник у короткому кеші
        // (`StaleCountsTtl`, без інвалідації; ключ містить projectId): виклик з projectId, зроблений ДО правки,
        // лишається в кеші, а виклик без projectId - інший ключ - рахується заново.
        var s = await CacheScenarioAsync();

        // 1. Без кешу: ті самі дані - те саме число (вибір по проєкту = вибір по межі грантів з одного проєкту).
        await using (var plain = s.Builder.CreateContext())
        {
            var store = new DocumentListSummaryStore(plain);
            var withProject = await store.SummarizeAsync(
                s.Chain.ProjectId, s.Period, [s.Chain.ProjectId], null, Me, CancellationToken.None);
            var withoutProject = await store.SummarizeAsync(
                null, s.Period, [s.Chain.ProjectId], null, Me, CancellationToken.None);
            Assert.Equal(2, withProject.StaleResultsCount);
            Assert.Equal(withProject.StaleResultsCount, withoutProject.StaleResultsCount);
            Assert.Equal(withProject.StaleResultsMineCount, withoutProject.StaleResultsMineCount);
        }

        // 2. З кешем: виклик з projectId тепліє до правки; після правки виклик без projectId бачить нове,
        // а тепла відповідь з projectId - ще ні (це і було 0 vs 1), аж до кінця TTL.
        await using var db = s.Builder.CreateContext();
        using var cache = NewCache();
        var clock = new TestClock(Now.AddHours(2));
        var cached = new DocumentListSummaryStore(db, cache, clock);

        Assert.Equal(2, (await cached.SummarizeAsync(
            s.Chain.ProjectId, s.Period, [s.Chain.ProjectId], null, Me, CancellationToken.None)).StaleResultsCount);

        await EditAsync(s.Fresh, s.Period, s.Chain.ColumnDefIds[1], Now.AddHours(1), user: Me);

        // LP-45 етап 2: карта періоду не залежить від projectId - обидва виклики ділять ОДИН запис, тож
        // розбіжності «0 з projectId / 1 без нього» більше немає навіть у межах TTL (обидва бачать 2).
        var coldKey = await cached.SummarizeAsync(
            null, s.Period, [s.Chain.ProjectId], null, Me, CancellationToken.None);
        var warmKey = await cached.SummarizeAsync(
            s.Chain.ProjectId, s.Period, [s.Chain.ProjectId], null, Me, CancellationToken.None);
        Assert.Equal(2, coldKey.StaleResultsCount);
        Assert.Equal(coldKey.StaleResultsCount, warmKey.StaleResultsCount);

        clock.Advance(DocumentListSummaryStore.StaleCountsTtl + TimeSpan.FromSeconds(1));
        var after = await cached.SummarizeAsync(
            s.Chain.ProjectId, s.Period, [s.Chain.ProjectId], null, Me, CancellationToken.None);
        Assert.Equal(3, after.StaleResultsCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Різний_scope_читача_не_ділить_запис_а_звужений_не_отримує_теплий_лічильник_незвуженого()
    {
        var s = await CacheScenarioAsync();
        var counter = new DbCommandCounter();
        await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(counter).Options);
        using var cache = NewCache();
        var store = new DocumentListSummaryStore(counted, cache, new TestClock(Now.AddHours(2)));

        // Той самий користувач і проєкт: спершу незвужений - кеш теплий (2 / 1).
        var open = await SummaryAsync(store, s, restrictions: null, Me);
        Assert.Equal(2, open.StaleResultsCount);

        // ⛔ Звужений (проєкт зі схованим) НЕ отримує числа незвуженого: 0, а не 2.
        var narrowed = new SummaryRestrictions([(s.Chain.ProjectId, "HID")], [s.Chain.ProjectId]);
        var closed = await SummaryAsync(store, s, narrowed, Me);
        Assert.Equal(0, closed.StaleResultsCount);
        Assert.Equal(0, closed.StaleResultsMineCount);

        // Інший набір схованого аркуша без звуження проєкту: карта періоду спільна (вона без читача), а ЧИСЛО
        // рахується щоразу по видимих документах - агрегат + відбиток + 2 підрахунки, журнал правок не чіпається.
        var otherHidden = new SummaryRestrictions([(s.Chain.ProjectId, "OTHER")], []);
        var before = counter.Tally.Snapshot().Total;
        var partial = await SummaryAsync(store, s, otherHidden, Me);
        Assert.Equal(2, partial.StaleResultsCount);
        Assert.Equal(4, counter.Tally.Snapshot().Total - before);

        // Кожен читає своє й з теплого кешу.
        Assert.Equal(0, (await SummaryAsync(store, s, narrowed, Me)).StaleResultsCount);
        Assert.Equal(2, (await SummaryAsync(store, s, null, Me)).StaleResultsCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Користувачі_не_ділять_запис_мої_лічильник_свій_у_кожного()
    {
        var s = await CacheScenarioAsync();
        await using var db = s.Builder.CreateContext();
        using var cache = NewCache();
        var store = new DocumentListSummaryStore(db, cache, new TestClock(Now.AddHours(2)));

        Assert.Equal(1, (await SummaryAsync(store, s, null, Me)).StaleResultsMineCount);
        Assert.Equal(0, (await SummaryAsync(store, s, null, 424242)).StaleResultsMineCount);
        Assert.Equal(1, (await SummaryAsync(store, s, null, 62)).StaleResultsMineCount);
        Assert.Equal(0, (await SummaryAsync(store, s, null, null)).StaleResultsMineCount);
    }

    // ── інвалідація кешу лічильників епохою (власна правка видна одразу) ─────────────────────

    private EcrDbContext EpochContext(StaleCountsEpoch epoch, DbCommandCounter? counter = null)
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString);
        options.AddInterceptors(epoch);
        if (counter is not null)
        {
            options.AddInterceptors(counter);
        }

        return new EcrDbContext(options.Options);
    }

    private static CellChangeRecord EditRecord(CacheScenario s, long documentId)
        => new(
            Now.AddHours(1), new CellAddress(s.Chain.PeriodKey, 1, s.Chain.ColumnDefIds[1]), documentId, "R1",
            "1", "2", Me, "UserEdit", IsLateEdit: false, CorrelationId: null);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Власна_правка_видна_лічильнику_одразу_після_коміту_без_очікування_TTL()
    {
        var s = await CacheScenarioAsync();
        var epoch = new StaleCountsEpoch();
        await using var db = EpochContext(epoch);
        using var cache = NewCache();
        var store = new DocumentListSummaryStore(db, cache, new TestClock(Now.AddHours(2)), epoch);
        var audit = new AuditWriter(db, epoch);

        Assert.Equal(2, (await SummaryAsync(store, s, null, Me)).StaleResultsCount);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await audit.WriteCellChangesAsync([EditRecord(s, s.Fresh)], CancellationToken.None);

            // ⛔ До коміту епоха НЕ рухається: читач іншого з'єднання ще не бачить правки, кеш лишається чинним.
            Assert.Equal(2, (await SummaryAsync(store, s, null, Me)).StaleResultsCount);

            await transaction.CommitAsync();
        }

        // Після коміту - нове значення БЕЗ жодного просування годинника.
        var after = await SummaryAsync(store, s, null, Me);
        Assert.Equal(3, after.StaleResultsCount);
        Assert.Equal(2, after.StaleResultsMineCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відкат_транзакції_не_скидає_кеш_і_без_правок_у_межах_TTL_це_влучання()
    {
        var s = await CacheScenarioAsync();
        var epoch = new StaleCountsEpoch();
        var counter = new DbCommandCounter();
        await using var db = EpochContext(epoch, counter);
        using var cache = NewCache();
        var store = new DocumentListSummaryStore(db, cache, new TestClock(Now.AddHours(2)), epoch);
        var audit = new AuditWriter(db, epoch);

        Assert.Equal(2, (await SummaryAsync(store, s, null, Me)).StaleResultsCount);
        var epochBefore = epoch.Value;

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await audit.WriteCellChangesAsync([EditRecord(s, s.Fresh)], CancellationToken.None);
            await transaction.RollbackAsync();
        }

        Assert.Equal(epochBefore, epoch.Value);

        var queriesBefore = counter.Tally.Snapshot().Total;
        Assert.Equal(2, (await SummaryAsync(store, s, null, Me)).StaleResultsCount);

        // Агрегат + відбиток + 2 підрахунки (4): карти з кешу (промах дав би 6).
        Assert.Equal(4, counter.Tally.Snapshot().Total - queriesBefore);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перемикання_актуального_прогону_піднімає_епоху()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var epoch = new StaleCountsEpoch();
        await using var db = EpochContext(epoch);

        var version = await VersionAsync(db);
        var run = await RunAsync(db, chain, version, Now, [chain.DocumentId]);
        var before = epoch.Value;

        await new CalculationResultStore(db, new TestClock(Now), epoch)
            .SwitchCurrentRunAsync(run.Id, "{}", CancellationToken.None);

        Assert.True(epoch.Value > before);
    }

    /// <summary>
    /// R2-04 / Z5-02: перемикання актуального прогону піднімає епоху ЛИШЕ свого періоду. Доти воно піднімало
    /// глобальну, і кеш лічильників усіх періодів скидався після кожного завершеного перерахунку.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Z5-02")]
    public async Task Z5_02_перемикання_актуального_прогону_піднімає_епоху_лише_свого_періоду()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var epoch = new StaleCountsEpoch();
        await using var db = EpochContext(epoch);

        var version = await VersionAsync(db);
        var run = await RunAsync(db, chain, version, Now, [chain.DocumentId]);
        var own = epoch.ValueFor(chain.PeriodKey.Value);
        var otherPeriod = chain.PeriodKey.Value == 202608 ? 202607 : 202608;
        var otherBefore = epoch.ValueFor(otherPeriod);

        await new CalculationResultStore(db, new TestClock(Now), epoch)
            .SwitchCurrentRunAsync(run.Id, "{}", CancellationToken.None);

        Assert.True(epoch.ValueFor(chain.PeriodKey.Value) > own);
        Assert.Equal(otherBefore, epoch.ValueFor(otherPeriod));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читачі_що_працюють_під_час_коміту_після_нього_бачать_нове()
    {
        var s = await CacheScenarioAsync();
        var epoch = new StaleCountsEpoch();
        using var cache = NewCache();
        var clock = new TestClock(Now.AddHours(2));

        await using var writer = EpochContext(epoch);
        var audit = new AuditWriter(writer, epoch);
        using var stop = new CancellationTokenSource();

        // Кеш теплий ДО коміту (без цього тест нічого б не доводив: холодний кеш і так дає свіже).
        Assert.Equal(2, (await SummaryAsync(new DocumentListSummaryStore(writer, cache, clock, epoch), s, null, Me)).StaleResultsCount);

        // Читачі крутяться без упину (кожен зі своїм контекстом), поки йде коміт: старе значення кладуть лише під старою епохою.
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var db = EpochContext(epoch);
            var store = new DocumentListSummaryStore(db, cache, clock, epoch);
            while (!stop.IsCancellationRequested)
            {
                await SummaryAsync(store, s, null, Me);
            }
        })).ToArray();

        await using (var transaction = await writer.Database.BeginTransactionAsync())
        {
            await audit.WriteCellChangesAsync([EditRecord(s, s.Fresh)], CancellationToken.None);
            await transaction.CommitAsync();
        }

        // Читання, що ПОЧАЛИСЬ після коміту, мусять бачити 3 - незалежно від того, що встигли покласти читачі.
        var seen = new int[8];
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var db = EpochContext(epoch);
            seen[i] = (await SummaryAsync(new DocumentListSummaryStore(db, cache, clock, epoch), s, null, Me)).StaleResultsCount;
        }));

        await stop.CancelAsync();
        await Task.WhenAll(readers);

        Assert.All(seen, count => Assert.Equal(3, count));
    }

    /// <summary>
    /// AN-108 / P2-01: епоха — по періоду. Правка в іншому періоді не скидає кеш лічильників цього: за глобальної
    /// епохи 100 редакторів піднімали її 10–20 разів на секунду, і кеш не влучав ніколи.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_в_іншому_періоді_не_скидає_кеш_лічильників_цього_періоду()
    {
        var s = await CacheScenarioAsync();
        var epoch = new StaleCountsEpoch();
        var counter = new DbCommandCounter();
        await using var db = EpochContext(epoch, counter);
        using var cache = NewCache();
        var store = new DocumentListSummaryStore(db, cache, new TestClock(Now.AddHours(2)), epoch);
        var otherPeriod = s.Period == 202608 ? 202607 : 202608;

        Assert.Equal(2, (await SummaryAsync(store, s, null, Me)).StaleResultsCount);
        var own = epoch.ValueFor(s.Period);

        epoch.Invalidate(db, [otherPeriod]);

        Assert.Equal(own, epoch.ValueFor(s.Period));
        Assert.True(epoch.ValueFor(otherPeriod) > 0);

        var queriesBefore = counter.Tally.Snapshot().Total;
        Assert.Equal(2, (await SummaryAsync(store, s, null, Me)).StaleResultsCount);

        // Агрегат + відбиток + 2 підрахунки (4): карти з кешу.
        Assert.Equal(4, counter.Tally.Snapshot().Total - queriesBefore);

        // Глобальний підйом (невідомий період) скидає і цей.
        epoch.Invalidate(db);
        Assert.True(epoch.ValueFor(s.Period) > own);
    }

    // ── LP-45, етап 2: карта застарілості періоду в кеші; по ТЕСТУ НА КОЖНУ ПОДІЮ інвалідації ──────────
    //
    // Мутаційні докази (detached-worktree): прибрати `staleEpoch?.Invalidate` у AuditWriter - червоний
    // `Подія_запис_комірок...`; прибрати `hit.Fingerprint == fingerprint` у StaleResultsMapCache - червоні
    // `Подія_прогін_завершено_іншим_процесом...`, `Подія_правка_довідника...`, `Подія_нове_ребро...`.

    private sealed record MapRig(
        TestDocumentBuilder Builder, TestDocument Chain, int Period, long A, long B, long Fresh, int Version,
        Microsoft.Extensions.Caching.Memory.MemoryCache Cache, TestTimeSource Clock, StaleCountsEpoch Epoch);

    /// <summary>Годинник кешу: фіксований, не рухається (інвалідація має спрацьовувати БЕЗ TTL).</summary>
    private sealed class TestTimeSource(DateTime now) : Ecr.Domain.Abstractions.IClock
    {
        public DateTime UtcNow { get; } = now;
    }

    /// <summary>Два застарілі документи (A, B), один свіжий; спільні кеш, годинник і епоха процесу.</summary>
    private async Task<MapRig> RigAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var a = await AddDocumentAsync(db, chain, "MP-A");
        var b = await AddDocumentAsync(db, chain, "MP-B");
        var fresh = await AddDocumentAsync(db, chain, "MP-FRESH");
        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [a, b, fresh]);
        await EditAsync(a, period, chain.ColumnDefIds[1], Now.AddHours(1), user: Me);
        await EditAsync(b, period, chain.ColumnDefIds[1], Now.AddHours(1), user: 62);

        return new MapRig(builder, chain, period, a, b, fresh, version, NewCache(), new TestTimeSource(Now.AddHours(2)), new StaleCountsEpoch());
    }

    private DocumentStore CachedStore(MapRig r, EcrDbContext db)
        => new(db, r.Cache, r.Clock, r.Epoch);

    private static async Task<HashSet<long>> StaleOnPageAsync(DocumentStore store, MapRig r, IReadOnlyCollection<int>? visible = null)
    {
        var page = await store.ListAsync(
            projectId: null, new PeriodKeyFilter(r.Period), new DocumentListFilter(null, null, ResultsStale: true),
            new CursorRequest(Limit: 200), visible ?? [r.Chain.ProjectId], CancellationToken.None);
        return [.. page.Items.Select(d => d.Id)];
    }

    private static async Task<bool?> FlagAsync(DocumentStore store, MapRig r, long documentId)
    {
        var page = await store.ListAsync(
            r.Chain.ProjectId, new PeriodKeyFilter(r.Period), default, new CursorRequest(Limit: 200),
            [r.Chain.ProjectId], CancellationToken.None);
        return page.Items.Single(d => d.Id == documentId).ResultsStale;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_запис_комірок_PATCH_видимий_наступному_запиту_без_TTL_і_не_раніше_коміту()
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        await using var db = EpochContext(r.Epoch);
        var store = CachedStore(r, db);
        var audit = new AuditWriter(db, r.Epoch);

        Assert.Equal([r.A, r.B], (await StaleOnPageAsync(store, r)).Order());
        Assert.False(await FlagAsync(store, r, r.Fresh));

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await audit.WriteCellChangesAsync([EditRecord(new CacheScenario(r.Builder, r.Chain, r.Fresh, r.Period), r.Fresh)], CancellationToken.None);

            // До коміту іншим читачам правки не видно, і кеш лишається чинним.
            Assert.False(await FlagAsync(store, r, r.Fresh));
            await transaction.CommitAsync();
        }

        Assert.True(await FlagAsync(store, r, r.Fresh));
        Assert.Contains(r.Fresh, await StaleOnPageAsync(store, r));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_перемикання_актуального_прогону_знімає_застарілість_у_наступному_запиті()
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        await using var db = EpochContext(r.Epoch);
        var store = CachedStore(r, db);

        Assert.True(await FlagAsync(store, r, r.A));

        var run = await PendingRunAsync(db, r.Chain, r.Version, Now.AddHours(2), [r.A]);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await new CalculationResultStore(db, new TestClock(Now), r.Epoch)
                .SwitchCurrentRunAsync(run.Id, "{}", CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync();
        }

        // Новий прогін стартував після правок, а старий знято (прогін проєкту): жоден документ не застарілий.
        Assert.False(await FlagAsync(store, r, r.A));
        Assert.False(await FlagAsync(store, r, r.B));
        Assert.Empty(await StaleOnPageAsync(store, r));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_прогін_завершено_іншим_процесом_видно_без_епохи_цього_процесу()
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        await using var reader = EpochContext(r.Epoch);
        var store = CachedStore(r, reader);
        Assert.True(await FlagAsync(store, r, r.A));
        var epochBefore = r.Epoch.ValueFor(r.Period);

        // «Worker»: своя епоха, свій контекст, кеш читача про це не знає.
        var workerEpoch = new StaleCountsEpoch();
        await using var worker = EpochContext(workerEpoch);
        var run = await PendingRunAsync(worker, r.Chain, r.Version, Now.AddHours(2), [r.A]);
        await using (var transaction = await worker.Database.BeginTransactionAsync())
        {
            await new CalculationResultStore(worker, new TestClock(Now), workerEpoch)
                .SwitchCurrentRunAsync(run.Id, "{}", CancellationToken.None);
            await worker.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.Equal(epochBefore, r.Epoch.ValueFor(r.Period));
        Assert.False(await FlagAsync(store, r, r.A));
    }

    private async Task<(int RegistryId, MapRig Rig)> RegistryRigAsync(DateTime? changedAt, bool withEdge)
    {
        var r = await RigAsync();
        await using var db = r.Builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var registry = new RegistryDef(EcrCode.Create($"MR{tag}"), Name("Registry"), isTemporal: false);
        if (changedAt is { } at)
        {
            registry.MarkDataChanged(at);
        }

        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync(CancellationToken.None);
        if (withEdge)
        {
            db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(r.Version, "F1", registry.Id, "X"));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return (registry.Id, r);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_правка_довідника_у_базі_видна_наступному_запиту_без_TTL()
    {
        // Документ Fresh читає довідник через методологію; довідник змінено ДО прогону - не застарів.
        var (registryId, r) = await RegistryRigAsync(Now.AddHours(-1), withEdge: true);
        using var _ = r.Cache;
        await using var db = EpochContext(r.Epoch);
        var store = CachedStore(r, db);
        Assert.False(await FlagAsync(store, r, r.Fresh));

        // Інший процес міняє довідник ПІСЛЯ початку прогону (так робить UnitOfWork.RestampRegistryDataChanges).
        await using (var other = r.Builder.CreateContext())
        {
            await other.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.RegistryDef SET DataChangedAt = {Now.AddHours(1)} WHERE Id = {registryId}");
        }

        Assert.True(await FlagAsync(store, r, r.Fresh));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_нове_ребро_використання_довідника_видне_наступному_запиту_без_TTL()
    {
        // Довідник змінено після прогону, але методологія його ще не читає - документ не застарів.
        var (registryId, r) = await RegistryRigAsync(Now.AddHours(1), withEdge: false);
        using var _ = r.Cache;
        await using var db = EpochContext(r.Epoch);
        var store = CachedStore(r, db);
        Assert.False(await FlagAsync(store, r, r.Fresh));

        await using (var other = r.Builder.CreateContext())
        {
            other.RegistryUses.Add(RegistryUse.ForMethodologyFormula(r.Version, "F1", registryId, "X"));
            await other.SaveChangesAsync(CancellationToken.None);
        }

        Assert.True(await FlagAsync(store, r, r.Fresh));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_видалення_документа_і_прогонів_не_лишає_його_у_відповіді_і_лічильнику()
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        await using var db = EpochContext(r.Epoch);
        var store = CachedStore(r, db);
        var summaries = new DocumentListSummaryStore(db, r.Cache, r.Clock, r.Epoch);

        Assert.Equal(2, (await summaries.SummarizeAsync(
            r.Chain.ProjectId, r.Period, [r.Chain.ProjectId], null, Me, CancellationToken.None)).StaleResultsCount);
        Assert.Contains(r.A, await StaleOnPageAsync(store, r));

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await new DocumentDeletionStore(db).DeleteAsync(r.A, CancellationToken.None);
            await transaction.CommitAsync();
        }

        Assert.DoesNotContain(r.A, await StaleOnPageAsync(store, r));
        var after = await summaries.SummarizeAsync(
            r.Chain.ProjectId, r.Period, [r.Chain.ProjectId], null, Me, CancellationToken.None);
        Assert.Equal(1, after.StaleResultsCount);
        Assert.Equal(0, after.StaleResultsMineCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_створення_документа_нового_без_результатів_не_застарілий_а_лічильник_той_самий()
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        await using var db = EpochContext(r.Epoch);
        var store = CachedStore(r, db);
        var summaries = new DocumentListSummaryStore(db, r.Cache, r.Clock, r.Epoch);

        Assert.Equal(2, (await summaries.SummarizeAsync(
            r.Chain.ProjectId, r.Period, [r.Chain.ProjectId], null, Me, CancellationToken.None)).StaleResultsCount);

        var created = await AddDocumentAsync(db, r.Chain, "MP-NEW");

        Assert.False(await FlagAsync(store, r, created));
        Assert.Equal(2, (await summaries.SummarizeAsync(
            r.Chain.ProjectId, r.Period, [r.Chain.ProjectId], null, Me, CancellationToken.None)).StaleResultsCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_зміна_доступу_між_запитами_кеш_карти_не_віддає_чужих_документів()
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        var foreign = await r.Builder.BuildAsync(ct: CancellationToken.None);
        await using var db = EpochContext(r.Epoch);
        var store = CachedStore(r, db);
        var summaries = new DocumentListSummaryStore(db, r.Cache, r.Clock, r.Epoch);

        // Читач із доступом до проєкту прогріває карту.
        Assert.Equal([r.A, r.B], (await StaleOnPageAsync(store, r)).Order());
        Assert.Equal(2, (await summaries.SummarizeAsync(
            null, r.Period, [r.Chain.ProjectId], null, Me, CancellationToken.None)).StaleResultsCount);

        // Той самий період, ТЕПЛА карта, але доступ лише до чужого проєкту: жодного документа першого.
        var narrowedPage = await store.ListAsync(
            null, new PeriodKeyFilter(r.Period), new DocumentListFilter(null, null, ResultsStale: true),
            new CursorRequest(Limit: 200), [foreign.ProjectId], CancellationToken.None);
        Assert.DoesNotContain(narrowedPage.Items, d => d.Id == r.A || d.Id == r.B);
        Assert.Equal(0, (await summaries.SummarizeAsync(
            null, r.Period, [foreign.ProjectId], null, Me, CancellationToken.None)).StaleResultsCount);

        // Доступ без проєктів (порожня межа): порожньо, а не «усе з кешу».
        var none = await store.ListAsync(
            null, new PeriodKeyFilter(r.Period), new DocumentListFilter(null, null, ResultsStale: true),
            new CursorRequest(Limit: 200), Array.Empty<int>(), CancellationToken.None);
        Assert.Empty(none.Items);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кеш_карти_дає_ті_самі_прапорці_що_без_кешу_і_запитів_не_більше_ніж_документів_немає()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var period = chain.PeriodKey.Value;
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var documents = new List<long>();
        for (var i = 0; i < 55; i++)
        {
            documents.Add(await AddDocumentAsync(db, chain, $"STM{tag}-{i:00}"));
        }

        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, [.. documents]);
        for (var i = 0; i < documents.Count; i += 2)
        {
            await EditAsync(documents[i], period, chain.ColumnDefIds[1], Now.AddMinutes(10 + i));
        }

        var filter = new DocumentListFilter(null, null, Query: $"STM{tag}");
        var plain = (await new DocumentStore(db).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(period), filter, new CursorRequest(Limit: 50), null, CancellationToken.None)).Items;

        using var cache = NewCache();
        var clock = new TestTimeSource(Now.AddHours(2));
        var counter = new DbCommandCounter();
        await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(counter).Options);
        var cached = new DocumentStore(counted, cache, clock, new StaleCountsEpoch());

        var cold = (await cached.ListAsync(
            chain.ProjectId, new PeriodKeyFilter(period), filter, new CursorRequest(Limit: 50), null, CancellationToken.None)).Items;
        var coldTotal = counter.Tally.Snapshot().Total;
        var warm = (await cached.ListAsync(
            chain.ProjectId, new PeriodKeyFilter(period), filter, new CursorRequest(Limit: 50), null, CancellationToken.None)).Items;
        var warmTotal = counter.Tally.Snapshot().Total - coldTotal;

        foreach (var rows in new[] { cold, warm })
        {
            Assert.Equal(plain.Count, rows.Count);
            for (var i = 0; i < plain.Count; i++)
            {
                Assert.Equal(plain[i].Id, rows[i].Id);
                Assert.Equal(plain[i].ResultsStale, rows[i].ResultsStale);
                Assert.Equal(plain[i].ResultsStaleSince, rows[i].ResultsStaleSince);
            }
        }

        // Запит сторінки не робить запитів на документ: на теплому кеші - рівно на один (відбиток) менше, ніж на
        // холодному, а холодний не залежить від числа документів (50 рядків - те саме число команд, що й 5).
        Assert.Equal(coldTotal - 1, warmTotal);

        var counterSmall = new DbCommandCounter();
        await using var small = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(counterSmall).Options);
        using var smallCache = NewCache();
        await new DocumentStore(small, smallCache, clock, new StaleCountsEpoch()).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(period), filter, new CursorRequest(Limit: 5), null, CancellationToken.None);
        Assert.Equal(coldTotal, counterSmall.Tally.Snapshot().Total);
    }
    // ── LP-45 етап 2 (R2/R3): картка без карти, single-flight, деградація при переповненні ──────────────────
    //
    // Мутації: у StaleResultsMapCache.GetOneAsync прибрати приєднання слідувачів (завжди лідер) - червоний
    // Single_flight...; у DocumentStore.FindAsync повернути StaleResultsBatchAsync - червоний Картка...;
    // у LoadAsync прибрати `return null` при переповненні (кинути) - червоний Переповнення...

    /// <summary>Записує тексти команд і (опційно) тримає запит карти, доки всі читачі не пройшли відбиток.</summary>
    private sealed class MapGateInterceptor(int readers = 0) : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int fingerprints;
        private int maps;

        public System.Collections.Concurrent.ConcurrentBag<string> Texts { get; } = [];

        public int MapQueries => Volatile.Read(ref maps);

        public int FingerprintQueries => Volatile.Read(ref fingerprints);

        /// <summary>Відпускає утримані запити карти вручну (режим без автоматичного випуску).</summary>
        public void Release() => gate.TrySetResult();

        /// <summary>Чекає, поки запитів карти стане не менше <paramref name="count"/> (запобіжник від зависання).</summary>
        public async Task WaitMapQueriesAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (MapQueries < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Запитів карти {MapQueries}, очікувалось {count}.");
                }

                await Task.Delay(10);
            }
        }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Texts.Add(command.CommandText);

            if (command.CommandText.Contains("CHECKSUM_AGG", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref fingerprints) >= readers)
                {
                    gate.TrySetResult();
                }
            }
            else if (command.CommandText.Contains("MIN(t.At)", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref maps);
                if (readers > 0)
                {
                    // Не таймінг-асерт: лише запобіжник від зависання, якщо відбитки не набралися.
                    await gate.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
                }
            }

            return result;
        }
    }

    private EcrDbContext InterceptedContext(Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor interceptor, StaleCountsEpoch? epoch = null)
    {
        var options = new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).AddInterceptors(interceptor);
        if (epoch is not null)
        {
            options.AddInterceptors(epoch);
        }

        return new EcrDbContext(options.Options);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Картка_документа_ходить_вузьким_запитом_по_id_а_не_картою_періоду()
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        var recorder = new MapGateInterceptor();
        await using var db = InterceptedContext(recorder, r.Epoch);
        var store = CachedStore(r, db);

        var card = await store.FindAsync(r.A, new PeriodKeyFilter(r.Period), CancellationToken.None);

        Assert.True(card!.ResultsStale);

        // Жодного відбитка (карта не зачіпалась), а запит застарілості - один, і з фільтром по id.
        Assert.DoesNotContain(recorder.Texts, x => x.Contains("CHECKSUM_AGG", StringComparison.Ordinal));
        var stale = recorder.Texts.Where(x => x.Contains("MIN(t.At)", StringComparison.Ordinal)).ToList();
        Assert.Single(stale);
        Assert.True(
            stale[0].Contains("OPENJSON", StringComparison.OrdinalIgnoreCase) || stale[0].Contains(" IN ", StringComparison.Ordinal)
            || stale[0].Contains("DocumentId] =", StringComparison.Ordinal) || stale[0].Contains("DocumentId =", StringComparison.Ordinal),
            stale[0]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Single_flight_N_одночасних_читачів_одного_ключа_запускають_один_запит_карти()
    {
        const int readers = 6;
        var r = await RigAsync();
        using var _ = r.Cache;
        var gate = new MapGateInterceptor(readers);

        var tasks = Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
        {
            await using var db = InterceptedContext(gate, r.Epoch);
            return await StaleOnPageAsync(CachedStore(r, db), r);
        })).ToArray();

        var pages = await Task.WhenAll(tasks);

        Assert.All(pages, page => Assert.Equal([r.A, r.B], page.Order()));
        // Відбиток читається двічі на читача (фільтр і позначка рядків); карта - один раз на всіх.
        Assert.Equal(readers * 2, gate.FingerprintQueries);
        Assert.Equal(1, gate.MapQueries);
    }

    private async Task ReaderAfterChangeDoesNotJoinOldLoadAsync(Func<MapRig, Task> change)
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        var gate = new MapGateInterceptor(int.MaxValue);

        async Task<HashSet<long>> ReadAsync()
        {
            await using var db = InterceptedContext(gate, r.Epoch);
            return await StaleOnPageAsync(CachedStore(r, db), r);
        }

        var first = Task.Run(ReadAsync);
        await gate.WaitMapQueriesAsync(1);

        // «Коміт» змінив стан, поки перше завантаження утримується: другий читач НЕ має приєднатись до нього.
        await change(r);
        var second = Task.Run(ReadAsync);
        await gate.WaitMapQueriesAsync(2);

        gate.Release();
        await Task.WhenAll(first, second);
        Assert.True(gate.MapQueries >= 2);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public Task Single_flight_читач_після_підйому_епохи_не_приєднується_до_старого_завантаження()
        => ReaderAfterChangeDoesNotJoinOldLoadAsync(r =>
        {
            r.Epoch.Bump(r.Period);
            return Task.CompletedTask;
        });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public Task Single_flight_читач_після_зміни_відбитка_не_приєднується_до_старого_завантаження()
        => ReaderAfterChangeDoesNotJoinOldLoadAsync(async r =>
        {
            // Інший процес додав довідник: відбиток cfg.RegistryDef змінився, епоха цього процесу - ні.
            await using var other = r.Builder.CreateContext();
            var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            other.RegistryDefs.Add(new RegistryDef(EcrCode.Create($"FP{tag}"), Name("Registry"), isTemporal: false));
            await other.SaveChangesAsync(CancellationToken.None);
        });
    /// <summary>Мінімальний логер, що рахує попередження.</summary>
    private sealed class WarningCounter<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public int Warnings;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Interlocked.Increment(ref Warnings);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Переповнення_карти_не_кидає_а_деградує_до_прямого_запиту_з_тим_самим_результатом()
    {
        var r = await RigAsync();
        using var _ = r.Cache;
        await using var db = EpochContext(r.Epoch);
        var log = new WarningCounter<DocumentStore>();
        var summaryLog = new WarningCounter<DocumentListSummaryStore>();

        // Стеля 1 при двох застарілих документах: карта «переповнена».
        var store = new DocumentStore(db, r.Cache, r.Clock, r.Epoch, log) { StaleMapMaxDocuments = 1 };
        var summaries = new DocumentListSummaryStore(db, r.Cache, r.Clock, r.Epoch, summaryLog) { StaleMapMaxDocuments = 1 };

        // Фільтр і позначка рядка - прямий запит, результат той самий.
        Assert.Equal([r.A, r.B], (await StaleOnPageAsync(store, r)).Order());
        Assert.True(await FlagAsync(store, r, r.A));
        Assert.False(await FlagAsync(store, r, r.Fresh));

        // Зведення - як було: 2 застарілі, 1 «мій».
        var summary = await summaries.SummarizeAsync(
            r.Chain.ProjectId, r.Period, [r.Chain.ProjectId], null, Me, CancellationToken.None);
        Assert.Equal(2, summary.StaleResultsCount);
        Assert.Equal(1, summary.StaleResultsMineCount);

        // Перше переповнення дає РІВНО одне Warning (прапорець - на кеш, тобто на процес); повторне - ще нуль.
        Assert.Equal(1, log.Warnings + summaryLog.Warnings);

        r.Epoch.Bump(r.Period);
        Assert.Equal([r.A, r.B], (await StaleOnPageAsync(store, r)).Order());
        Assert.Equal(1, log.Warnings + summaryLog.Warnings);
    }
    // ── допоміжне ────────────────────────────────────────────────────────────────────────

    private static async Task<DocumentSummary> CardAsync(EcrDbContext db, TestDocument chain, int period)
        => (await new DocumentStore(db).FindAsync(chain.DocumentId, new PeriodKeyFilter(period), CancellationToken.None))!;

    private static async Task<IReadOnlyList<DocumentSummary>> ListAsync(
        EcrDbContext db, TestDocument chain, int period, DocumentListFilter filter)
        => (await new DocumentStore(db).ListAsync(
            chain.ProjectId, new PeriodKeyFilter(period), filter, new CursorRequest(Limit: 200),
            visibleProjectIds: null, CancellationToken.None)).Items;

    /// <summary>Прогін, що ще НЕ актуальний (стан «Running»), з числами для документів.</summary>
    private static async Task<CalculationRun> PendingRunAsync(
        EcrDbContext db, TestDocument chain, int versionId, DateTime startedAt, long[] documents)
    {
        var run = new CalculationRun(chain.ProjectId, chain.PeriodKey.Value, triggeredByUserId: null, startedAt, null);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync(CancellationToken.None);
        foreach (var documentId in documents)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {versionId},
                        {chain.PeriodKey.Value}, {documentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
                """);
        }

        return run;
    }
    private static async Task<long> AddDocumentAsync(EcrDbContext db, TestDocument chain, string key)
    {
        var document = new Document(chain.ProjectId, $"{key}-{chain.ProjectId}", 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync(CancellationToken.None);
        return document.Id;
    }

    /// <summary>Нова методологія з однією версією (код унікальний у базі).</summary>
    private static async Task<int> VersionAsync(EcrDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(EcrCode.Create($"SM_{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync(CancellationToken.None);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync(CancellationToken.None);
        return version.Id;
    }

    /// <summary>Актуальний прогін, що стартував о <paramref name="startedAt"/> і дав число кожному з документів.</summary>
    private static async Task<CalculationRun> RunAsync(
        EcrDbContext db, TestDocument chain, int versionId, DateTime startedAt, long[] documents,
        long? runDocumentId = null)
    {
        var run = new CalculationRun(
            chain.ProjectId, chain.PeriodKey.Value, triggeredByUserId: null, startedAt, runDocumentId);
        run.Complete("Succeeded", startedAt.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync(CancellationToken.None);
        foreach (var documentId in documents)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO calc.CalculationResult
                    (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {versionId},
                        {chain.PeriodKey.Value}, {documentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
                """);
        }

        return run;
    }

    /// <summary>Рядок журналу — прямим ADO: <c>aud.*</c> поза моделлю EF.</summary>
    private async Task EditAsync(
        long documentId, int periodKey, int columnDefId, DateTime changedAt, int user = 1, string origin = "UserEdit")
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES (@at, @period, @document, 1, N'R1', @column, N'1', N'2', @user, @origin, 0);
            """;
        command.Parameters.AddWithValue("@at", changedAt);
        command.Parameters.AddWithValue("@period", periodKey);
        command.Parameters.AddWithValue("@document", documentId);
        command.Parameters.AddWithValue("@column", columnDefId);
        command.Parameters.AddWithValue("@user", user);
        command.Parameters.AddWithValue("@origin", origin);

        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
