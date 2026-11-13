using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-155b: перемикання актуального прогону не скидає карту застарілості, а лікує її по документах зачеплених прогонів;
/// на старті й при першому зверненні N читачів - РІВНО одне повне завантаження на (період, ключ).
/// </summary>
/// <remarks>
/// Мутаційні докази (detached-worktree): у <c>StaleCountsEpoch.InvalidateRunSwitch</c> замінити <c>BumpRunSwitch</c> на
/// <c>Bump</c> (перемикання піднімає жорстку епоху) - червоні <c>Перемикання_прогону_лікує_лише_зачеплені_документи_...</c> і
/// <c>Перемикання_прогону_читає_менше_за_повне_завантаження</c>; у <c>SingleFlightAsync</c> замінити
/// <c>!ReferenceEquals(existing, mine)</c> на <c>false</c> (прибрати single-flight) - червоний
/// <c>Прогрів_і_N_паралельних_перших_читачів_роблять_одне_повне_завантаження</c>.
/// </remarks>
public sealed partial class DocumentStaleResultsStoreTests
{
    private sealed record SwitchRig(
        TestDocumentBuilder Builder, TestDocument Chain, int Period, int Version, long[] Docs, MemoryCache Cache,
        MovableClock Clock, StaleCountsEpoch Epoch, SwrProbe Probe);

    /// <summary>Період із <paramref name="documents"/> документами в одному прогоні проєкту; кожен парний відредаговано після нього.</summary>
    private async Task<SwitchRig> SwitchRigAsync(int documents, int periodKey = 202601)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(periodKey, ct: CancellationToken.None);
        var period = chain.PeriodKey!.Value;
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var docs = new long[documents];
        for (var i = 0; i < documents; i++)
        {
            docs[i] = await AddDocumentAsync(db, chain, $"RS{tag}-{i:000}");
        }

        var version = await VersionAsync(db);
        await RunAsync(db, chain, version, Now, docs);
        for (var i = 0; i < documents; i += 2)
        {
            await EditAsync(docs[i], period, chain.ColumnDefIds[1], Now.AddHours(1), user: i % 4 == 0 ? Me : 62);
        }

        return new SwitchRig(
            builder, chain, period, version, docs, NewCache(), new MovableClock(Now.AddHours(2)), new StaleCountsEpoch(),
            new SwrProbe());
    }

    /// <summary>Прогін ДОКУМЕНТА: створюється й одразу стає актуальним (транзакція, як у <c>RunCalculationHandler</c>).</summary>
    private async Task SwitchDocumentRunAsync(SwitchRig r, long documentId, DateTime startedAt, StaleCountsEpoch? writerEpoch = null)
    {
        var epoch = writerEpoch ?? r.Epoch;
        await using var db = EpochContext(epoch);
        var run = new CalculationRun(r.Chain.ProjectId, r.Period, triggeredByUserId: null, startedAt, documentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync(CancellationToken.None);
        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync(CancellationToken.None);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {r.Version},
                    {r.Period}, {documentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
            """);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await new CalculationResultStore(db, new TestClock(Now), epoch)
            .SwitchCurrentRunAsync(run.Id, "{}", CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync();
    }

    private StaleResultsMapCache CachedMap(SwitchRig r, EcrDbContext db) => new(db, r.Cache, r.Clock, r.Epoch);

    private static readonly int?[] MapUsers = [null, Me, 62, 424242];

    /// <summary>Карти періоду через кеш (всі ключі автора й «*») - порівнюються з прямим завантаженням без кешу.</summary>
    private async Task AssertCacheEqualsFreshAsync(SwitchRig r)
    {
        await using var cachedDb = InterceptedContext(r.Probe, r.Epoch);
        var cached = await CachedMap(r, cachedDb).GetAsync(r.Period, MapUsers, CancellationToken.None);

        await using var freshDb = r.Builder.CreateContext();
        var fresh = await new StaleResultsMapCache(freshDb, null, null, null).GetAsync(r.Period, MapUsers, CancellationToken.None);

        Assert.Equal(fresh.Count, cached.Count);
        for (var i = 0; i < fresh.Count; i++)
        {
            Assert.NotNull(cached[i]);
            Assert.Equal(
                fresh[i]!.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)),
                cached[i]!.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перемикання_прогону_лікує_лише_зачеплені_документи_кеш_дорівнює_свіжому_завантаженню()
    {
        var r = await SwitchRigAsync(12);
        using var _ = r.Cache;

        await AssertCacheEqualsFreshAsync(r);
        Assert.Equal(1, r.Probe.Maps);
        Assert.Equal(1, r.Probe.AuthorMaps);

        // 1. Прогін документа 0 стартував ПІСЛЯ правки: документ уже не застарілий.
        await SwitchDocumentRunAsync(r, r.Docs[0], Now.AddHours(2));
        await AssertCacheEqualsFreshAsync(r);

        // 2. Новіший прогін того ж документа, що стартував ДО правки: документ знову застарілий (Y1 знято, Y2 додано).
        await SwitchDocumentRunAsync(r, r.Docs[0], Now.AddMinutes(30));
        await AssertCacheEqualsFreshAsync(r);

        // 3. Правка документа 1 (PATCH: позначка накладки) і його прогін, що стартував раніше правки.
        await EditAsync(r.Docs[1], r.Period, r.Chain.ColumnDefIds[1], Now.AddHours(3), user: 62);
        r.Epoch.MarkDirty(r.Period, [r.Docs[1]]);
        await SwitchDocumentRunAsync(r, r.Docs[1], Now.AddHours(2));
        await AssertCacheEqualsFreshAsync(r);

        // 4. Прогін іншого ПРОЦЕСУ (Worker: власна епоха, кеш читача про нього не знає) для документа 2.
        var before = r.Epoch.HardValueFor(r.Period);
        await SwitchDocumentRunAsync(r, r.Docs[2], Now.AddHours(5), new StaleCountsEpoch());
        Assert.Equal(before, r.Epoch.HardValueFor(r.Period));
        await AssertCacheEqualsFreshAsync(r);

        // 5. Два прогони різних документів між двома читаннями.
        await SwitchDocumentRunAsync(r, r.Docs[4], Now.AddHours(6));
        await SwitchDocumentRunAsync(r, r.Docs[6], Now.AddMinutes(10));
        await AssertCacheEqualsFreshAsync(r);

        // Жодного повного завантаження після першого: лише точкові запити накладки.
        Assert.Equal(1, r.Probe.Maps);
        Assert.Equal(1, r.Probe.AuthorMaps);
        Assert.True(r.Probe.Deltas >= 5, $"точкових запитів {r.Probe.Deltas}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перемикання_прогону_усього_проєкту_запасно_дає_повне_завантаження_з_тим_самим_результатом()
    {
        var r = await SwitchRigAsync(6);
        using var _ = r.Cache;
        await AssertCacheEqualsFreshAsync(r);
        Assert.Equal(1, r.Probe.Maps);

        // Прогін усього проєкту (DocumentId = null): область невідома - точкове оновлення неможливе.
        await using (var db = EpochContext(r.Epoch))
        {
            var run = await PendingRunAsync(db, r.Chain, r.Version, Now.AddHours(1).AddMinutes(30), [r.Docs[0], r.Docs[2]]);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await new CalculationResultStore(db, new TestClock(Now), r.Epoch)
                .SwitchCurrentRunAsync(run.Id, "{}", CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync();
        }

        await AssertCacheEqualsFreshAsync(r);
        Assert.Equal(2, r.Probe.Maps);
        Assert.Equal(2, r.Probe.AuthorMaps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_довідника_разом_із_перемиканням_прогону_лишається_повним_завантаженням()
    {
        var r = await SwitchRigAsync(6);
        using var _ = r.Cache;
        await AssertCacheEqualsFreshAsync(r);

        // Жорстка епоха рухається від будь-чого, крім перемикання прогону.
        var hard = r.Epoch.HardValueFor(r.Period);
        r.Epoch.Bump(r.Period);
        Assert.True(r.Epoch.HardValueFor(r.Period) > hard);

        await SwitchDocumentRunAsync(r, r.Docs[0], Now.AddHours(2));
        await AssertCacheEqualsFreshAsync(r);
        Assert.Equal(2, r.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогрів_і_N_паралельних_перших_читачів_роблять_одне_повне_завантаження()
    {
        const int readers = 8;
        var r = await SwitchRigAsync(6);
        using var _ = r.Cache;
        r.Probe.Hold();

        // Прогрів (як StaleMapWarmer: власний контекст, спільний кеш) і читачі стартують одночасно.
        var warm = Task.Run(async () =>
        {
            await using var db = InterceptedContext(r.Probe, r.Epoch);
            return await new StaleResultsMapCache(db, r.Cache, r.Clock, r.Epoch).WarmAsync(r.Period, CancellationToken.None);
        });
        var pages = Enumerable.Range(0, readers).Select(_ => Task.Run(async () =>
        {
            await using var db = InterceptedContext(r.Probe, r.Epoch);
            return (await new StaleResultsMapCache(db, r.Cache, r.Clock, r.Epoch)
                .GetAsync(r.Period, [null], CancellationToken.None))[0]!.Count;
        })).ToList();

        // Лідер утримує запит; слідувачі до нього приєднались, а не пішли у власний.
        await r.Probe.WaitMapsAsync(1);
        await Task.Delay(300);
        r.Probe.Release();

        var counts = await Task.WhenAll(pages).WaitAsync(TimeSpan.FromSeconds(60));
        var warmed = await warm.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.All(counts, c => Assert.Equal(warmed, c));
        Assert.Equal(1, r.Probe.Maps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перемикання_прогону_читає_менше_за_повне_завантаження()
    {
        // Власний період: карта, відбиток і набір прогонів періоду - лише дані цього тесту, а не спільної бази.
        var r = await SwitchRigAsync(150, OwnPeriod);
        using var _ = r.Cache;
        await AssertCacheEqualsFreshAsync(r);

        // B92: не сирі logical reads сесії - вони залежать від плану, статистики й чужих даних спільної бази
        // (виміряно: повне завантаження 622 читань на чистій базі, 20-37 після чужого проєкту того ж періоду,
        // 1736 у CI). Міряється те, від чого залежить ціна: які команди точкового шляху йдуть по ВАЖКИХ таблицях
        // (результати розрахунку, журнал правок) і скільки документів вони охоплюють.
        var recorder = new HeavyCommandRecorder();
        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(r.Probe, recorder, r.Epoch);
        await using var db = new EcrDbContext(options.Options);
        var map = CachedMap(r, db);

        await SwitchDocumentRunAsync(r, r.Docs[0], Now.AddHours(2));
        recorder.Clear();
        var scoped = (await map.GetAsync(r.Period, [null], CancellationToken.None))[0]!;
        var scopedHeavy = recorder.Snapshot();
        Assert.False(scoped.ContainsKey(r.Docs[0]));

        await using var fullDb = r.Builder.CreateContext();
        var full = (await new StaleResultsMapCache(fullDb, null, null, null).GetAsync(r.Period, [null], CancellationToken.None))[0];
        Assert.NotNull(full);

        // Точковий шлях: жодного повного запиту карти, рівно ОДИН запит по важких таблицях - накладка, прив'язана
        // лише до документа перемкнутого прогону (1 зі 150). Повне завантаження перечитує весь період.
        Assert.Equal(1, r.Probe.Maps);
        var delta = Assert.Single(scopedHeavy);
        Assert.Contains(StaleResultsMapCache.DeltaTag, delta.Text, StringComparison.Ordinal);
        Assert.Equal([r.Docs[0]], delta.Numbers);

        // Результат точкового шляху - той самий, що й повного: 74 застарілі документи власного періоду (кожен парний
        // зі 150 відредаговано після прогону, документ 0 щойно перераховано), а перечитано один.
        Assert.Equal(full.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)), scoped.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)));
        Assert.Equal(r.Docs.Where((_, i) => i % 2 == 0).Skip(1).Order(), full.Keys.Order());
    }

    /// <summary>Період, який не бере жоден інший тест (B92: лише власні дані).</summary>
    private const int OwnPeriod = 209912;

    /// <summary>Записує команди, що читають важкі таблиці карти (<c>calc.CalculationResult</c>, <c>aud.CellChange</c>), і числа в їхніх параметрах.</summary>
    private sealed class HeavyCommandRecorder : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, long[] Numbers)> commands = new();

        public void Clear() => commands.Clear();

        public IReadOnlyList<(string Text, long[] Numbers)> Snapshot() => [.. commands];

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("calc.CalculationResult", StringComparison.OrdinalIgnoreCase)
                || command.CommandText.Contains("aud.CellChange", StringComparison.OrdinalIgnoreCase))
            {
                // Числа з параметрів колекції документів (`ids.Contains(...)` у StaleResultsMapCache): і поштучних
                // (@ids1, @ids2...), і одного JSON-параметра. Інші параметри (період, TOP) - не документи.
                var numbers = command.Parameters.Cast<System.Data.Common.DbParameter>()
                    .Where(p => p.ParameterName.Contains("ids", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(p => System.Text.RegularExpressions.Regex.Matches(
                        Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, @"\d{1,18}"))
                    .Select(m => long.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))
                    .Distinct()
                    .ToArray();
                commands.Enqueue((command.CommandText, numbers));
            }

            return ValueTask.FromResult(result);
        }
    }
}
