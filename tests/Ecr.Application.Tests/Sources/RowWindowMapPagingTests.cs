// tests/Ecr.Application.Tests/Sources/RowWindowMapPagingTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Аудит Z6-02: зміна прив'язки вікна рядків доходить до ВСІХ відкритих екземплярів таблиці, а не до першої тисячі.
/// </summary>
/// <remarks>
/// До виправлення і постановка підтягування (I1-02), і зняття чинності (X3-04) брали одну вибірку
/// <c>OpenInstancesAsync(…, 1000)</c> за Id без курсора: екземпляр 1001+ лишався з <c>Fetched</c>-числом за старою
/// конфігурацією, і жоден інший шлях (тригер, щогодинний повтор) його не підбирав.
/// Мутація: у <c>RowWindowMapSupport.ForEachOpenInstancePageAsync</c> вийти після першої сторінки — червоніють усі
/// тести нижче (1000 замість 2001/2000).
/// </remarks>
public sealed class RowWindowMapPagingTests
{
    private const int Table = 10;
    private const int MapId = 77;
    private const int EntityId = 5;

    /// <summary>Сховище з <paramref name="count"/> відкритими екземплярами; сторінки — за keyset-ом, як у справжньому.</summary>
    private static IRowWindowMapStore StoreWith(int count)
    {
        var all = Enumerable.Range(1, count)
            .Select(i => new RowWindowFetchRequest(i * 10L, 202601))
            .ToList();

        var store = Substitute.For<IRowWindowMapStore>();
        store.OpenInstancesAsync(Table, Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var after = call.ArgAt<long?>(1) ?? 0;
                var limit = call.ArgAt<int>(2);
                IReadOnlyList<RowWindowFetchRequest> page = [.. all.Where(r => r.TableInstanceId > after).Take(limit)];
                return Task.FromResult(page);
            });
        return store;
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "Z6-02")]
    [InlineData(2001)]
    [InlineData(2000)]
    [InlineData(1)]
    public async Task Підтягування_ставиться_кожному_відкритому_екземпляру_понад_першу_тисячу(int count)
    {
        var store = StoreWith(count);
        var jobs = Substitute.For<IBackgroundJobScheduler>();

        await RowWindowMapSupport.EnqueueFetchAsync(store, jobs, Map(), CancellationToken.None);

        var targets = jobs.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync))
            .Select(c => ((RowWindowFetchRequest)c.GetArguments()[1]!).TableInstanceId)
            .ToList();

        Assert.Equal(count, targets.Count);
        Assert.Equal(count, targets.Distinct().Count());
        Assert.Contains(count * 10L, targets);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "Z6-02")]
    public async Task Зняття_чинності_за_зміною_згортки_охоплює_всі_відкриті_екземпляри()
    {
        var store = StoreWith(2001);
        var touched = new List<long>();
        store.SupersedeFoldedValuesAsync(
                MapId, Arg.Any<IReadOnlyList<RowWindowFetchRequest>>(), EntityId, "Flare.Total", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var page = call.ArgAt<IReadOnlyList<RowWindowFetchRequest>>(1);
                touched.AddRange(page.Select(r => r.TableInstanceId));
                return Task.FromResult(page.Count);
            });

        await RowWindowMapSupport.SupersedeRefoldedAsync(
            store, Map(), [(EntityId, "Flare.Total")], CancellationToken.None);

        Assert.Equal(2001, touched.Count);
        Assert.Equal(2001, touched.Distinct().Count());
        Assert.Contains(20010L, touched);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "Z6-02")]
    public async Task Прив_язка_на_паузі_й_правка_без_зміни_згортки_екземплярів_не_читають()
    {
        var store = StoreWith(3);
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var paused = Map();
        paused.SetActive(false);

        await RowWindowMapSupport.EnqueueFetchAsync(store, jobs, paused, CancellationToken.None);
        await RowWindowMapSupport.SupersedeRefoldedAsync(store, Map(), [], CancellationToken.None);

        Assert.Empty(store.ReceivedCalls());
        Assert.Empty(jobs.ReceivedCalls());
    }

    private static RowWindowMap Map()
    {
        var map = RowWindowMap.Create(
            Column("VOLUME", CellDataType.Decimal, id: 3),
            Column("START_AT", CellDataType.Date, id: 1),
            Column("END_AT", CellDataType.Date, id: 2),
            selector: null,
            RowWindowSummaryKind.Total,
            isStep: false,
            targetUnitId: 501);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty(nameof(Ecr.Domain.Abstractions.Entity<int>.Id))!.SetValue(map, MapId);
        return map;
    }

    private static ColumnDef Column(string code, CellDataType type, int id)
    {
        var column = new ColumnDef(
            Table, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), id, type);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty(nameof(Ecr.Domain.Abstractions.Entity<int>.Id))!.SetValue(column, id);
        return column;
    }
}
