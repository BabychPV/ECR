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
