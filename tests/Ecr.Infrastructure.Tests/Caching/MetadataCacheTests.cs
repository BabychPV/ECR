using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Xunit;

namespace Ecr.Infrastructure.Tests.Caching;

/// <summary>
/// Ключ <c>v{id}:r{rev}</c> робить інвалідацію непотрібною: презентаційна
/// правка створює новий ключ, а не псує старий (D-16).
/// </summary>
/// <remarks>
/// ⚠ Тести позначені <c>Integration</c>, хоча в <c>06-tests.md</c> цієї
/// позначки не було (`Q-043`). Причина: <see cref="EcrDbContext"/> описує
/// модель SQL Server — <c>nvarchar(max)</c>, <c>rowversion</c>, тригери,
/// послідовності. На SQLite вона не створюється (<c>near "max": syntax
/// error</c>), а робити її провайдер-нейтральною означало б тестувати не ту
/// модель, яка працює в проді.
/// </remarks>
[Collection("SqlServer")]
public sealed class MetadataCacheTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]

    // ⛔ Трейт `ФВ-2.12` знято. Він стояв тут із самого початку і був хибним:
    // вимога про ЗВ'ЯЗКИ МІЖ ТАБЛИЦЯМИ (`TableRelationDef`) була позначена
    // покритою тестом кешу метаданих, який зв'язків не читає і не створює.
    // Матриця трасування показувала зелене там, де механізму не існувало
    // взагалі — той самий клас, що й `ФВ-9.15` цієї ночі. Вимогу тепер
    // покривають `TableRelationDefTests` і `TableRelationTests`.
    public async Task Повторне_читання_не_звертається_до_БД()
    {
        var doc = await ArrangeAsync();
        var memory = new MemoryCache(new MemoryCacheOptions());

        var first = new List<string>();
        await using (var db = CreateContext(first))
        {
            await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        var second = new List<string>();
        await using (var db = CreateContext(second))
        {
            await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        // ⚠ Точне формулювання: СТРУКТУРА вдруге не читається. Один запит
        // лишається — по PresentationRevision, і прибрати його не можна: саме
        // він робить ключ v{id}:r{rev} чесним. Без нього кеш віддавав би
        // застарілий знімок після презентаційної правки — тобто рівно ту
        // проблему когерентності, заради усунення якої ключ і придуманий.
        // ⚠ Шість, а не п'ять: `W8` додав шостий запит — ПРАВИЛА ВАЛІДАЦІЇ.
        // Знімок їх не вантажив узагалі, і `ValidateDocumentHandler`, який
        // читає рівно `table.ValidationRules` цього знімка, відповідав
        // «зауважень немає» на будь-яких даних при будь-яких заведених
        // правилах (директива №09 `W8`, `S-19`). Число тут навмисно точне:
        // «менше або дорівнює» пропустило б і повернення N+1.
        // ⚠ Сім, а не шість: інтеграція `W6`/`W8` додала сьомий запит —
        // ФОРМУЛИ ШАБЛОНУ (`Q-158`/`Q-160`, той самий клас прогалини, що й
        // правила валідації). `RecalculationService` бере формули рівно
        // звідси, і без цього запиту жодна формула шаблону не рахувалася.
        // ⚠ Вісім, а не сім: фундамент шапки документа додав восьмий запит —
        // ПОЛЯ ШАПКИ (`HeaderFieldDef`), фільтровані за TemplateVersionId, а
        // не за tableIds, як решта семи. `GetHeaderFieldDefsHandler` бере
        // поля шапки рівно з `snapshot.HeaderFields`.
        Assert.Equal(8, first.Count);

        // ⛔ НУЛЬ, а не один (`RD-05`). Абзац вище — про те, як було до
        // директиви №14: «один запит лишається, і прибрати його не можна».
        // Прибрати його виявилося можна, але не безкоштовно — ціною стелі
        // несвіжості в 5 с (`CacheLifetimes.DefaultRevision`), протягом яких
        // ревізія не перечитується. Що саме за це купується, видно тут: на
        // один зріз таблиці припадає ≥ 2 виклики `GetAsync`, тобто ≥ 2
        // звернення до `cfg.TemplateVersion` на кожен `GET` зрізу — і всі
        // вони зникають.
        //
        // ⚠ Мутація: у `CacheLifetimes` поставити `DefaultRevision` нулем
        // (мемоїзація вимкнена) — число стає 1, як було.
        Assert.Empty(second);
    }

    /// <summary>
    /// Вікно мемоїзації скінченне: після нього ревізія перечитується.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього тесту попередній був би зеленим і в разі, якби ревізію
    /// закешували НАЗАВЖДИ — а це вже не «стеля несвіжості 5 с», а кеш, що
    /// ніколи не бачить презентаційних правок. Нуль звернень і «перестало
    /// працювати» — одне й те саме число.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ревізія_перечитується_після_спливу_вікна()
    {
        var doc = await ArrangeAsync();
        var clock = new ManualClock();
        var memory = new MemoryCache(new MemoryCacheOptions { Clock = clock });

        await using (var db = CreateContext([]))
        {
            await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        var inside = new List<string>();
        await using (var db = CreateContext(inside))
        {
            await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromSeconds(6));

        var outside = new List<string>();
        await using (var db = CreateContext(outside))
        {
            await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        Assert.Empty(inside);

        // Рівно одне: ревізія. Знімок лежить під тим самим ключем — вікно
        // ревізії не має права перебудовувати структуру.
        Assert.Single(outside);
    }

    /// <summary>
    /// Явна інвалідація діє НЕГАЙНО, а не через п'ять секунд.
    /// </summary>
    /// <remarks>
    /// ⛔ Стеля несвіжості в 5 с придатна для презентаційної правки чернетки і
    /// НЕпридатна для <c>Publish</c> і міграції — рівно тих випадків, заради
    /// яких <see cref="MetadataCache.InvalidateAsync"/> узагалі існує. Якби
    /// мемоїзоване число там не знімалося, явна інвалідація мовчки не діяла б
    /// своє вікно: знімки зняті, а ключ обчислюється зі старої ревізії, тобто
    /// віддається та сама стара структура.
    ///
    /// ⚠ Мутація: прибрати `memory.Remove(RevisionKey(...))` з
    /// `InvalidateAsync` — `after` дорівнює `before`, тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Інвалідація_знімає_мемоїзовану_ревізію()
    {
        var doc = await ArrangeAsync();
        var memory = new MemoryCache(new MemoryCacheOptions());

        string before, after;
        await using (var db = CreateContext([]))
        {
            before = (await new MetadataCache(memory, db)
                .GetAsync(doc.TemplateVersionId, CancellationToken.None)).CacheKey;
        }

        await BumpRevisionAsync(doc.TemplateVersionId);

        await using (var db = CreateContext([]))
        {
            await new MetadataCache(memory, db)
                .InvalidateAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        await using (var db = CreateContext([]))
        {
            after = (await new MetadataCache(memory, db)
                .GetAsync(doc.TemplateVersionId, CancellationToken.None)).CacheKey;
        }

        Assert.EndsWith(":r0", before, StringComparison.Ordinal);
        Assert.EndsWith(":r1", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Стандарт доказу `RD-05`:</b> 50 одночасних <c>GetAsync</c> на
    /// холодному ключі — <c>LoadAsync</c> рівно один раз.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Як цей тест не став хибнозеленим.</b> «Одна побудова» виходить і
    /// без злиття, якщо перший виклик устигає завершитися до старту решти.
    /// Тут цього статися не може, і не через таймаути: ревізія ПРОГРІВАЄТЬСЯ
    /// заздалегідь (перший <c>GetAsync</c>, після якого з кешу знімається
    /// ЛИШЕ знімок), тож кожен із 50 викликів доходить до злиття
    /// СИНХРОННО — без жодного <c>await</c> по дорозі. Перший стає власником
    /// і зависає на першому ж запиті <c>LoadAsync</c>, а решта 49 стають у
    /// чергу, поки той потік ще всередині циклу. Планувальник тут ні на що не
    /// впливає.
    /// </para>
    /// <para>
    /// ⚠ <b>Міряється число, а не час.</b> Лічильник — спільний
    /// <see cref="DbCommandCounter"/> на всі 50 контекстів (`MS-01`).
    /// Очікування — рівно 6: один <c>LoadAsync</c> (аркуші, таблиці, колонки,
    /// рядки, правила, формули). Ревізії в цьому числі немає взагалі — вона
    /// мемоїзована прогрівом, і це друга половина `RD-05`.
    /// </para>
    /// <para>
    /// ⚠ <b>Мутація:</b> у <c>MetadataCache.GetAsync</c> замінити виклик
    /// <c>_flight.RunAsync(...)</c> на прямий <c>BuildAsync(...)</c> —
    /// <b>виміряно 144 звернення замість 6</b>, тобто 24 побудови знімка
    /// замість однієї, і <c>Assert.Same</c> падає на 49 з 50. Число саме
    /// 144, а не 300, і причина чесна: частина з п'ятдесяти встигає
    /// побачити вже покладений знімок — стеля тут 50 побудов, а не рівно 50.
    /// Твердження це не послаблює: 24 &gt; 1 із запасом, а мутація валить
    /// тест детерміновано. У .NET мутацію треба ПЕРЕЗБИРАТИ.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пʼятдесят_одночасних_читань_холодного_ключа_будують_знімок_один_раз()
    {
        const int Parallel = 50;

        var doc = await ArrangeAsync();
        var memory = new MemoryCache(new MemoryCacheOptions());
        var flight = new SingleFlight<TemplateVersionSnapshot>();
        var counter = new DbCommandCounter();

        // Прогрів РЕВІЗІЇ, і лише її: знімок одразу знімається з кешу, тож
        // ключ лишається холодним. Саме це й робить вхід у злиття синхронним.
        await using (var warm = CreateContext([]))
        {
            var warmed = await new MetadataCache(memory, warm, lifetimes: null, flight)
                .GetAsync(doc.TemplateVersionId, CancellationToken.None);
            memory.Remove(warmed.CacheKey);
        }

        var contexts = new List<EcrDbContext>(Parallel);
        var tasks = new Task<TemplateVersionSnapshot>[Parallel];

        try
        {
            for (var i = 0; i < Parallel; i++)
            {
                contexts.Add(CreateCountingContext(counter));
            }

            counter.Tally.Reset();

            // ⛔ Виклики видаються ОДИН ЗА ОДНИМ і не чекаються: кожен доходить
            // до словника польотів синхронно, поки побудова власника висить на
            // першому запиті до бази.
            for (var i = 0; i < Parallel; i++)
            {
                tasks[i] = new MetadataCache(memory, contexts[i], lifetimes: null, flight)
                    .GetAsync(doc.TemplateVersionId, CancellationToken.None);
            }

            var snapshots = await Task.WhenAll(tasks);
            var seen = counter.Tally.Snapshot();

            // Сім — це рівно один LoadAsync (шість структурних запитів і
            // сьомий — поля шапки документа, HeaderFieldDef). Без злиття
            // було б 350.
            // ⚠ Твердження ПЕРШЕ саме тому, що воно головне: воно друкує
            // виміряне число, а не «не той екземпляр» п'ятдесят разів.
            Assert.Equal(7, seen.Total);

            // Один знімок на всіх — не просто «однакові числа», а той самий
            // об'єкт: побудова була одна.
            Assert.All(snapshots, s => Assert.Same(snapshots[0], s));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Після_презентаційної_правки_повертається_новий_знімок()
    {
        var doc = await ArrangeAsync();

        // ⚠ Керований годинник — через `RD-05`: ревізія мемоїзується на 5 с, і
        // без переведення стрілок правка, зроблена в тій самій мілісекунді,
        // просто не встигла б стати видимою. Реальне очікування 5 с у тесті —
        // це 5 с на КОЖНОМУ прогоні CI, тобто те, чого `ManualClock` і
        // уникає (Q-252 завів його тут із тієї ж причини).
        var clock = new ManualClock();
        var memory = new MemoryCache(new MemoryCacheOptions { Clock = clock });

        TemplateVersionSnapshot before;
        await using (var db = CreateContext([]))
        {
            before = await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        await BumpRevisionAsync(doc.TemplateVersionId);
        clock.Advance(TimeSpan.FromSeconds(6));

        TemplateVersionSnapshot after;
        await using (var db = CreateContext([]))
        {
            after = await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        Assert.Equal(0, before.PresentationRevision);
        Assert.Equal(1, after.PresentationRevision);
        Assert.NotSame(before, after);
        Assert.NotEqual(before.CacheKey, after.CacheKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Старий_знімок_лишається_валідним_для_старого_ключа()
    {
        var doc = await ArrangeAsync();

        // Той самий керований годинник і з тієї ж причини, що вище: вікно
        // мемоїзації ревізії (`RD-05`) треба перевести, а не пересидіти.
        var clock = new ManualClock();
        var memory = new MemoryCache(new MemoryCacheOptions { Clock = clock });

        TemplateVersionSnapshot before;
        await using (var db = CreateContext([]))
        {
            before = await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        await BumpRevisionAsync(doc.TemplateVersionId);
        clock.Advance(TimeSpan.FromSeconds(6));

        await using (var db = CreateContext([]))
        {
            await new MetadataCache(memory, db).GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        // Новий ключ не витісняє старий: запит, який уже почався зі старою
        // ревізією, дочитує узгоджений знімок, а не половину нового.
        Assert.True(memory.TryGetValue(before.CacheKey, out TemplateVersionSnapshot? old));
        Assert.Same(before, old);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.6")]
    public async Task Знімок_містить_індекси_колонок_і_рядків_для_швидкого_доступу()
    {
        var doc = await ArrangeAsync();

        await using var db = CreateContext([]);
        var snapshot = await new MetadataCache(new MemoryCache(new MemoryCacheOptions()), db)
            .GetAsync(doc.TemplateVersionId, CancellationToken.None);

        var sheet = Assert.Single(snapshot.Sheets);
        var table = Assert.Single(sheet.Tables);

        // Індекси, а не лінійний обхід: резолвер посилань звертається до них
        // на кожну комірку формули, і O(n) там перетворився б на O(n²).
        Assert.Equal(doc.ColumnDefIds.Count, snapshot.ColumnsById.Count);
        Assert.Equal(doc.RowDefIds.Count, snapshot.RowsByKey.Count);
        Assert.Equal(doc.TableDefId, table.Id);
        Assert.All(doc.ColumnDefIds, id => Assert.True(snapshot.ColumnsById.ContainsKey(id)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Знімок_витісняється_з_памʼяті_після_спливу_TTL_Q252()
    {
        var doc = await ArrangeAsync();
        var clock = new ManualClock();
        var memory = new MemoryCache(new MemoryCacheOptions { Clock = clock });

        // Явна стеля, а не `CacheLifetimes.Default`: тест стереже факт
        // витіснення, а не число з конфігурації (дефолт 30→240 хв його вже ламав).
        var lifetimes = new CacheLifetimes(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));

        TemplateVersionSnapshot snapshot;
        await using (var db = CreateContext([]))
        {
            snapshot = await new MetadataCache(memory, db, lifetimes)
                .GetAsync(doc.TemplateVersionId, CancellationToken.None);
        }

        // Санітарна перевірка: запис справді ліг у кеш під власним ключем.
        Assert.True(memory.TryGetValue(snapshot.CacheKey, out _));

        // ⛔ Q-252: без AbsoluteExpirationRelativeToNow знімок ревізії, що
        // випала з вузького вікна InvalidateAsync (кілька презентаційних
        // правок без Publish/міграції), лишався б тут НАЗАВЖДИ — ні TTL, ні
        // SizeLimit його не витіснять.
        clock.Advance(lifetimes.Metadata + TimeSpan.FromMinutes(1));

        Assert.False(memory.TryGetValue(snapshot.CacheKey, out _));
    }

    /// <summary>
    /// Керований годинник для перевірки TTL без реального очікування:
    /// <see cref="MemoryCache"/> звіряє строк придатності запису з
    /// <see cref="MemoryCacheOptions.Clock"/> при кожному <c>TryGetValue</c>,
    /// тож переведення стрілок наперед і є симуляцією спливу часу (Q-252).
    /// </summary>
    private sealed class ManualClock : ISystemClock
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private async Task<TestDocument> ArrangeAsync()
        => await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);

    private EcrDbContext CreateContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);

    /// <summary>Контекст, чиї команди йдуть у спільний підрахунок (`MS-01`).</summary>
    /// <remarks>
    /// ⚠ <see cref="DbCommandCounter"/>, а не <c>LogTo</c>: підрахунок мусить
    /// бути СПІЛЬНИМ на п'ятдесят контекстів і потокобезпечним, а
    /// <c>List&lt;string&gt;.Add</c> із півсотні потоків дає мовчки занижене
    /// число — саме той вид хибнозеленого, від якого лічильник і рятує.
    /// </remarks>
    private EcrDbContext CreateCountingContext(DbCommandCounter counter)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options);

    private async Task BumpRevisionAsync(int versionId)
    {
        await using var db = CreateContext([]);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE cfg.TemplateVersion SET PresentationRevision = PresentationRevision + 1 WHERE Id = {0}",
            versionId);
    }
}
