using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17: History комірки таблиці «місяці в колонках» читає зміну там, куди її пише PATCH, — у періоді-ВЛАСНИКУ колонки
/// (введена не-місячна — якір року 202601; місячна M{m} — період m), а не лише в періоді перегляду.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Дефект 12.11 (doc 96, T3_00, LTD-A01, колонка FuelConsumptionUnder100LoadFromTheTe): правка з жовтня 4582 → 7
/// лягла в <c>aud.CellChange</c> з <c>PeriodKey = 202601</c> (<c>PatchCellsHandler.RouteForeignOwnersAsync</c>), а
/// <c>GetCellChangesHandler</c> питав 202610 — «No history … in this period».
/// </para>
/// <para>
/// Справжній шлях: запис — справжній <see cref="PatchCellsHandler"/> з роутером і <c>MonthRoutedCellStore</c>;
/// читання — <see cref="GetCellChangesHandler"/> над справжніми <see cref="AuditReader"/>, <see cref="MonthColumnRouter"/>,
/// <see cref="MetadataCache"/> і межами читання справжньої <see cref="AccessDecisionService"/>. Доступ доводиться
/// ДИФЕРЕНЦІЙНО: той самий запит, різниться лише Deny колонки в профілі; фільтри доступу не підмінюються.
/// </para>
/// <para>
/// ⚠ МУТАЦІЙНИЙ ДОКАЗ (локально, не в коміті): <c>RouteToOwnerPeriodAsync</c> повертає фільтр без змін — червоніють
/// <see cref="Правка_не_місячної_колонки_з_жовтня_видна_в_History_жовтня_і_січня"/> (жовтень порожній) і
/// <see cref="Правка_місячної_M11_з_жовтня_лишається_місячною"/> (M11 з жовтня порожня).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class CellHistoryMonthRoutedTests(SqlServerFixture sql) : IDisposable
{
    private const int Oct = 202610;
    private const int Jan = 202601;
    private const int Nov = 202611;
    private const int UserId = 1;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    /// <summary>Колонки: 0 — рядкова, 1 — звичайна (PLAIN), 2.. — M1..M12 за номером.</summary>
    private sealed record World(TestDocument Doc, string RowKey, IReadOnlyDictionary<int, string> Codes);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Правка_не_місячної_колонки_з_жовтня_видна_в_History_жовтня_і_січня()
    {
        var world = await ArrangeAsync();
        await PatchAsync(world, ("PLAIN", 4582m));
        await PatchAsync(world, ("PLAIN", 7m));

        var plain = world.Doc.ColumnDefIds[1];
        var fromOctober = await HistoryAsync(world, plain, Oct, Reader(world));
        var fromJanuary = await HistoryAsync(world, plain, Jan, Reader(world));

        // Журнал фізично в якорі — на тому ж RowKey, що й логічна адреса.
        Assert.Equal([Jan, Jan], fromOctober.Select(c => c.PeriodKey));
        Assert.Equal(["7", "4582"], fromOctober.Select(c => Trim(c.NewValue)));
        Assert.All(fromOctober, c => Assert.Equal(world.RowKey, c.RowKey));
        Assert.Equal(fromOctober.Select(c => c.Id), fromJanuary.Select(c => c.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Правка_місячної_M11_з_жовтня_лишається_місячною()
    {
        var world = await ArrangeAsync();
        await PatchAsync(world, ("M11", 5m), ("M10", 3m));

        var m11 = world.Doc.MonthColumnDefIds[10];
        var m10 = world.Doc.MonthColumnDefIds[9];

        // M11 — власник листопад: її історія з будь-якого місяця — це листопад, і лише вона.
        var m11FromOctober = await HistoryAsync(world, m11, Oct, Reader(world));
        var m11FromNovember = await HistoryAsync(world, m11, Nov, Reader(world));
        Assert.Equal([Nov], m11FromOctober.Select(c => c.PeriodKey));
        Assert.Equal(["5"], m11FromOctober.Select(c => Trim(c.NewValue)));
        Assert.Equal(m11FromOctober.Select(c => c.Id), m11FromNovember.Select(c => c.Id));

        // M10 — власник сам жовтень: його правка не з'являється в M11 і не переїжджає в якір.
        var m10FromOctober = await HistoryAsync(world, m10, Oct, Reader(world));
        Assert.Equal([Oct], m10FromOctober.Select(c => c.PeriodKey));
        Assert.Equal(["3"], m10FromOctober.Select(c => Trim(c.NewValue)));
        Assert.DoesNotContain(m11FromOctober, c => c.ColumnDefId == m10);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Deny_колонки_ховає_історію_з_власника_диференційно()
    {
        var world = await ArrangeAsync();
        await PatchAsync(world, ("PLAIN", 7m));
        var plain = world.Doc.ColumnDefIds[1];

        // Той самий запит (жовтень, та сама комірка), різниться лише Deny колонки.
        var allowed = await HistoryAsync(world, plain, Oct, Reader(world));
        var denied = await HistoryAsync(world, plain, Oct, Reader(world).Deny(ResourceKind.Column, plain));

        Assert.Single(allowed);
        Assert.Empty(denied);

        // І з якоря — так само: маршрут до власника не відкриває прихованого.
        Assert.Empty(await HistoryAsync(world, plain, Jan, Reader(world).Deny(ResourceKind.Column, plain)));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private static string? Trim(string? value)
        => value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            .ToString("G29", System.Globalization.CultureInfo.InvariantCulture);

    private static AccessBuilder Reader(World world)
        => new AccessBuilder { UserId = UserId }
            .Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write)
            .Permission(GetCellChangesHandler.CellHistoryPermission);

    private static int CodeIndex(string alias)
        => alias == "PLAIN" ? 1 : 1 + int.Parse(alias[1..], System.Globalization.CultureInfo.InvariantCulture);

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            Oct, columnCount: 2, rowCount: 1, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12,
            ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            for (var m = 1; m <= 12; m++)
            {
                var key = (2026 * 100) + m;
                var period = key == Oct
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == Oct)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m)))).Entity;
                period.AdvanceTo(PeriodState.Open, Now);
            }

            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            await db.SaveChangesAsync();
        }

        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Jan));
        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Nov));

        await using var read = builder.CreateContext();
        var columns = await read.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId).ToDictionaryAsync(c => c.Id);
        var rowKey = await read.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();

        var codes = new Dictionary<int, string>();
        for (var i = 0; i < doc.ColumnDefIds.Count; i++)
        {
            codes[i] = columns[doc.ColumnDefIds[i]].Code;
        }

        return new World(doc, rowKey, codes);
    }

    /// <summary>Правка з жовтня справжнім <see cref="PatchCellsHandler"/> з роутером (як клієнт: період перегляду).</summary>
    private async Task PatchAsync(World world, params (string Alias, decimal Value)[] changes)
    {
        await using var db = CreateContext();
        var clock = new TestClock(Now);
        var router = Router(db);
        var metadata = new MetadataCache(_memory, db);
        var real = RealAccess(db, metadata, router, clock);
        var profile = new AccessBuilder { UserId = UserId }
            .Grant(ResourceKind.Project, world.Doc.ProjectId, GrantLevel.Write)
            .Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanReadDocumentAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.ReadScopeAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));
        access.CanEditCellsAsync(
                Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanEditCellsAsync(
                c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<PeriodKey>(2),
                c.ArgAt<IReadOnlyCollection<CellAddress>>(3), c.ArgAt<CancellationToken>(4)));
        access.CanCreateRowsAsync(
                Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanCreateRowsAsync(
                c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<IReadOnlyCollection<string>>(2), c.ArgAt<CancellationToken>(3)));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var patch = new PatchCellsHandler(
            new MonthRoutedCellStore(new NormalizedCellStore(db), router, metadata, db),
            new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock, monthRouter: router),
            new DocumentStore(db), new PeriodStore(db), metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            new MethodologyStore(db), new RegistryStore(db), new DocumentHeaderStore(db), new AuditWriter(db), new AuditReader(db),
            Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db, clock), user, clock, new SheetEditGate(db),
            new UnitCatalog(db), monthRouter: router);

        var rows = await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock)
            .GetRowsBatchAsync([world.Doc.TableInstanceId], new PeriodKey(Oct), CancellationToken.None);
        var version = rows[world.Doc.TableInstanceId].Single(r => r.RowKey == world.RowKey).RowVersion;

        await patch.HandleAsync(
            new PatchCellsRequest(
                world.Doc.TableInstanceId, Oct, CellChangeOrigins.UserEdit,
                [new PatchRow(world.RowKey, version,
                    [.. changes.Select(c => new PatchCell(world.Codes[CodeIndex(c.Alias)], c.Value))])]),
            CancellationToken.None);
    }

    /// <summary>History однієї комірки, як її питає інспектор: документ + RowKey + колонка + період перегляду.</summary>
    private async Task<IReadOnlyList<CellChangeView>> HistoryAsync(World world, int columnDefId, int viewPeriod, AccessBuilder reader)
    {
        await using var db = CreateContext();
        var clock = new TestClock(Now);
        var router = Router(db);
        var metadata = new MetadataCache(_memory, db);
        var real = RealAccess(db, metadata, router, clock);
        var profile = reader.Build();

        // Межі читання — справжні; підмінено лише побудову профілю (з бази ролей тут немає).
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanReadDocumentAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));
        access.DocumentProjectIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.DocumentProjectIdAsync(c.ArgAt<long>(0), c.ArgAt<CancellationToken>(1)));
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.ReadScopeAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var handler = new GetCellChangesHandler(
            new AuditReader(db), access, user, clock: null, monthRouter: router, metadata: metadata);

        var page = await handler.HandleAsync(
            new CellChangeFilter(
                Now.AddDays(-30), Now.AddDays(1), world.Doc.DocumentId, world.RowKey, columnDefId, PeriodKey: viewPeriod),
            new CursorRequest(50),
            CancellationToken.None);

        return page.Items;
    }

    private static MonthColumnRouter Router(EcrDbContext db)
        => new(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
            .Build());

    private AccessDecisionService RealAccess(EcrDbContext db, MetadataCache metadata, MonthColumnRouter router, IClock clock)
        => new(db, metadata, new AccessProfileCache(_memory), clock, Substitute.For<ICurrentUser>(), new WorkflowStore(db), router);

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
