using Ecr.Application.Audit;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17 (LAND-YEAR G1): журнал дій аркуша періоду перегляду показує й правки колонок, що лежать у періоді-ВЛАСНИКУ
/// (не-місячна — якір 202601; M{m} — період m), з періодом-власником у рядку.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Дефект: правку не-місячної колонки з жовтня PATCH пише в якір (<c>aud.CellChange.PeriodKey = 202601</c>), а
/// <c>SheetActivityQuery</c> фільтрував за періодом перегляду — журнал аркуша жовтня її не мав.
/// </para>
/// <para>
/// Справжній шлях: запис — <see cref="PatchCellsHandler"/> з роутером; читання — <see cref="GetSheetActivityHandler"/> над
/// справжніми <see cref="AuditReader"/>, <see cref="MonthColumnRouter"/>, <c>MetadataCache</c> і межами читання
/// <c>AccessDecisionService</c>. Доступ — диференційно (той самий запит, різниться лише Deny колонки).
/// </para>
/// <para>
/// ⚠ МУТАЦІЙНИЙ ДОКАЗ (локально, не в коміті): <c>GetSheetActivityHandler</c> передає <c>owners = null</c> — червоніє
/// <see cref="Правка_чужої_колонки_з_жовтня_видна_в_журналі_жовтня_з_періодом_власника"/>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetActivityMonthRoutedTests(SqlServerFixture sql)
{
    private const int Author = 1;
    private const int Oct = MonthRoutedReadWorld.Oct;
    private const int Jan = MonthRoutedReadWorld.Jan;
    private const int Nov = MonthRoutedReadWorld.Nov;
    private const int Mar = 202603;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Правка_чужої_колонки_з_жовтня_видна_в_журналі_жовтня_з_періодом_власника()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);
        await world.PatchAsync(Author, Oct, ("PLAIN", 7m), ("M11", 5m), ("M10", 3m));

        var items = await ActivityAsync(world, Oct, world.Profile(Author, GetSheetActivityHandler.Permission).Build());

        var edits = items.Where(i => i.Kind == SheetActivityKinds.CellEdit)
            .ToDictionary(i => i.ColumnDefId!.Value, i => (i.PeriodKey, Value: MonthRoutedReadWorld.Trim(i.NewValue)));
        Assert.Equal((Jan, "7"), edits[world.Plain]);
        Assert.Equal((Nov, "5"), edits[world.Month(11)]);
        Assert.Equal((Oct, "3"), edits[world.Month(10)]);

        // І з якоря: та сама правка — у журналі січня (його власна колонка), M10 — з жовтня.
        var fromJanuary = await ActivityAsync(world, Jan, world.Profile(Author, GetSheetActivityHandler.Permission).Build());
        Assert.Contains(fromJanuary, i => i.ColumnDefId == world.Plain && i.PeriodKey == Jan);
        Assert.Contains(fromJanuary, i => i.ColumnDefId == world.Month(10) && i.PeriodKey == Oct);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Deny_колонки_ховає_правку_з_власника_диференційно()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);
        await world.PatchAsync(Author, Oct, ("PLAIN", 7m), ("M11", 5m));

        // Той самий запит (жовтень, той самий аркуш), різниться лише Deny колонки PLAIN.
        var allowed = await ActivityAsync(world, Oct, world.Profile(Author, GetSheetActivityHandler.Permission).Build());
        var denied = await ActivityAsync(
            world, Oct, world.Profile(Author, GetSheetActivityHandler.Permission).Deny(ResourceKind.Column, world.Plain).Build());

        Assert.Contains(allowed, i => i.ColumnDefId == world.Plain);
        Assert.DoesNotContain(denied, i => i.ColumnDefId == world.Plain);
        Assert.Contains(denied, i => i.ColumnDefId == world.Month(11) && i.PeriodKey == Nov);
    }

    /// <summary>
    /// D-214 (межа періоду-ВЛАСНИКА): читач бачить PLAIN у жовтні (логічна адреса), але роль, звужена січнем (якір —
    /// власник PLAIN), її забороняє — правки з якоря в журналі жовтня немає. Контроль — та сама заборона, звужена
    /// березнем (ні перегляд, ні власник), — правка є з періодом-власником.
    /// </summary>
    /// <remarks>
    /// Диференційно: обидва профілі однакові, різниться лише період шару; передумову (видно в жовтні, закрито / відкрито
    /// в січні) перевіряють справжні межі читання. Без межі D-214 в <c>OwnerPeriodColumns.CollectAsync</c> пара
    /// (202601, PLAIN) пішла б у запит і цей тест червонів би.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    [Trait("Decision", "D-214")]
    public async Task Межа_D214_власника_ховає_правку_з_якоря_диференційно()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);
        await world.PatchAsync(Author, Oct, ("PLAIN", 7m), ("M11", 5m));

        var denied = world.DeniedInPeriod(world.Profile(Author, GetSheetActivityHandler.Permission), world.Plain, Jan);
        var control = world.DeniedInPeriod(world.Profile(Author, GetSheetActivityHandler.Permission), world.Plain, Mar);
        Assert.Equal((true, false), await world.ColumnReadableAsync(denied, world.Plain, Oct, Jan));
        Assert.Equal((true, true), await world.ColumnReadableAsync(control, world.Plain, Oct, Jan));

        var shown = await ActivityAsync(world, Oct, control);
        var hidden = await ActivityAsync(world, Oct, denied);

        Assert.Contains(shown, i => i.ColumnDefId == world.Plain && i.PeriodKey == Jan);
        Assert.DoesNotContain(hidden, i => i.ColumnDefId == world.Plain);
        Assert.Contains(hidden, i => i.ColumnDefId == world.Month(11) && i.PeriodKey == Nov);
    }

    /// <summary>
    /// Контроль: маршрутизація вимкнена — правка фізична в жовтні, журнал той самий, а в порт іде ОДИН запит без пар
    /// власників (ні звернень роутера до бази, ні зміни тексту запиту).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Без_маршрутизації_журнал_і_запит_як_були()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString, routing: false);
        await world.PatchAsync(Author, Oct, ("PLAIN", 7m));

        var filters = new List<CellChangeFilter>();
        var items = await ActivityAsync(world, Oct, world.Profile(Author, GetSheetActivityHandler.Permission).Build(), filters);

        Assert.Contains(items, i => i.ColumnDefId == world.Plain && i.PeriodKey == Oct);
        var filter = Assert.Single(filters);
        Assert.Null(filter.OwnerPeriodColumns);
        Assert.Equal(Oct, filter.PeriodKey);
    }

    private static async Task<IReadOnlyList<SheetActivityItem>> ActivityAsync(
        MonthRoutedReadWorld world, int viewPeriod, AccessProfile reader, List<CellChangeFilter>? filters = null)
    {
        await using var db = world.CreateContext();
        var router = world.Router(db);
        var metadata = world.Metadata(db);
        var access = world.Access(db, reader, metadata, router);
        var user = MonthRoutedReadWorld.User(Author);

        // Порт журналу — справжній; обгортка лише записує фільтри (скільки запитів і з чим).
        var real = new AuditReader(db);
        var audit = Substitute.For<IAuditReader>();
        audit.ReadCellChangesAsync(Arg.Any<CellChangeFilter>(), Arg.Any<Ecr.Application.Common.CursorRequest>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                filters?.Add(c.ArgAt<CellChangeFilter>(0));
                return real.ReadCellChangesAsync(
                    c.ArgAt<CellChangeFilter>(0), c.ArgAt<Ecr.Application.Common.CursorRequest>(1), c.ArgAt<CancellationToken>(2));
            });

        var handler = new GetSheetActivityHandler(
            new GetDocumentHandler(new DocumentStore(db), access, user),
            new SheetActivityQuery(audit, new WorkflowStore(db), clock: null, rowChanges: null),
            access, user, router, metadata);

        var page = await handler.HandleAsync(
            world.Doc.DocumentId, world.Doc.SheetDefId, viewPeriod, byUserId: null, includeSystem: false, limit: 100,
            cursor: null, CancellationToken.None);
        return page.Items;
    }
}
