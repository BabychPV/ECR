using Ecr.Application.Documents.SheetChanges;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17 (LAND-YEAR G4): «дані аркуша змінив хтось інший» (живе оновлення, <c>GET …/sheets/{id}/changes</c>) бачить і
/// правку колонки, що лежить в екземплярі ВЛАСНИКА (не-місячна — якір 202601; M{m} — період m), зроблену з іншого місяця.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Дефект (перевірено цим тестом до фіксу): проба порівнювала <c>rowversion</c> лише рядків екземпляра перегляду.
/// Правка PLAIN з січня торкається лише рядка якоря, правка M11 з листопада — лише листопада; жовтень у сітці показує
/// обидві колонки, а банер мовчав.
/// </para>
/// <para>
/// Справжній шлях: запис — <c>PatchCellsHandler</c> з роутером від іншого користувача; опитування —
/// <see cref="GetSheetChangesHandler"/> над справжніми <see cref="SheetChangeStore"/>, <see cref="MonthColumnRouter"/>,
/// <c>MetadataCache</c> і межами читання <c>AccessDecisionService</c>.
/// </para>
/// <para>
/// ⚠ МУТАЦІЙНИЙ ДОКАЗ (локально, не в коміті): <c>OwnerTargetsAsync</c> повертає <c>null</c> — червоніє
/// <see cref="Чужа_правка_з_іншого_місяця_будить_банер_жовтня"/>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetChangesMonthRoutedTests(SqlServerFixture sql)
{
    private const int Viewer = 1;
    private const int Other = 2;
    private const int Oct = MonthRoutedReadWorld.Oct;
    private const int Jan = MonthRoutedReadWorld.Jan;
    private const int Nov = MonthRoutedReadWorld.Nov;
    private const int Mar = 202603;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Чужа_правка_з_іншого_місяця_будить_банер_жовтня()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);

        var first = await PollAsync(world, since: null);
        Assert.False(first.Changed);

        // Не-місячна колонка з якоря: торкається лише рядка січня.
        await world.PatchAsync(Other, Jan, ("PLAIN", 7m));
        var afterAnchor = await PollAsync(world, first.Marker);

        Assert.True(afterAnchor.Changed);
        Assert.Equal(Other, afterAnchor.LastChangedByUserId);
        var table = Assert.Single(afterAnchor.ChangedTables);
        Assert.Equal(world.Instances[Oct], table.TableInstanceId);

        // Місячна M11 з листопада: торкається лише рядка листопада, а жовтень її показує.
        await world.PatchAsync(Other, Nov, ("M11", 5m));
        var afterMonth = await PollAsync(world, afterAnchor.Marker);

        Assert.True(afterMonth.Changed);
        Assert.Equal(world.Instances[Oct], Assert.Single(afterMonth.ChangedTables).TableInstanceId);

        // Нічого нового — тиша.
        Assert.False((await PollAsync(world, afterMonth.Marker)).Changed);
    }

    /// <summary>
    /// D-214 (межа періоду-ВЛАСНИКА): глядач бачить PLAIN у жовтні, але роль, звужена січнем (якір — власник PLAIN), її
    /// забороняє — чужа правка PLAIN у якорі банер жовтня НЕ будить і автора не називає. Контроль — та сама заборона,
    /// звужена березнем, — банер з автором.
    /// </summary>
    /// <remarks>
    /// Диференційно: профілі однакові, різниться лише період шару; передумову перевіряють справжні межі читання. Без
    /// межі D-214 в <c>GetSheetChangesHandler.OwnerTargetsAsync</c> PLAIN не потрапила б у сховані для авторства — і
    /// банер прокинувся б.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    [Trait("Decision", "D-214")]
    public async Task Межа_D214_власника_глушить_банер_диференційно()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);

        var denied = world.DeniedInPeriod(world.Profile(Viewer, GetSheetChangesHandler.Permission), world.Plain, Jan);
        var control = world.DeniedInPeriod(world.Profile(Viewer, GetSheetChangesHandler.Permission), world.Plain, Mar);
        Assert.Equal((true, false), await world.ColumnReadableAsync(denied, world.Plain, Oct, Jan));
        Assert.Equal((true, true), await world.ColumnReadableAsync(control, world.Plain, Oct, Jan));

        var deniedFirst = await PollAsync(world, since: null, viewer: denied);
        var controlFirst = await PollAsync(world, since: null, viewer: control);

        await world.PatchAsync(Other, Jan, ("PLAIN", 7m));

        var shown = await PollAsync(world, controlFirst.Marker, viewer: control);
        var hidden = await PollAsync(world, deniedFirst.Marker, viewer: denied);

        Assert.True(shown.Changed);
        Assert.Equal(Other, shown.LastChangedByUserId);
        Assert.False(hidden.Changed);
        Assert.Null(hidden.LastChangedByUserId);
    }

    /// <summary>
    /// Контроль: маршрутизація вимкнена — правка в екземплярі січня жовтня не стосується (банера немає, як і було), а
    /// сховище отримує ту саму єдину пробу періоду перегляду, без проби власників.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task Без_маршрутизації_проба_як_була()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString, routing: false);

        var first = await PollAsync(world, since: null);
        await world.PatchAsync(Other, Jan, ("PLAIN", 7m));

        var calls = new List<string>();
        var after = await PollAsync(world, first.Marker, calls);

        Assert.False(after.Changed);
        Assert.Equal(["Status", "Probe"], calls);
    }

    /// <summary>
    /// B43: автор правит НЕ-місячну колонку з жовтня — PATCH пише її в якір (січень, там і журнал), а рядок жовтня лише
    /// «захоплює» (claim версії клієнта, без запису в журнал). Наступне опитування самого автора — без банера «змінено іншим».
    /// </summary>
    /// <remarks>
    /// ⛔ Дефект (червоний до фіксу): проба бачила рядок жовтня з новою версією, журнал якого порожній, і рахувала його
    /// «чужим з невідомим автором» — <c>Changed = true</c> на власну правку.
    /// ⚠ МУТАЦІЙНИЙ ДОКАЗ (локально, не в коміті): прибрати в <c>GetSheetChangesHandler</c> перенесення авторства з
    /// рядка власника (<c>ClaimedByOwnerPeer</c> → порожній набір) — червоніє цей тест; і окремо — проба власників без
    /// <c>RowKey</c> (<c>SheetChangeStore.ReadProbeAsync</c> без 5-ї/6-ї колонок) — теж.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task B43_власна_правка_немісячної_колонки_з_жовтня_банера_не_будить()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);

        var first = await PollAsync(world, since: null);

        await world.PatchAsync(Viewer, Oct, ("PLAIN", 7m));
        var after = await PollAsync(world, first.Marker);

        Assert.False(after.Changed);
        Assert.Null(after.LastChangedByUserId);
        Assert.Empty(after.ChangedTables);

        // Місячна колонка ІНШОГО місяця з жовтня (власник — березень, екземпляр створюється ліниво) — так само тиша.
        await world.PatchAsync(Viewer, Oct, ("M3", 4m));
        Assert.False((await PollAsync(world, after.Marker)).Changed);
    }

    /// <summary>
    /// B43 (контроль, без регресії справжнього сигналу): ту саму правку з жовтня робить ІНШИЙ користувач — банер є,
    /// з його іменем і таблицею жовтня; і власна правка після чужої в межах одного опитування чужу не ховає.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task B43_чужа_правка_немісячної_колонки_з_жовтня_будить_банер()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);

        var first = await PollAsync(world, since: null);

        await world.PatchAsync(Other, Oct, ("PLAIN", 7m));
        var after = await PollAsync(world, first.Marker);

        Assert.True(after.Changed);
        Assert.Equal(Other, after.LastChangedByUserId);
        Assert.Equal(world.Instances[Oct], Assert.Single(after.ChangedTables).TableInstanceId);

        // Чужа місячна M3 (березень) і власна PLAIN (січень) у тому самому вікні: рядок жовтня «захоплено» обома, але
        // чужа зміна рядка березня лишається чужою.
        await world.PatchAsync(Other, Oct, ("M3", 5m));
        await world.PatchAsync(Viewer, Oct, ("PLAIN", 8m));
        var mixed = await PollAsync(world, after.Marker);

        Assert.True(mixed.Changed);
        Assert.Equal(Other, mixed.LastChangedByUserId);
    }

    /// <summary>
    /// B43 (рев'ю F13, п. 1): структурна зміна ІНШОГО користувача без журналу комірок у таблиці Q-17 — видалення рядка в
    /// усіх екземплярах року (як <c>DeleteRowHandler</c>) і дзеркальне додавання рядка — банер будить (автор невідомий).
    /// </summary>
    /// <remarks>
    /// ⚠ МУТАЦІЙНИЙ ДОКАЗ (локально): у режимі проби власників рядок без журналу не вважається чужим — червоніє.
    /// Мутація «пара власника без журналу теж рахується» (умова <c>AttributedRowIds</c> пари в <c>ClaimedByOwnerPeer</c>)
    /// ЕКВІВАЛЕНТНА — перевірено, тест зелений: видалення/додавання Q-17 іде в усі екземпляри року, рядок власника без
    /// журналу чужий сам по собі, і банер дає він (див. примітку в обробнику). Мутацію «без перевірки пари» ловить
    /// <see cref="B43_пара_власника_лише_в_тій_самій_таблиці"/>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task B43_чуже_видалення_й_додавання_рядка_Q17_будять_банер()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);

        var first = await PollAsync(world, since: null);

        await world.SoftDeleteRowAsync(Oct, Jan, Nov);
        var deleted = await PollAsync(world, first.Marker);

        Assert.True(deleted.Changed);
        Assert.Null(deleted.LastChangedByUserId);
        Assert.Equal(world.Instances[Oct], Assert.Single(deleted.ChangedTables).TableInstanceId);

        await world.AddRowAsync("B43NEW", Oct, Jan, Nov);
        var added = await PollAsync(world, deleted.Marker);

        Assert.True(added.Changed);
        Assert.Equal(world.Instances[Oct], Assert.Single(added.ChangedTables).TableInstanceId);
    }

    /// <summary>
    /// B43 (рев'ю F13, п. 2): той самий <c>RowKey</c> у ДВОХ таблицях аркуша. Власна правка PLAIN (пара у власника — у
    /// таблиці «місяці в колонках») не глушить чуже видалення рядка з тим самим ключем в ІНШІЙ таблиці.
    /// </summary>
    /// <remarks>⚠ МУТАЦІЙНИЙ ДОКАЗ (локально): пара власника без перевірки <c>TableDefId</c> — червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "Q-17")]
    public async Task B43_пара_власника_лише_в_тій_самій_таблиці()
    {
        using var world = await MonthRoutedReadWorld.ArrangeAsync(sql.ConnectionString);
        var (twinInstance, twinRow) = await world.AddTwinTableAsync();

        var first = await PollAsync(world, since: null);

        await world.PatchAsync(Viewer, Oct, ("PLAIN", 7m));
        await world.SoftDeleteOctRowAsync(twinRow);
        var after = await PollAsync(world, first.Marker);

        Assert.True(after.Changed);
        Assert.Contains(after.ChangedTables, t => t.TableInstanceId == twinInstance);
        Assert.DoesNotContain(after.ChangedTables, t => t.TableInstanceId == world.Instances[Oct]);
    }

    private static async Task<SheetChangesDto> PollAsync(
        MonthRoutedReadWorld world, string? since, List<string>? calls = null, AccessProfile? viewer = null)
    {
        await using var db = world.CreateContext();
        var router = world.Router(db);
        var metadata = world.Metadata(db);
        var access = world.Access(
            db, viewer ?? world.Profile(Viewer, GetSheetChangesHandler.Permission).Build(), metadata, router);

        // Сховище — справжнє; обгортка лише записує, які звернення були.
        var real = new SheetChangeStore(db);
        var store = Substitute.For<ISheetChangeStore>();
        store.CurrentMarkerAsync(Arg.Any<CancellationToken>())
            .Returns(c => real.CurrentMarkerAsync(c.ArgAt<CancellationToken>(0)));
        store.SheetStatusAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<long?>())
            .Returns(c =>
            {
                calls?.Add("Status");
                return real.SheetStatusAsync(
                    c.ArgAt<long>(0), c.ArgAt<int>(1), c.ArgAt<int>(2), c.ArgAt<CancellationToken>(3), c.ArgAt<long?>(4));
            });
        store.ProbeAsync(
                Arg.Any<long>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<ulong>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>(), Arg.Any<long?>())
            .Returns(c =>
            {
                calls?.Add("Probe");
                return real.ProbeAsync(
                    c.ArgAt<long>(0), c.ArgAt<int>(1), c.ArgAt<IReadOnlyCollection<int>>(2), c.ArgAt<ulong>(3), c.ArgAt<int>(4),
                    c.ArgAt<CancellationToken>(5), c.ArgAt<long?>(6));
            });
        store.ProbeOwnersAsync(
                Arg.Any<long>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<SheetProbeTarget>>(), Arg.Any<ulong>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                calls?.Add("ProbeOwners");
                return real.ProbeOwnersAsync(
                    c.ArgAt<long>(0), c.ArgAt<int>(1), c.ArgAt<IReadOnlyCollection<SheetProbeTarget>>(2), c.ArgAt<ulong>(3),
                    c.ArgAt<int>(4), c.ArgAt<CancellationToken>(5));
            });
        store.AttributeAsync(
                Arg.Any<long>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateTime>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                calls?.Add("Attribute");
                return real.AttributeAsync(
                    c.ArgAt<long>(0), c.ArgAt<IReadOnlyCollection<long>>(1), c.ArgAt<IReadOnlyCollection<int>>(2),
                    c.ArgAt<DateTime>(3), c.ArgAt<int>(4), c.ArgAt<CancellationToken>(5));
            });

        var handler = new GetSheetChangesHandler(
            access, MonthRoutedReadWorld.User(Viewer), store, new TestClock(MonthRoutedReadWorld.Now), router, metadata);

        return await handler.HandleAsync(world.Doc.DocumentId, world.Doc.SheetDefId, Oct, since, CancellationToken.None);
    }
}
