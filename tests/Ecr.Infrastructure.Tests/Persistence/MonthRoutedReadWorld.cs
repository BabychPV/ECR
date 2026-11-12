using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
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

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17 (LAND-YEAR L6, G1/G2/G4): спільна підготовка читачів таблиці «місяці в колонках» — документ з календарем
/// 2026-01…12 (усі Open), екземпляри жовтня (перегляд), січня (якір року) і листопада; запис — справжнім
/// <see cref="PatchCellsHandler"/> з роутером і <c>MonthRoutedCellStore</c> (як <c>CellHistoryMonthRoutedTests</c>).
/// </summary>
/// <remarks>
/// Колонки: 0 — рядкова, 1 — звичайна (<c>PLAIN</c>, власник — якір 202601), 2.. — M1..M12 (власник M{m} — період m).
/// <see cref="Routing"/> = <c>false</c> — прапор <c>Collection:MonthColumnRouting</c> вимкнено (контроль: запис і читання
/// фізичні, як до Q-17). Межі читання — справжня <see cref="AccessDecisionService"/>; підмінено лише побудову профілю.
/// </remarks>
internal sealed class MonthRoutedReadWorld : IDisposable
{
    public const int Oct = 202610;
    public const int Jan = 202601;
    public const int Nov = 202611;

    public static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _connectionString;
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    private MonthRoutedReadWorld(
        string connectionString, bool routing, TestDocument doc, string rowKey, IReadOnlyDictionary<int, string> codes,
        IReadOnlyDictionary<int, long> instances)
    {
        _connectionString = connectionString;
        Routing = routing;
        Doc = doc;
        RowKey = rowKey;
        Codes = codes;
        Instances = instances;
    }

    /// <summary>Прапор маршрутизації.</summary>
    public bool Routing { get; }

    /// <summary>Документ (екземпляр <see cref="TestDocument.TableInstanceId"/> — жовтень).</summary>
    public TestDocument Doc { get; }

    /// <summary>Ключ єдиного рядка.</summary>
    public string RowKey { get; }

    /// <summary>Коди колонок за індексом.</summary>
    public IReadOnlyDictionary<int, string> Codes { get; }

    /// <summary>Екземпляри таблиці за періодом (жовтень, січень, листопад).</summary>
    public IReadOnlyDictionary<int, long> Instances { get; }

    /// <summary>Звичайна (не-місячна) колонка.</summary>
    public int Plain => Doc.ColumnDefIds[1];

    /// <summary>Місячна колонка M{m}.</summary>
    public int Month(int m) => Doc.MonthColumnDefIds[m - 1];

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    /// <summary>Будує документ.</summary>
    public static async Task<MonthRoutedReadWorld> ArrangeAsync(string connectionString, bool routing = true)
    {
        var builder = new TestDocumentBuilder(connectionString);
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

        var jan = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Jan));
        var nov = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Nov));

        await using var read = builder.CreateContext();
        var columns = await read.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId).ToDictionaryAsync(c => c.Id);
        var rowKey = await read.TableRows.AsNoTracking().Where(r => r.Id == doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();

        var codes = new Dictionary<int, string>();
        for (var i = 0; i < doc.ColumnDefIds.Count; i++)
        {
            codes[i] = columns[doc.ColumnDefIds[i]].Code;
        }

        return new MonthRoutedReadWorld(
            connectionString, routing, doc, rowKey, codes,
            new Dictionary<int, long> { [Oct] = doc.TableInstanceId, [Jan] = jan.TableInstanceId, [Nov] = nov.TableInstanceId });
    }

    /// <summary>Читач/автор з грантом на проєкт і переданими правами.</summary>
    public AccessBuilder Profile(int userId, params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = userId }.Grant(ResourceKind.Project, Doc.ProjectId, GrantLevel.Write);
        foreach (var permission in permissions)
        {
            builder.Permission(permission);
        }

        return builder;
    }

    /// <summary>
    /// Правка справжнім <see cref="PatchCellsHandler"/> з екземпляра періоду <paramref name="viewPeriod"/> (як клієнт),
    /// від імені <paramref name="userId"/>. Аліаси: <c>PLAIN</c>, <c>M1</c>…<c>M12</c>.
    /// </summary>
    public async Task PatchAsync(int userId, int viewPeriod, params (string Alias, decimal Value)[] changes)
    {
        await using var db = CreateContext();
        var clock = new TestClock(Now);
        var router = Router(db);
        var metadata = Metadata(db);
        var access = Access(db, Profile(userId).Build(), metadata, router);
        var user = User(userId);

        var patch = new PatchCellsHandler(
            new MonthRoutedCellStore(new NormalizedCellStore(db), router, metadata, db),
            new RowStore(db, new BulkCellLoader(_connectionString, 1000), clock, monthRouter: router),
            new DocumentStore(db), new PeriodStore(db), metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            new MethodologyStore(db), new RegistryStore(db), new DocumentHeaderStore(db), new AuditWriter(db), new AuditReader(db),
            Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db, clock), user, clock, new SheetEditGate(db),
            new UnitCatalog(db), monthRouter: router);

        var instance = Instances[viewPeriod];
        var rows = await new RowStore(db, new BulkCellLoader(_connectionString, 1000), clock)
            .GetRowsBatchAsync([instance], new PeriodKey(viewPeriod), CancellationToken.None);
        var version = rows[instance].Single(r => r.RowKey == RowKey).RowVersion;

        await patch.HandleAsync(
            new PatchCellsRequest(
                instance, viewPeriod, CellChangeOrigins.UserEdit,
                [new PatchRow(RowKey, version, [.. changes.Select(c => new PatchCell(Codes[CodeIndex(c.Alias)], c.Value))])]),
            CancellationToken.None);
    }

    /// <summary>Роутер власності з прапором <see cref="Routing"/>.</summary>
    public MonthColumnRouter Router(EcrDbContext db)
        => new(db, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Collection:MonthColumnRouting"] = Routing ? "true" : "false",
            })
            .Build());

    /// <summary>Кеш метаданих (спільна пам'ять світу).</summary>
    public MetadataCache Metadata(EcrDbContext db) => new(_memory, db);

    /// <summary>
    /// Служба доступу: профіль — <paramref name="profile"/>, усе інше (документ, проєкт, межі читання, правка) —
    /// справжня <see cref="AccessDecisionService"/>. Фільтри доступу не підмінюються.
    /// </summary>
    public IAccessDecisionService Access(EcrDbContext db, AccessProfile profile, MetadataCache metadata, MonthColumnRouter router)
    {
        var real = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), new TestClock(Now), Substitute.For<ICurrentUser>(),
            new WorkflowStore(db), router);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.CanReadDocumentAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<CancellationToken>(2)));
        access.DocumentProjectIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(c => real.DocumentProjectIdAsync(c.ArgAt<long>(0), c.ArgAt<CancellationToken>(1)));
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
        return access;
    }

    /// <summary>Поточний користувач.</summary>
    public static ICurrentUser User(int userId)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);
        return user;
    }

    /// <summary>Контекст на базу тестів.</summary>
    public EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(_connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    /// <summary>Значення журналу без хвостових нулів (<c>7.0000</c> → <c>7</c>).</summary>
    public static string? Trim(string? value)
        => value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
            .ToString("G29", System.Globalization.CultureInfo.InvariantCulture);

    private static int CodeIndex(string alias)
        => alias == "PLAIN" ? 1 : 1 + int.Parse(alias[1..], System.Globalization.CultureInfo.InvariantCulture);
}
