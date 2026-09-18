// tests/Ecr.Infrastructure.Tests/Persistence/TableSliceMethodologyQueryCountTests.cs
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>RD-04</c>: число звернень до БД на <c>GET</c> зрізу <b>не залежить</b> від
/// кількості прив'язаних методологій.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Замір, а не таймінг.</b> Лічильник — <c>Ecr.TestKit.DbCommandCounter</c>
/// (інструмент рядка <c>MS-01</c>). Тут доречний саме EF-перехоплювач: усі
/// звернення цього шляху випускає EF (<c>MethodologyStore</c>), сирих команд
/// сховища комірок на ньому немає взагалі — те «вужче джерело», яке
/// <c>DbCommandCounter</c> сам називає перевагою для <c>N+1</c> у зрізі.
/// </para>
/// <para>
/// ⚠ <b>Що саме рахується.</b> Усі порти, крім <see cref="IMethodologyStore"/>,
/// підмінені, тож у число входять рівно звернення методологій — та частина
/// 21.3 звернень із <c>MS-01-BASELINE.md</c> §3.1, за яку відповідає цей рядок
/// роботи. Повне число зрізу міряє лінійка <c>MS-01</c>, не цей тест.
/// </para>
/// <para>
/// ⚠ <b>Обробник береться з контейнера</b>, а не конструюється руками, і це
/// несуча деталь: кеш <c>RD-04</c> живе у спільному <see cref="IMemoryCache"/>,
/// який приходить із DI. Ручна конструкція довела б роботу кеша, який у
/// продукті може бути вимкнений — тобто була б хибнозеленою.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class TableSliceMethodologyQueryCountTests(SqlServerFixture sql)
{
    private const long TableInstance = 9_000_001;
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Теплий стан: одна методологія і три дають <b>однакове</b> число звернень.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація, що валить тест: прибрати кеш (повернути три запити на кожну
    /// методологію) — теплі числа стають 4 і 10 замість 1 і 1. Саме тому
    /// холодні числа теж перевіряються: вони показують, що <c>3N</c> реально
    /// виконується хоч раз, тобто рівність теплих не досягнута тим, що код
    /// узагалі нічого не робить.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Число_звернень_у_теплому_стані_не_залежить_від_кількості_методологій()
    {
        var one = await MeasureAsync(methodologyCount: 1);
        var three = await MeasureAsync(methodologyCount: 3);

        // Головне твердження рядка RD-04.
        Assert.Equal(one.Warm, three.Warm);

        // ⚠ Лишається рівно одне звернення — склад прив'язок
        // (`cfg.CalculationBinding`). Кешувати його не можна: він і є ключем
        // кешу, і саме тому зняття прив'язки видно негайно.
        Assert.Equal(1, one.Warm);

        // Холодний прогін: 1 (прив'язки) + 3 на кожну методологію.
        Assert.Equal(4, one.Cold);
        Assert.Equal(10, three.Cold);
    }

    /// <summary>
    /// Позначка не зникла разом із запитами: кеш віддає той самий результат,
    /// що й холодний прогін.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цієї перевірки попередній тест був би зеленим і в разі, якби кеш
    /// повертав порожній набір: нуль запитів — це й «оптимізовано», і
    /// «перестало працювати», а розрізняє їх лише результат.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кеш_віддає_ту_саму_позначку_що_й_холодний_прогін()
    {
        var measured = await MeasureAsync(methodologyCount: 3);

        Assert.True(measured.MarkedCold, "холодний прогін не позначив колонку — міряти нічого");
        Assert.True(measured.MarkedWarm, "теплий прогін втратив позначку — кеш віддає не те");
        Assert.Equal(measured.ColdColumnIds, measured.WarmColumnIds);
    }

    private async Task<Measurement> MeasureAsync(int methodologyCount)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var seeded = await builder.BuildAsync(columnCount: 2);
        var requiredColumnId = seeded.ColumnDefIds[1];

        await SeedMethodologiesAsync(builder, seeded, requiredColumnId, methodologyCount);

        var counter = new DbCommandCounter();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(counter)
            .Options);

        await using var provider = BuildProvider(db, seeded);

        counter.Tally.Reset();
        var cold = await SliceAsync(provider);
        var coldSeen = counter.Tally.Snapshot();

        counter.Tally.Reset();
        var warm = await SliceAsync(provider);
        var warmSeen = counter.Tally.Snapshot();

        return new Measurement(
            Cold: coldSeen.Total,
            Warm: warmSeen.Total,
            MarkedCold: Marked(cold, requiredColumnId),
            MarkedWarm: Marked(warm, requiredColumnId),
            ColdColumnIds: MarkedIds(cold),
            WarmColumnIds: MarkedIds(warm));
    }

    private static bool Marked(Application.Documents.Dto.TableSliceDto slice, int columnDefId)
        => slice.Columns.Single(c => c.Id == columnDefId).IsRequiredByMethodology;

    private static IReadOnlyList<int> MarkedIds(Application.Documents.Dto.TableSliceDto slice)
        => [.. slice.Columns.Where(c => c.IsRequiredByMethodology).Select(c => c.Id).Order()];

    /// <summary>Один «запит користувача» — власний scope, як у продукті.</summary>
    private static async Task<Application.Documents.Dto.TableSliceDto> SliceAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetTableSliceHandler>();

        // ⛔ Кеш мусить бути справді ввімкнений. Інакше «нуль зайвих звернень»
        // читалося б як успіх і тоді, коли сховище кешу не дійшло з контейнера.
        Assert.True(handler.RequiredColumnsCache.IsEnabled, "кеш RD-04 не отримав сховища з контейнера");

        return await handler.HandleAsync(1, TableInstance, Profile(), "en", CancellationToken.None);
    }

    private static ServiceProvider BuildProvider(EcrDbContext db, TestDocument seeded)
    {
        var services = new ServiceCollection();
        Ecr.Application.DependencyInjection.AddEcrApplication(services);
        services.AddMemoryCache();

        services.AddSingleton(db);
        services.AddScoped<IMethodologyStore>(sp => new MethodologyStore(sp.GetRequiredService<EcrDbContext>()));

        services.AddSingleton(Rows(seeded));
        services.AddSingleton(Cells());
        services.AddSingleton(Metadata(seeded));
        services.AddSingleton(Units());
        services.AddSingleton(Access());
        services.AddSingleton(Periods());
        services.AddSingleton(Styles());

        return services.BuildServiceProvider();
    }

    private static async Task SeedMethodologiesAsync(
        TestDocumentBuilder builder, TestDocument seeded, int requiredColumnId, int count)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        for (var i = 0; i < count; i++)
        {
            await using var db = builder.CreateContext();

            var methodology = new Methodology(
                EcrCode.Create($"RD04_{tag}_{i}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = $"RD-04 {tag} {i}" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            // ⚠ Версія зберігається ДО заведення правила й вимоги: обидва
            // несуть `MethodologyVersionId`, і взяті з незбереженої версії вони
            // вказували б на нуль.
            var version = new MethodologyVersion(
                methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();

            var rule = version.AddRule(EcrCode.Create("all"), "{}", 100);
            var requiredInput = version.AddRequiredInput(requiredColumnId, RequiredInputSeverity.Block, hint: null);

            // Чотири очі (D-40) — перевірка є і в схемі (`CK_MV_FourEyes`).
            version.Publish(publishedByUserId: 2, "RD-04", new DateOnly(2026, 1, 1), testsPassed: true, Now);

            db.MethodologyRules.Add(rule);
            db.MethodologyRequiredInputs.Add(requiredInput);
            db.CalculationBindings.Add(new CalculationBinding(
                seeded.TableDefId, seeded.ColumnDefIds[0], methodology.Id, "OUT", "{}"));

            await db.SaveChangesAsync();
        }
    }

    private static IRowStore Rows(TestDocument seeded)
    {
        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(
                TableInstance, DocumentId: 1, seeded.TableDefId, seeded.TemplateVersionId, seeded.PeriodKey.Value));
        rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long>());
        rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, bool>());

        return rows;
    }

    private static ICellStore Cells()
    {
        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns([]);

        return cells;
    }

    private static IMetadataCache Metadata(TestDocument seeded)
    {
        var sheet = new SheetDef(seeded.TemplateVersionId, EcrCode.Create("SH"), Text("Sheet"), 1);
        var table = new TableDef(
            seeded.SheetDefId, EcrCode.Create("TBL"), Text("Table"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, seeded.TableDefId);

        var columns = new Dictionary<int, ColumnDef>();
        for (var i = 0; i < seeded.ColumnDefIds.Count; i++)
        {
            var column = new ColumnDef(
                seeded.TableDefId, EcrCode.Create($"C{i}"), Text($"C{i}"), i + 1, CellDataType.Decimal);
            SetId(column, seeded.ColumnDefIds[i]);
            table.AddColumn(column);
            columns[column.Id] = column;
        }

        sheet.AddTable(table);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(seeded.TemplateVersionId, Arg.Any<CancellationToken>())
                .Returns(new TemplateVersionSnapshot(
                    seeded.TemplateVersionId, 0, [sheet], columns,
                    new Dictionary<(int, string), RowDef>()));

        return metadata;
    }

    private static IUnitCatalog Units()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return units;
    }

    private static IAccessDecisionService Access()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, EditDecision>());

        return access;
    }

    private static IPeriodStore Periods()
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        return periods;
    }

    private static IStyleCatalog Styles()
    {
        var styles = Substitute.For<IStyleCatalog>();
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());

        return styles;
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "rd04",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(StringComparer.Ordinal),
        RoleIds = new HashSet<int>(),
    };

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private sealed record Measurement(
        int Cold,
        int Warm,
        bool MarkedCold,
        bool MarkedWarm,
        IReadOnlyList<int> ColdColumnIds,
        IReadOnlyList<int> WarmColumnIds);
}
