using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Xunit;

namespace Ecr.Infrastructure.Tests.Caching;

/// <summary>
/// L2b (recalc-latency): граф залежностей формул версії (<c>ITemplateVersionStore.ListFormulaDependenciesAsync</c>) живе
/// в кеші метаданих — з ключем ревізії версії, скиданням <see cref="IMetadataCache.InvalidateAsync"/> і стелею життя
/// знімка. Кожну задачу перерахунку це економить ~100–200 мс на великому шаблоні.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази (запущені локально, не комітяться):
/// (1) у <c>MetadataCache.GetDerivedAsync</c> не додавати <c>AddExpirationToken</c> — червоніє «інвалідація»;
/// (2) у <c>GetDerivedAsync</c> брати <c>Lifetime</c> для чернетки — червоніє «чернетка не переживає вікна ревізії»;
/// (3) у <c>GetDerivedAsync</c> замінити <c>DerivedFlight.RunAsync(...)</c> прямим викликом — червоніє «один політ»;
/// (4) у <c>TemplateVersionStore.ListFormulaDependenciesAsync</c> читати напряму — червоніє «повторне читання».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage1)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class FormulaDependencyCacheTests(SqlServerFixture sql)
{
    private const string DependencyTable = "cfg.FormulaDependency";

    [Fact]
    public async Task Повторне_читання_графа_опублікованої_версії_не_звертається_до_БД_і_збігається_з_прямим()
    {
        var world = await ArrangeAsync(published: true);
        var memory = new MemoryCache(new MemoryCacheOptions());

        IReadOnlyList<FormulaDependency> first;
        await using (var db = CreateContext(new DbCommandCounter()))
        {
            first = await Store(db, memory).ListFormulaDependenciesAsync(world.VersionId, CancellationToken.None);
        }

        var counter = new DbCommandCounter();
        IReadOnlyList<FormulaDependency> second;
        await using (var db = CreateContext(counter))
        {
            second = await Store(db, memory).ListFormulaDependenciesAsync(world.VersionId, CancellationToken.None);
        }

        // Диференційна еквівалентність: кеш віддає РІВНО те, що пряме читання (той самий склад і порядок).
        IReadOnlyList<FormulaDependency> direct;
        await using (var db = CreateContext(new DbCommandCounter()))
        {
            direct = await new TemplateVersionStore(db).ListFormulaDependenciesAsync(world.VersionId, CancellationToken.None);
        }

        Assert.Equal(3, direct.Count);
        Assert.Equal(Describe(direct), Describe(first));
        Assert.Equal(Describe(direct), Describe(second));
        Assert.Equal(0, counter.Tally.Snapshot().Total);
    }

    [Fact]
    public async Task Інвалідація_скидає_граф_і_нові_залежності_видно_одразу()
    {
        var world = await ArrangeAsync(published: true);
        var memory = new MemoryCache(new MemoryCacheOptions());

        await using (var db = CreateContext(new DbCommandCounter()))
        {
            Assert.Equal(3, (await Store(db, memory).ListFormulaDependenciesAsync(world.VersionId, CancellationToken.None)).Count);
        }

        // Публікація/міграція замінила граф у БД (поза кешем цього вузла).
        await using (var db = CreateContext(new DbCommandCounter()))
        {
            await new TemplateVersionStore(db).ReplaceFormulaDependenciesAsync(
                world.VersionId, [Dependency(world.FormulaId, world.TableDefId, world.ColumnIds[0], 0)], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        // До інвалідації кеш чесно віддає знімок графа до заміни: опублікована версія структурно незмінна, а заміна
        // графа — це публікація, після коміту якої обробник ЗАВЖДИ викликає InvalidateAsync.
        await using (var db = CreateContext(new DbCommandCounter()))
        {
            Assert.Equal(3, (await Store(db, memory).ListFormulaDependenciesAsync(world.VersionId, CancellationToken.None)).Count);
        }

        await using (var db = CreateContext(new DbCommandCounter()))
        {
            await new MetadataCache(memory, db).InvalidateAsync(world.VersionId, CancellationToken.None);
        }

        await using (var db = CreateContext(new DbCommandCounter()))
        {
            var after = await Store(db, memory).ListFormulaDependenciesAsync(world.VersionId, CancellationToken.None);
            Assert.Single(after);
        }
    }

    [Fact]
    public async Task Граф_чернетки_не_переживає_вікно_ревізії_на_іншому_вузлі()
    {
        var world = await ArrangeAsync(published: false);
        var clock = new ManualClock();
        var memory = new MemoryCache(new MemoryCacheOptions { Clock = clock });

        await using (var db = CreateContext(new DbCommandCounter()))
        {
            Assert.Equal(3, (await Store(db, memory).ListFormulaDependenciesAsync(world.VersionId, CancellationToken.None)).Count);
        }

        // Інший вузол замінив граф; сюди інвалідація не доходить.
        await using (var db = CreateContext(new DbCommandCounter()))
        {
            await new TemplateVersionStore(db).ReplaceFormulaDependenciesAsync(
                world.VersionId, [Dependency(world.FormulaId, world.TableDefId, world.ColumnIds[0], 0)], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromSeconds(6));

        await using (var db = CreateContext(new DbCommandCounter()))
        {
            Assert.Single(await Store(db, memory).ListFormulaDependenciesAsync(world.VersionId, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Різні_версії_не_ділять_запис()
    {
        var a = await ArrangeAsync(published: true);
        var b = await ArrangeAsync(published: true);
        await using (var db = CreateContext(new DbCommandCounter()))
        {
            await new TemplateVersionStore(db).ReplaceFormulaDependenciesAsync(
                b.VersionId, [Dependency(b.FormulaId, b.TableDefId, b.ColumnIds[0], 0)], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var memory = new MemoryCache(new MemoryCacheOptions());
        await using var db1 = CreateContext(new DbCommandCounter());
        await using var db2 = CreateContext(new DbCommandCounter());

        Assert.Equal(3, (await Store(db1, memory).ListFormulaDependenciesAsync(a.VersionId, CancellationToken.None)).Count);
        Assert.Single(await Store(db2, memory).ListFormulaDependenciesAsync(b.VersionId, CancellationToken.None));
    }

    [Fact]
    public async Task П_ятдесят_одночасних_читань_холодного_ключа_читають_граф_один_раз()
    {
        const int Parallel = 50;

        var world = await ArrangeAsync(published: true);
        var memory = new MemoryCache(new MemoryCacheOptions());
        var counter = new DbCommandCounter();

        // Прогрів ревізії (мемо) — решта викликів доходить до польоту без await.
        await using (var warm = CreateContext(new DbCommandCounter()))
        {
            await new MetadataCache(memory, warm).GetAsync(world.VersionId, CancellationToken.None);
        }

        var contexts = Enumerable.Range(0, Parallel).Select(_ => CreateContext(counter)).ToList();
        try
        {
            counter.Tally.Reset();
            var tasks = contexts
                .Select(db => Store(db, memory).ListFormulaDependenciesAsync(world.VersionId, CancellationToken.None))
                .ToList();
            var results = await Task.WhenAll(tasks);

            Assert.All(results, r => Assert.Equal(3, r.Count));
            Assert.Equal(1, counter.Tally.Snapshot()["SELECT " + DependencyTable]);
        }
        finally
        {
            foreach (var db in contexts)
            {
                await db.DisposeAsync();
            }
        }
    }

    // ── світ ─────────────────────────────────────────────────────────────────

    private static IEnumerable<string> Describe(IReadOnlyList<FormulaDependency> list)
        => list.Select(d => $"{d.Id}|{d.FormulaDefId}|{d.SortOrder}|{d.DependsOnKind}|{d.TableDefId}|{d.RowKey}|{d.ColumnDefId}|{d.PeriodOffset}");

    private static TemplateVersionStore Store(EcrDbContext db, IMemoryCache memory)
        => new(db, new MetadataCache(memory, db));

    private static FormulaDependency Dependency(int formulaId, int tableDefId, int columnId, int sort)
        => FormulaDependency.ForFormula(formulaId, 0, tableDefId, rowKey: null, columnId, null, null, sort);

    private sealed record World(int VersionId, int TableDefId, int FormulaId, IReadOnlyList<int> ColumnIds);

    /// <summary>Версія з однією формулою й графом із трьох залежностей; <paramref name="published"/> — заморожена.</summary>
    private async Task<World> ArrangeAsync(bool published)
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(columnCount: 3, ct: CancellationToken.None);

        await using var db = CreateContext(new DbCommandCounter());
        var formula = new FormulaDef(doc.TableDefId, FormulaScope.Column, "[C2]", ExpressionDialect.Template);
        formula.AssignColumn(doc.ColumnDefIds[2]);
        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync();

        await new TemplateVersionStore(db).ReplaceFormulaDependenciesAsync(
            doc.TemplateVersionId,
            [
                Dependency(formula.Id, doc.TableDefId, doc.ColumnDefIds[0], 0),
                Dependency(formula.Id, doc.TableDefId, doc.ColumnDefIds[1], 1),
                Dependency(formula.Id, doc.TableDefId, doc.ColumnDefIds[2], 2),
            ],
            CancellationToken.None);
        await db.SaveChangesAsync();

        if (published)
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE cfg.TemplateVersion SET Status = 1, PublishedAt = SYSUTCDATETIME(), PublishedByUserId = 1 WHERE Id = {0}",
                doc.TemplateVersionId);
        }

        return new World(doc.TemplateVersionId, doc.TableDefId, formula.Id, doc.ColumnDefIds);
    }

    private EcrDbContext CreateContext(DbCommandCounter counter)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(counter)
            .Options);

    private sealed class ManualClock : ISystemClock
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public DateTimeOffset UtcNow => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
