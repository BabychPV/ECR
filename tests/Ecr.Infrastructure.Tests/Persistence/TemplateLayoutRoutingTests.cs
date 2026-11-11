using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-474: маршрутизація місячних колонок вмикається ВЕРСІЄЮ шаблону (<see cref="TemplateLayout.YearWithMonthColumns"/>),
/// а не лише прапором <c>Collection:MonthColumnRouting</c>. Реальний SQL Server, справжній
/// <see cref="TemplateLayoutRegistry"/>.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати <c>|| _registry?.IsYearSheet(...)</c> з <c>MonthColumnRouter.AppliesToTable</c> або
/// <c>RoutesVersion</c> — перший тест червоніє (читає копію перегляду 999); прибрати перевірку версії взагалі
/// (маршрутизувати все, щойно реєстр непорожній) — червоніє другий.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateLayoutRoutingTests(SqlServerFixture sql) : IDisposable
{
    private const int ViewMonth = 10;

    private const int Year = 2026;

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    private readonly TestDocumentBuilder _builder = new(sql.ConnectionString);

    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task Прапор_вимкнено_версія_з_річною_розкладкою_маршрутизується()
    {
        var doc = await ArrangeAsync();
        var november = await _builder.AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + 11));
        var january = await _builder.AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + 1));
        await SetLayoutAsync(doc.TemplateVersionId, TemplateLayout.YearWithMonthColumns);

        var m11 = doc.MonthColumnDefIds[10];
        var plain = doc.ColumnDefIds[1];
        await PutAsync(doc, november.PeriodKey, november.RowIds[0], m11, 111m); // власник M11
        await PutAsync(doc, doc.PeriodKey, doc.RowIds[0], m11, 999m);           // копія в перегляді — затінена
        await PutAsync(doc, january.PeriodKey, january.RowIds[0], plain, 5m);   // якір року
        await PutAsync(doc, doc.PeriodKey, doc.RowIds[0], plain, 7m);           // копія в перегляді — затінена

        await using var provider = Services();
        var registry = new TemplateLayoutRegistry(provider.GetRequiredService<IServiceScopeFactory>());
        await registry.RefreshAsync(CancellationToken.None);

        Assert.True(registry.HasAny);
        Assert.True(registry.IsYearVersion(doc.TemplateVersionId));
        Assert.True(registry.IsYearSheet(doc.SheetDefId));

        await using var db = _builder.CreateContext();
        var router = new MonthColumnRouter(db, FlagOff(), registry);
        Assert.True(router.Enabled);
        Assert.False(router.AllVersions);

        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, new MetadataCache(_memory, db), db);
        var slice = await store.ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, CancellationToken.None);

        Assert.Equal(111m, ValueOf(slice, doc.RowIds[0], m11));
        Assert.Equal(5m, ValueOf(slice, doc.RowIds[0], plain));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task Версія_з_місячними_копіями_лишається_фізичною_поруч_із_річною()
    {
        // Річна версія існує (реєстр непорожній), але ЦЕЙ документ — версії PerMonthCopies.
        var year = await ArrangeAsync();
        await SetLayoutAsync(year.TemplateVersionId, TemplateLayout.YearWithMonthColumns);

        var doc = await ArrangeAsync();
        var november = await _builder.AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + 11));
        var m11 = doc.MonthColumnDefIds[10];
        await PutAsync(doc, november.PeriodKey, november.RowIds[0], m11, 111m);
        await PutAsync(doc, doc.PeriodKey, doc.RowIds[0], m11, 999m);

        await using var provider = Services();
        var registry = new TemplateLayoutRegistry(provider.GetRequiredService<IServiceScopeFactory>());
        await registry.RefreshAsync(CancellationToken.None);

        Assert.True(registry.HasAny);
        Assert.False(registry.IsYearVersion(doc.TemplateVersionId));
        Assert.False(registry.IsYearSheet(doc.SheetDefId));

        await using var db = _builder.CreateContext();
        var router = new MonthColumnRouter(db, FlagOff(), registry);
        Assert.Null(await router.GetDocumentAsync(doc.DocumentId, CancellationToken.None));
        Assert.NotNull(await router.GetDocumentAsync(year.DocumentId, CancellationToken.None));

        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, new MetadataCache(_memory, db), db);
        var slice = await store.ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, CancellationToken.None);

        // Фізичний зріз: значення екземпляра перегляду, а не власника місяця.
        Assert.Equal(999m, ValueOf(slice, doc.RowIds[0], m11));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-474")]
    public async Task Без_реєстру_і_без_прапора_роутер_вимкнено()
    {
        await using var db = _builder.CreateContext();

        Assert.False(new MonthColumnRouter(db, FlagOff()).Enabled);
        Assert.False(new MonthColumnRouter(db, FlagOff(), registry: null).Enabled);
    }

    private Task<TestDocument> ArrangeAsync()
        => _builder.BuildAsync(
            periodKey: (Year * 100) + ViewMonth,
            columnCount: 2,
            rowCount: 2,
            layoutKind: TableLayoutKind.MonthsInColumns,
            monthColumns: 12);

    private async Task SetLayoutAsync(int templateVersionId, TemplateLayout layout)
    {
        await using var db = _builder.CreateContext();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == templateVersionId);
        db.Entry(version).Property(v => v.Layout).CurrentValue = layout;
        await db.SaveChangesAsync();
    }

    private async Task PutAsync(TestDocument doc, PeriodKey period, long rowId, int columnDefId, decimal value)
    {
        await using var db = _builder.CreateContext();
        db.CellValues.Add(new CellValue(
            new CellAddress(period, rowId, columnDefId),
            doc.TableDefId,
            new CellValueData { ValueNumeric = value }));
        await db.SaveChangesAsync();
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _builder.CreateContext());
        return services.BuildServiceProvider();
    }

    private static IConfiguration FlagOff()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "false" })
            .Build();

    private static decimal? ValueOf(IReadOnlyList<CellRecord> slice, long rowId, int columnDefId)
        => slice.SingleOrDefault(c => c.Address.TableRowId == rowId && c.Address.ColumnDefId == columnDefId)?.Value.ValueNumeric;
}
