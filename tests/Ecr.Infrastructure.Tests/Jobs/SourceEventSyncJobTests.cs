// tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncJobTests.cs
using System.Globalization;
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="SourceEventSyncJob"/> на РЕАЛЬНОМУ SQL Server із справжнім записом рядків
/// (HSE301 A5b, FEATURE-HSE301-VIEW §4.7.4): подія джерела → рядок <c>EF-…</c> і зв'язок.
/// </summary>
/// <remarks>
/// ⚠ Усе справжнє й зібране контейнером, як у <c>IntegrationRowUpsertTests</c>: патчер,
/// <c>PatchCellsHandler</c>, автор <c>svc-integration</c>; підроблене лише джерело подій і годинник.
/// Динамічна таблиця з колонками: назва (String), об'єм (Decimal), категорія (Lookup), Start/End (Date);
/// проєкт у <c>Asia/Atyrau</c> (+05:00), періоди січень і лютий 2026.
///
/// Мутаційні докази (A5b), кожен — точковою правкою <c>SourceEventSyncJob</c> чи планувальника:
/// <list type="bullet">
/// <item>рядок без перевірки існування: <c>RowExists = after.Contains(key)</c> → <c>true</c> — червоніє
/// <see cref="Стеля_рядків_нового_рядка_дає_RowLimit_а_після_підняття_стелі_Synced"/>;</item>
/// <item>закритий період пишеться (гілка <c>Closed</c> планувальника → <c>Write</c>) —
/// червоніє <see cref="Закритий_період_нуль_записів_і_позначка_у_зв_язку"/>;</item>
/// <item><c>Missing</c> без урахування <c>Truncated</c> — червоніє <see cref="Зникла_подія_Missing_лише_при_повному_читанні"/>;</item>
/// <item>природний ключ вимкнено — червоніє <see cref="Перестворена_подія_лишається_в_тому_самому_рядку"/>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceEventSyncJobTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 5, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = new(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 1, 28, 9, 9, 20, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 1, 28, 9, 24, 50, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Нова_подія_створює_рядок_EF_id_із_часом_у_поясі_проєкту_і_зв_язок_Synced()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, End, category: "V8", volume: 269.258m));

        await RunAsync(stand, source);

        var row = Assert.Single(await RowsAsync(stand));
        Assert.Equal(("EF-E1", stand.JanuaryInstanceId), (row.RowKey, row.TableInstanceId));

        var cells = await CellsAsync(stand, "EF-E1");
        Assert.Equal(new DateTime(2026, 1, 28, 14, 9, 20), cells[stand.StartColumn].Date);
        Assert.Equal(new DateTime(2026, 1, 28, 14, 24, 50), cells[stand.EndColumn].Date);
        Assert.Equal("Flaring HP", cells[stand.NameColumn].Text);
        Assert.Equal(269.258m, cells[stand.VolumeColumn].Numeric);
        Assert.Equal(stand.V8EntryId, cells[stand.CategoryColumn].EntryId);

        var link = Assert.Single(await LinksAsync(stand));
        Assert.Equal(
            ("E1", SourceEventLinkStatus.Synced, 202601, stand.JanuaryInstanceId, "EF-E1"),
            (link.SourceEventId, link.Status, link.PeriodKey, link.TableInstanceId, link.RowKey));

        // Читання — за шаблоном сутності й атрибутами мапінгу без зарезервованих.
        Assert.Equal(stand.EntityCode, source.LastQuery!.Template);
        Assert.Equal(["Category", "Volume"], source.LastQuery.Attributes.Select(a => a.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Повторний_прогін_ідемпотентний_без_дублів_і_зайвих_записів()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, End, category: "V8", volume: 269.258m));

        await RunAsync(stand, source);
        var version = await RowVersionAsync(stand, "EF-E1");
        var audit = await AuditCountAsync(stand);

        await RunAsync(stand, source);

        Assert.Single(await RowsAsync(stand));
        Assert.Single(await LinksAsync(stand));
        Assert.Equal(version, await RowVersionAsync(stand, "EF-E1"));
        Assert.Equal(audit, await AuditCountAsync(stand));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    [InlineData(false, SourceEventLinkStatus.Missing)]
    [InlineData(true, SourceEventLinkStatus.Synced)]
    public async Task Зникла_подія_Missing_лише_при_повному_читанні(bool truncated, SourceEventLinkStatus expected)
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, End));
        await RunAsync(stand, source);
        var version = await RowVersionAsync(stand, "EF-E1");

        source.Result = new SourceEventResult([], truncated, null);
        await RunAsync(stand, source);

        Assert.Equal(expected, Assert.Single(await LinksAsync(stand)).Status);

        // Рядок не видаляється й не змінюється в обох випадках.
        Assert.Single(await RowsAsync(stand));
        Assert.Equal(version, await RowVersionAsync(stand, "EF-E1"));

        // Подія повернулась — знову Synced.
        source.Result = new SourceEventResult([Ev("E1", Start, End)], false, null);
        await RunAsync(stand, source);
        Assert.Equal(SourceEventLinkStatus.Synced, Assert.Single(await LinksAsync(stand)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task ПатчерСкидаєТрекер_ЗвязокOpenСтаєSynced()
    {
        // ⛔ L3-03: патчер ділить scoped-контекст із задачею і на гонці за рядок робить
        // ChangeTracker.Clear(). До фіксу зміни зв'язків етапу 2 (тут Open → Synced) мовчки
        // губилися: SaveChangesAsync відчеплених не бачив. Мутація: прибрати перечитування
        // links після WriteRowsAsync у SourceEventSyncJob.SyncMapAsync.
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, null));
        await RunAsync(stand, source);
        Assert.Equal(SourceEventLinkStatus.Open, Assert.Single(await LinksAsync(stand)).Status);

        source.Result = new SourceEventResult([Ev("E1", Start, End, volume: 5m)], false, null);
        await RunAsync(stand, source, clearTrackerOnWrite: true);

        Assert.Single(await RowsAsync(stand));
        Assert.Equal(SourceEventLinkStatus.Synced, Assert.Single(await LinksAsync(stand)).Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Незакрита_подія_не_рахується_і_перечитується_після_закриття()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, null));

        await RunAsync(stand, source);

        Assert.Empty(await RowsAsync(stand));
        var open = Assert.Single(await LinksAsync(stand));
        Assert.Equal((SourceEventLinkStatus.Open, false), (open.Status, open.HasRow));

        // PI закрив подію: той самий зв'язок стає Synced, рядок з'являється.
        source.Result = new SourceEventResult([Ev("E1", Start, End)], false, null);
        await RunAsync(stand, source);

        Assert.Single(await RowsAsync(stand));
        var closed = Assert.Single(await LinksAsync(stand));
        Assert.Equal((open.Id, SourceEventLinkStatus.Synced, "EF-E1"), (closed.Id, closed.Status, closed.RowKey));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Закритий_період_нуль_записів_і_позначка_у_зв_язку()
    {
        await using var stand = await ArrangeAsync();
        await using (var db = sql.CreateContext())
        {
            var period = await db.Periods.SingleAsync(p => p.ProjectId == stand.ProjectId && p.PeriodKeyValue == 202601);
            period.AdvanceTo(PeriodState.Closed, Now);
            await db.SaveChangesAsync();
        }

        var source = new FakeEventSource(Ev("E1", Start, End));
        var audit = await AuditCountAsync(stand);

        await RunAsync(stand, source);

        Assert.Empty(await RowsAsync(stand));
        Assert.Equal(audit, await AuditCountAsync(stand));
        var link = Assert.Single(await LinksAsync(stand));
        Assert.Equal((SourceEventLinkStatus.PeriodClosed, false), (link.Status, link.HasRow));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Період_за_початком_у_поясі_проєкту_межа_доби_Atyrau()
    {
        await using var stand = await ArrangeAsync();

        // 18:59:59Z — 23:59:59 31 січня в Atyrau; 19:00:00Z — уже 00:00:00 1 лютого.
        var source = new FakeEventSource(
            Ev("JAN", new DateTime(2026, 1, 31, 18, 59, 59, DateTimeKind.Utc), new DateTime(2026, 1, 31, 19, 30, 0, DateTimeKind.Utc)),
            Ev("FEB", new DateTime(2026, 1, 31, 19, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 31, 19, 30, 0, DateTimeKind.Utc)));

        await RunAsync(stand, source);

        var rows = (await RowsAsync(stand)).ToDictionary(r => r.RowKey, r => r.TableInstanceId);
        Assert.Equal(stand.JanuaryInstanceId, rows["EF-JAN"]);
        Assert.Equal(stand.FebruaryInstanceId, rows["EF-FEB"]);
        Assert.Equal(
            new DateTime(2026, 2, 1, 0, 0, 0),
            (await CellsAsync(stand, "EF-FEB"))[stand.StartColumn].Date);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Лише_кореневі_події_дочірня_не_дає_ні_рядка_ні_зв_язку()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("ROOT", Start, End), Ev("CHILD", Start.AddMinutes(1), End, parent: "ROOT"));

        await RunAsync(stand, source);

        Assert.Equal(["EF-ROOT"], (await RowsAsync(stand)).Select(r => r.RowKey));
        Assert.Equal(["ROOT"], (await LinksAsync(stand)).Select(l => l.SourceEventId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Перестворена_подія_лишається_в_тому_самому_рядку()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("OLD", Start, End));
        await RunAsync(stand, source);

        // PI перестворив EF: інший ID, той самий шаблон, початок і назва.
        source.Result = new SourceEventResult([Ev("NEW", Start, End.AddMinutes(1))], false, null);
        await RunAsync(stand, source);

        Assert.Equal(["EF-OLD"], (await RowsAsync(stand)).Select(r => r.RowKey));
        var link = Assert.Single(await LinksAsync(stand));
        Assert.Equal(("NEW", "EF-OLD", SourceEventLinkStatus.Synced), (link.SourceEventId, link.RowKey, link.Status));
        Assert.Equal(new DateTime(2026, 1, 28, 14, 25, 50), (await CellsAsync(stand, "EF-OLD"))[stand.EndColumn].Date);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Значення_які_колонка_не_приймає_не_створюють_синхронізованого_зв_язку()
    {
        await using var stand = await ArrangeAsync();

        // Категорії V10 у довіднику немає: значення лягає в «незіставлене», решта рядка — записана.
        var source = new FakeEventSource(Ev("E1", Start, End, category: "V10", volume: 5m));

        await RunAsync(stand, source);

        var cells = await CellsAsync(stand, "EF-E1");
        Assert.Equal(5m, cells[stand.VolumeColumn].Numeric);
        Assert.False(cells.ContainsKey(stand.CategoryColumn));

        var link = Assert.Single(await LinksAsync(stand));
        Assert.Equal(SourceEventLinkStatus.Unmapped, link.Status);
        Assert.Contains("V10", link.UnmappedJson);
        Assert.Contains(stand.CategoryCode, link.UnmappedJson);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Стеля_рядків_нового_рядка_дає_RowLimit_а_після_підняття_стелі_Synced()
    {
        await using var stand = await ArrangeAsync();

        // Таблиця порожня; стеля 1 пропускає лише одну подію.
        await ExecuteAsync($"UPDATE cfg.TableDef SET MaxDynamicRows = 1 WHERE Id = {stand.TableDefId}");
        var source = new FakeEventSource(Ev("E1", Start, End), Ev("E2", Start.AddHours(1), End.AddHours(1)));

        await RunAsync(stand, source, freshContainer: true);

        Assert.Equal(["EF-E1"], (await RowsAsync(stand)).Select(r => r.RowKey));
        var links = (await LinksAsync(stand)).ToDictionary(l => l.SourceEventId);
        Assert.Equal(SourceEventLinkStatus.Synced, links["E1"].Status);
        Assert.Equal((SourceEventLinkStatus.RowLimit, false), (links["E2"].Status, links["E2"].HasRow));

        await ExecuteAsync($"UPDATE cfg.TableDef SET MaxDynamicRows = NULL WHERE Id = {stand.TableDefId}");
        await RunAsync(stand, source, freshContainer: true);

        Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.All(await LinksAsync(stand), l => Assert.Equal(SourceEventLinkStatus.Synced, l.Status));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    [Trait("Finding", "D-118")]
    public async Task Правка_людини_не_перезаписується_і_йде_в_KeptManualJson()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, End, volume: 10m));
        await RunAsync(stand, source);

        await HumanWriteAsync(stand, "EF-E1", new PatchCell(stand.VolumeCode, 42m));

        source.Result = new SourceEventResult([Ev("E1", Start, End, volume: 99m)], false, null);
        await RunAsync(stand, source);

        Assert.Equal(42m, (await CellsAsync(stand, "EF-E1"))[stand.VolumeColumn].Numeric);
        var link = Assert.Single(await LinksAsync(stand));
        Assert.Equal(SourceEventLinkStatus.Synced, link.Status);
        Assert.Contains(stand.VolumeCode, link.KeptManualJson);
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: викликати тригер на кожну групу/подію замість одного разу на період — червоніє
    /// «рівно два виклики»; викликати без <c>applied &gt; 0</c> — червоніє «повтор не ставить».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A5b")]
    public async Task Автоперерахунок_один_раз_на_період_і_лише_коли_щось_записано()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        var january = new DateTime(2026, 1, 28, 9, 0, 0, DateTimeKind.Utc);
        var february = new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc);
        var source = new FakeEventSource(
            Ev("J1", january, january.AddMinutes(5)),
            Ev("J2", january.AddHours(1), january.AddHours(1).AddMinutes(5)),
            Ev("F1", february, february.AddMinutes(5)));

        await RunAsync(stand, source, trigger: trigger);

        // Дві події січня — один виклик; лютий — свій.
        await trigger.Received(1).RequestAsync(stand.DocumentId, new PeriodKey(202601), Arg.Any<CancellationToken>());
        await trigger.Received(1).RequestAsync(stand.DocumentId, new PeriodKey(202602), Arg.Any<CancellationToken>());
        Assert.Equal(2, trigger.ReceivedCalls().Count());

        // Повтор без змін: нуль записаного - нуль викликів.
        trigger.ClearReceivedCalls();
        await RunAsync(stand, source, trigger: trigger);
        Assert.Empty(trigger.ReceivedCalls());
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ (борг перерахунку): повернути виклик перерахунку ПІСЛЯ <c>SaveChangesAsync</c> без
    /// <c>finally</c> — червоніє: рядок уже записано, зв'язки впали, а перерахунок не поставлено.
    /// Збій зв'язків імітує тригер БД на <c>ext.SourceEventLink</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "enterprise-2-P2-recalc-debt")]
    public async Task Збій_збереження_зв_язків_після_запису_рядків_усе_одно_ставить_перерахунок()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        var source = new FakeEventSource(Ev("E1", Start, End));

        await ExecuteAsync(
            "CREATE OR ALTER TRIGGER ext.TR_rc4_SourceEventLink_fail ON ext.SourceEventLink AFTER INSERT AS THROW 51000, 'rc4 link save failure', 1;");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(stand, source, trigger: trigger));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER IF EXISTS ext.TR_rc4_SourceEventLink_fail;");
        }

        // Рядок уже в базі (закомічено патчером), зв'язку немає — і перерахунок усе одно поставлено.
        Assert.Single(await RowsAsync(stand));
        Assert.Empty(await LinksAsync(stand));
        await trigger.Received(1).RequestAsync(stand.DocumentId, new PeriodKey(202601), Arg.Any<CancellationToken>());
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНІ ДОКАЗИ (збій постановки не маскує початковий): прибрати try/catch у <c>finally</c> —
    /// червоніє тест «…пробрасується_початковий_збій…»; замінити <c>catch … when (initial is not null)</c>
    /// на безумовне проковтування — червоніє «…без_початкового_збою_постановка_падає…».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "enterprise-2-P2-recalc-debt")]
    public async Task Збій_збереження_і_збій_постановки_перерахунку_пробрасується_початковий_збій_а_постановка_логується()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("recalc-enqueue-failure"));
        var logger = new CollectingLogger();
        var source = new FakeEventSource(Ev("E1", Start, End));

        await ExecuteAsync(
            "CREATE OR ALTER TRIGGER ext.TR_rc4_SourceEventLink_fail ON ext.SourceEventLink AFTER INSERT AS THROW 51000, 'rc4 link save failure', 1;");
        Exception thrown;
        try
        {
            thrown = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(stand, source, trigger: trigger, logger: logger));
        }
        finally
        {
            await ExecuteAsync("DROP TRIGGER IF EXISTS ext.TR_rc4_SourceEventLink_fail;");
        }

        Assert.IsAssignableFrom<DbUpdateException>(thrown);
        Assert.DoesNotContain("recalc-enqueue-failure", thrown.ToString());
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("recalc-enqueue-failure", entry.Exception?.Message);
        Assert.Contains(stand.DocumentId.ToString(CultureInfo.InvariantCulture), entry.Message);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "enterprise-2-P2-recalc-debt")]
    public async Task Без_початкового_збою_постановка_перерахунку_падає_і_не_ковтається()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("recalc-enqueue-failure"));
        var logger = new CollectingLogger();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(stand, new FakeEventSource(Ev("E1", Start, End)), trigger: trigger, logger: logger));

        Assert.Equal("recalc-enqueue-failure", ex.Message);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "enterprise-2-P2-recalc-debt")]
    public async Task Без_збоїв_перерахунок_ставиться_зв_язок_збережено_лог_порожній()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        var logger = new CollectingLogger();

        await RunAsync(stand, new FakeEventSource(Ev("E1", Start, End)), trigger: trigger, logger: logger);

        Assert.Single(await LinksAsync(stand));
        await trigger.Received(1).RequestAsync(stand.DocumentId, new PeriodKey(202601), Arg.Any<CancellationToken>());
        Assert.Empty(logger.Entries);
    }

    // ── «Повна звірка за період» (рішення людини 2026-10-01: ID EventFrame нестабільні) ──
    //
    // ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати RemoveGoneAsync (або DeleteRowAsync) — червоніють T1/T7/T8;
    // прибрати гілку plan.SourceEmpty — червоніє T4; прибрати перевірку Open/Grace — червоніє T5;
    // прибрати перевірку manual/KeptManualJson — червоніє T6; додавати період в appliedPeriods на кожну
    // подію замість множини — червоніє T7 (рівно один виклик).

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task T1_Подія_зникла_з_джерела_рядок_і_зв_язок_видалені_перерахунок_поставлено_а_повтор_нічого_не_змінює()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        var source = new FakeEventSource(
            Ev("E1", Start, End), Ev("E2", Start.AddHours(1), End.AddHours(1)));
        await RunAsync(stand, source, trigger: trigger);
        trigger.ClearReceivedCalls();

        source.Result = new SourceEventResult([Ev("E2", Start.AddHours(1), End.AddHours(1))], false, null);
        await RunAsync(stand, source, trigger: trigger);

        Assert.Equal(["EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey));
        Assert.Equal(["E2"], (await LinksAsync(stand)).Select(l => l.SourceEventId));
        Assert.Empty(await CellsAsync(stand, "EF-E1"));
        await trigger.Received(1).RequestAsync(stand.DocumentId, new PeriodKey(202601), Arg.Any<CancellationToken>());

        // T3: повтор без змін — нуль змін і нуль перерахунків.
        trigger.ClearReceivedCalls();
        var version = await RowVersionAsync(stand, "EF-E2");
        await RunAsync(stand, source, trigger: trigger);
        Assert.Empty(trigger.ReceivedCalls());
        Assert.Equal(version, await RowVersionAsync(stand, "EF-E2"));
        Assert.Single(await LinksAsync(stand));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task T2_T8_Нова_подія_додається_а_повернена_після_видалення_ідентифікатор_лягає_знову_без_конфлікту_ключа()
    {
        await using var stand = await ArrangeAsync();
        var e1 = Ev("E1", Start, End);
        var e2 = Ev("E2", Start.AddHours(1), End.AddHours(1));
        var source = new FakeEventSource(e2);
        await RunAsync(stand, source);

        source.Result = new SourceEventResult([e1, e2], false, null);
        await RunAsync(stand, source);
        Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));

        source.Result = new SourceEventResult([e2], false, null);
        await RunAsync(stand, source);
        Assert.Equal(["EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey));

        // Той самий ID повернувся: UQ_TableRow_Key не заважає, бо рядок видалено фізично.
        source.Result = new SourceEventResult([e1, e2], false, null);
        await RunAsync(stand, source);
        Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.Equal(2, (await LinksAsync(stand)).Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task T4_Джерело_віддало_нуль_подій_при_наявних_рядках_нічого_не_видаляється_і_пишеться_подія_покриття()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        var source = new FakeEventSource(Ev("E1", Start, End), Ev("E2", Start.AddHours(1), End.AddHours(1)));
        await RunAsync(stand, source, trigger: trigger);
        trigger.ClearReceivedCalls();

        source.Result = new SourceEventResult([], false, null);
        await RunAsync(stand, source, trigger: trigger);

        Assert.Equal(2, (await RowsAsync(stand)).Count);
        Assert.Equal(2, (await LinksAsync(stand)).Count);
        Assert.Empty(trigger.ReceivedCalls());
        Assert.Equal(
            1,
            Convert.ToInt32(
                await ScalarAsync(
                    $"SELECT COUNT(*) FROM itg.CollectionCoverage WHERE SourceEntityId = {stand.EntityId} AND Status = N'SourceDataRefused'"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task T5_Закритий_період_подію_не_видаляє_рядок_лишається_і_пишеться_подія_покриття()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        var february = new DateTime(2026, 2, 2, 9, 0, 0, DateTimeKind.Utc);
        var source = new FakeEventSource(Ev("E1", Start, End), Ev("F1", february, february.AddMinutes(5)));
        await RunAsync(stand, source, trigger: trigger);

        await using (var db = sql.CreateContext())
        {
            var period = await db.Periods.SingleAsync(p => p.ProjectId == stand.ProjectId && p.PeriodKeyValue == 202601);
            period.AdvanceTo(PeriodState.Closed, Now);
            await db.SaveChangesAsync();
        }

        trigger.ClearReceivedCalls();
        source.Result = new SourceEventResult([Ev("F1", february, february.AddMinutes(5))], false, null);
        await RunAsync(stand, source, trigger: trigger);

        Assert.Equal(["EF-E1", "EF-F1"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.Equal(SourceEventLinkStatus.Missing, (await LinksAsync(stand)).Single(l => l.SourceEventId == "E1").Status);
        Assert.Empty(trigger.ReceivedCalls());
        Assert.Equal(
            1,
            Convert.ToInt32(
                await ScalarAsync(
                    $"SELECT COUNT(*) FROM itg.CollectionCoverage WHERE SourceEntityId = {stand.EntityId} AND Status = N'SkippedPeriodClosed'"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    [Trait("Finding", "D-118")]
    public async Task T6_Рядок_із_правкою_людини_не_видаляється_навіть_коли_події_немає_в_джерелі()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, End, volume: 10m), Ev("E2", Start.AddHours(1), End.AddHours(1)));
        await RunAsync(stand, source);
        await HumanWriteAsync(stand, "EF-E1", new PatchCell(stand.VolumeCode, 42m));

        source.Result = new SourceEventResult([Ev("E2", Start.AddHours(1), End.AddHours(1))], false, null);
        await RunAsync(stand, source);

        Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.Equal(42m, (await CellsAsync(stand, "EF-E1"))[stand.VolumeColumn].Numeric);
        Assert.Equal(SourceEventLinkStatus.Missing, (await LinksAsync(stand)).Single(l => l.SourceEventId == "E1").Status);
        Assert.Equal(
            1,
            Convert.ToInt32(
                await ScalarAsync(
                    $"SELECT COUNT(*) FROM itg.CollectionCoverage WHERE SourceEntityId = {stand.EntityId} AND Status = N'ConflictKeptManual'"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task T7_Каскад_видалення_кількох_подій_одного_періоду_ставить_один_перерахунок()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        var source = new FakeEventSource(
            Ev("E1", Start, End), Ev("E2", Start.AddHours(1), End.AddHours(1)), Ev("E3", Start.AddHours(2), End.AddHours(2)));
        await RunAsync(stand, source, trigger: trigger);
        trigger.ClearReceivedCalls();

        source.Result = new SourceEventResult([Ev("E3", Start.AddHours(2), End.AddHours(2))], false, null);
        await RunAsync(stand, source, trigger: trigger);

        Assert.Equal(["EF-E3"], (await RowsAsync(stand)).Select(r => r.RowKey));
        await trigger.Received(1).RequestAsync(stand.DocumentId, new PeriodKey(202601), Arg.Any<CancellationToken>());
        Assert.Single(trigger.ReceivedCalls());
    }

    // ── Блокери рев'ю «Аудита» (Б1–Б4) і межі вікна ──
    //
    // ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати ADO-транзакцію в ApplyRemovalsAsync (autocommit) — червоніє Б2; прибрати запис
    // INSERT INTO aud.StructureChange — червоніє Б1; прибрати перевірку `exceeds` — червоніє Б3; прибрати блок
    // `sheetStatus` — червоніє Б4; прибрати `l.StartUtc >= from`/`< to` у планувальнику — червоніє Вікно.

    private async Task<int> JournalCountAsync(Stand stand, string? eventId = null)
        => Convert.ToInt32(
            await ScalarAsync(
                "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = N'SourceEventRowRemoved' "
                + $"AND OldJson LIKE N'%\"mapId\":{stand.MapId},%'"
                + (eventId is null ? string.Empty : $" AND OldJson LIKE N'%\"eventId\":\"{eventId}\"%'")),
            CultureInfo.InvariantCulture);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    [Trait("Finding", "audit-B1")]
    public async Task B1_Видалення_пише_склад_видаленого_в_журнал_із_ключем_значеннями_і_прогоном()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, End, volume: 10m), Ev("E2", Start.AddHours(1), End.AddHours(1)));
        await RunAsync(stand, source);

        source.Result = new SourceEventResult([Ev("E2", Start.AddHours(1), End.AddHours(1))], false, null);
        await RunAsync(stand, source);

        Assert.Equal(1, await JournalCountAsync(stand, "E1"));
        Assert.Equal(0, await JournalCountAsync(stand, "E2"));

        var json = (string)(await ScalarAsync(
            "SELECT TOP 1 OldJson FROM aud.StructureChange WHERE Operation = N'SourceEventRowRemoved' "
            + $"AND OldJson LIKE N'%\"mapId\":{stand.MapId},%' AND OldJson LIKE N'%\"eventId\":\"E1\"%'"))!;
        Assert.Contains("\"rowKey\":\"EF-E1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"periodKey\":202601", json, StringComparison.Ordinal);
        Assert.Contains($"\"tableInstanceId\":{stand.JanuaryInstanceId}", json, StringComparison.Ordinal);
        Assert.Contains("\"number\":10", json, StringComparison.Ordinal);
        Assert.Matches("\"runId\":\"[0-9a-f]{32}\"", json);
        Assert.Equal(
            1,
            Convert.ToInt32(
                await ScalarAsync(
                    "SELECT COUNT(*) FROM aud.StructureChange WHERE Operation = N'SourceEventRowRemoved' "
                    + $"AND OldJson LIKE N'%\"mapId\":{stand.MapId},%' AND ChangedByUserId = "
                    + "(SELECT Id FROM sec.[User] WHERE UserName = N'svc-integration') AND CorrelationId IS NOT NULL"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    [Trait("Finding", "audit-B2")]
    public async Task B2_Збій_на_видаленні_зв_язку_відкочує_комірки_рядок_і_запис_журналу_разом()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, End, volume: 10m), Ev("E2", Start.AddHours(1), End.AddHours(1)));
        await RunAsync(stand, source);

        var trigger = $"TR_EfsyncB2_{stand.MapId}";
        await ExecuteAsync(
            $"CREATE OR ALTER TRIGGER ext.{trigger} ON ext.SourceEventLink AFTER DELETE AS "
            + $"BEGIN IF EXISTS (SELECT 1 FROM deleted WHERE SourceEventMapId = {stand.MapId}) "
            + "THROW 50999, N'efsync B2: link delete refused', 1; END");
        try
        {
            source.Result = new SourceEventResult([Ev("E2", Start.AddHours(1), End.AddHours(1))], false, null);
            await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(stand, source));

            // Усе або нічого: рядок і комірки на місці, журнал порожній, зв'язок цілий.
            Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
            Assert.Equal(10m, (await CellsAsync(stand, "EF-E1"))[stand.VolumeColumn].Numeric);
            Assert.Equal(0, await JournalCountAsync(stand));
            Assert.Equal(2, (await LinksAsync(stand)).Count);
        }
        finally
        {
            await ExecuteAsync($"DROP TRIGGER IF EXISTS ext.{trigger}");
        }
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-EFSYNC")]
    [Trait("Finding", "audit-B3")]
    [InlineData(3, 4, false)] // 75 %, але ≤ 10 — дозволено (мінімум)
    [InlineData(10, 10, false)] // рівно мінімум
    [InlineData(11, 11, true)] // 11 із 11 > max(10, 2.2)
    [InlineData(11, 100, false)] // 11 % від 100 і > 10, але ≤ 20 % → дозволено
    [InlineData(21, 100, true)] // 21 % > 20 %
    [InlineData(20, 100, false)] // рівно 20 %
    [InlineData(201, 1500, true)] // ≤ 20 % (13 %), але > 200 абсолютно
    [InlineData(200, 1500, false)]
    public void B3_Ліміт_масового_видалення_відсоток_мінімум_і_абсолютна_стеля(int candidates, int linked, bool exceeds)
        => Assert.Equal(exceeds, SourceEventSyncJob.ExceedsRemovalLimit(candidates, linked, out _));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    [Trait("Finding", "audit-B3")]
    public async Task B3_Масове_зникнення_блокується_лімітом_а_після_ручного_підтвердження_видаляється()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        var logger = new CollectingLogger();

        // Перший синк: зв'язків ще немає — ліміт його не зачіпає (16 подій записано).
        var all = Enumerable.Range(1, 16)
            .Select(i => Ev($"M{i:00}", Start.AddMinutes(i * 20), Start.AddMinutes((i * 20) + 10)))
            .ToArray();
        var source = new FakeEventSource(all);
        await RunAsync(stand, source, trigger: trigger);
        Assert.Equal(16, (await LinksAsync(stand)).Count);
        Assert.Equal(16, (await RowsAsync(stand)).Count);

        // Лишилось 2 із 16: 14 кандидатів > max(10, 3.2).
        source.Result = new SourceEventResult([all[0], all[1]], false, null);
        trigger.ClearReceivedCalls();
        await RunAsync(stand, source, trigger: trigger, logger: logger);

        Assert.Equal(16, (await RowsAsync(stand)).Count);
        Assert.Equal(16, (await LinksAsync(stand)).Count);
        Assert.Equal(0, await JournalCountAsync(stand));
        Assert.Empty(trigger.ReceivedCalls());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("перевищує ліміт", StringComparison.Ordinal));
        Assert.Equal(
            1,
            Convert.ToInt32(
                await ScalarAsync(
                    $"SELECT COUNT(*) FROM itg.CollectionCoverage WHERE SourceEntityId = {stand.EntityId} "
                    + "AND Status = N'SourceDataRefused' AND Details LIKE N'%eventRemovalLimit%'"),
                CultureInfo.InvariantCulture));

        // Здоров'я джерел: блок видно як прогалину (Degraded у SourcesHealthCheck).
        await using (var scope = stand.Provider.CreateAsyncScope())
        {
            var status = (await scope.ServiceProvider.GetRequiredService<ICollectionStore>()
                .ListSourceEntitiesAsync(CancellationToken.None)).Single(e => e.Id == stand.EntityId);
            Assert.NotNull(status.OldestGap);
        }

        // Підтвердження вручну (Integration.Manage ставить прапор у payload) — видалення виконується.
        await RunAsync(stand, source, trigger: trigger, confirm: true);
        Assert.Equal(["EF-M01", "EF-M02"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.Equal(14, await JournalCountAsync(stand));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    [Trait("Finding", "audit-B4")]
    public async Task B4_Поданий_аркуш_рядок_не_видаляється_а_пишеться_подія_покриття_і_журнал_порожній()
    {
        await using var stand = await ArrangeAsync();
        var trigger = Substitute.For<ICalculationTrigger>();
        var source = new FakeEventSource(Ev("E1", Start, End), Ev("E2", Start.AddHours(1), End.AddHours(1)));
        await RunAsync(stand, source, trigger: trigger);

        var sheetDefId = Convert.ToInt32(
            await ScalarAsync($"SELECT SheetDefId FROM cfg.TableDef WHERE Id = {stand.TableDefId}"), CultureInfo.InvariantCulture);
        await using (var db = sql.CreateContext())
        {
            var state = new ApprovalState(stand.DocumentId, sheetDefId, 202601);
            state.Submit(stand.HumanId, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        trigger.ClearReceivedCalls();
        source.Result = new SourceEventResult([Ev("E2", Start.AddHours(1), End.AddHours(1))], false, null);
        await RunAsync(stand, source, trigger: trigger);

        Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.Equal(0, await JournalCountAsync(stand));
        Assert.Empty(trigger.ReceivedCalls());
        Assert.Equal(
            1,
            Convert.ToInt32(
                await ScalarAsync(
                    $"SELECT COUNT(*) FROM itg.CollectionCoverage WHERE SourceEntityId = {stand.EntityId} "
                    + "AND Status = N'SkippedPeriodClosed' AND Details LIKE N'%eventRemovalSheetSubmitted%'"),
                CultureInfo.InvariantCulture));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task Часткова_відмова_джерела_ErrorCode_нічого_не_видаляє_і_не_ставить_Missing()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, End), Ev("E2", Start.AddHours(1), End.AddHours(1)));
        await RunAsync(stand, source);

        source.Result = new SourceEventResult([Ev("E2", Start.AddHours(1), End.AddHours(1))], false, "ECR-INT-0503");
        await RunAsync(stand, source);

        Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.All(await LinksAsync(stand), l => Assert.NotEqual(SourceEventLinkStatus.Missing, l.Status));
        Assert.Equal(0, await JournalCountAsync(stand));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task Вікно_звірки_подія_раніше_початку_і_з_початком_рівно_у_кінці_вікна_не_видаляються()
    {
        await using var stand = await ArrangeAsync();
        var early = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);
        var atEnd = Now; // кінець вікна виключно
        var source = new FakeEventSource(
            Ev("OLD", early, early.AddMinutes(5)), Ev("E1", Start, End), Ev("LATE", atEnd, atEnd.AddMinutes(5)));
        await RunAsync(stand, source, from: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), to: Now.AddDays(1));
        Assert.Equal(3, (await LinksAsync(stand)).Count);

        // Вікно прогону [20.01; Now): OLD раніше початку, LATE починається рівно в кінці — обидві поза звіркою.
        source.Result = new SourceEventResult([Ev("E1", Start, End)], false, null);
        await RunAsync(stand, source);

        Assert.Equal(["EF-E1", "EF-LATE", "EF-OLD"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
        Assert.Equal(0, await JournalCountAsync(stand));
    }

    // ── Братні шаблони: Auto / Auto_Day / Manual / Manual_Day (рішення людини 2026-10-01) ──
    //
    // ⛔ МУТАЦІЙНІ ДОКАЗИ: прибрати фільтр Outranked в ExecuteAsync — червоніє S1; прибрати перевірку
    // sibling.Overlaps — S2; прибрати `read.Truncated || sibling.Incomplete` — S3; прибрати
    // `!other.Contains(...)` у gone планувальника — S4.

    private const string LowerPriority = "_Zz";
    private const string HigherPriority = "_Auto";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task S1_Одна_подія_в_двох_шаблонах_дає_один_рядок_і_один_зв_язок_у_шаблоні_з_вищим_пріоритетом()
    {
        await using var stand = await ArrangeAsync();
        var sibling = await AddSiblingAsync(stand, HigherPriority);
        try
        {
            var source = new FakeEventSource();
            source.ByTemplate[stand.EntityCode] = new SourceEventResult([Ev("E1", Start, End, volume: 10m)], false, null);
            source.ByTemplate[sibling.Code] = new SourceEventResult([Ev("E1", Start, End, volume: 10m)], false, null);

            await RunAsync(stand, source);
            Assert.Empty(await RowsAsync(stand));
            Assert.Empty(await LinksAsync(stand));

            await RunAsync(stand, source, entityId: sibling.EntityId);
            await RunAsync(stand, source);

            Assert.Equal(["EF-E1"], (await RowsAsync(stand)).Select(r => r.RowKey));
            Assert.Empty(await LinksAsync(stand));
            Assert.Single(await LinksOfMapAsync(sibling.MapId));
        }
        finally
        {
            await DeactivateAsync(sibling.EntityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task S2_Перекриття_з_різними_значеннями_дає_попередження_а_береться_перший_за_порядком_шаблонів()
    {
        await using var stand = await ArrangeAsync();
        var sibling = await AddSiblingAsync(stand, LowerPriority);
        try
        {
            var source = new FakeEventSource();
            source.ByTemplate[stand.EntityCode] = new SourceEventResult([Ev("E1", Start, End, volume: 10m)], false, null);
            source.ByTemplate[sibling.Code] = new SourceEventResult([Ev("E1", Start, End, volume: 20m)], false, null);

            await RunAsync(stand, source);

            Assert.Equal(10m, (await CellsAsync(stand, "EF-E1"))[stand.VolumeColumn].Numeric);
            Assert.Equal(
                1,
                Convert.ToInt32(
                    await ScalarAsync(
                        $"SELECT COUNT(*) FROM itg.CollectionCoverage WHERE SourceEntityId = {stand.EntityId} AND Details LIKE N'%eventTemplateOverlap%'"),
                    CultureInfo.InvariantCulture));
        }
        finally
        {
            await DeactivateAsync(sibling.EntityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task S3_Недоступний_братній_шаблон_пропускає_видалення_для_всього_періоду()
    {
        await using var stand = await ArrangeAsync();
        var sibling = await AddSiblingAsync(stand, LowerPriority);
        try
        {
            var source = new FakeEventSource();
            source.ByTemplate[stand.EntityCode] = new SourceEventResult(
                [Ev("E1", Start, End), Ev("E2", Start.AddHours(1), End.AddHours(1))], false, null);
            source.ByTemplate[sibling.Code] = new SourceEventResult([], false, null);
            await RunAsync(stand, source);

            source.ByTemplate[stand.EntityCode] = new SourceEventResult([Ev("E2", Start.AddHours(1), End.AddHours(1))], false, null);
            source.FailTemplates.Add(sibling.Code);
            await RunAsync(stand, source);

            Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
            Assert.Equal(2, (await LinksAsync(stand)).Count);

            // Шаблон знову доступний — тепер подія E1 справді зникла.
            source.FailTemplates.Clear();
            await RunAsync(stand, source);
            Assert.Equal(["EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey));
        }
        finally
        {
            await DeactivateAsync(sibling.EntityId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task S4_Подія_переїхала_в_братній_шаблон_зв_язок_знімається_а_рядок_лишається()
    {
        await using var stand = await ArrangeAsync();
        var sibling = await AddSiblingAsync(stand, LowerPriority);
        try
        {
            var source = new FakeEventSource();
            source.ByTemplate[stand.EntityCode] = new SourceEventResult([Ev("E1", Start, End), Ev("E2", Start.AddHours(1), End.AddHours(1))], false, null);
            source.ByTemplate[sibling.Code] = new SourceEventResult([], false, null);
            await RunAsync(stand, source);

            source.ByTemplate[stand.EntityCode] = new SourceEventResult([Ev("E2", Start.AddHours(1), End.AddHours(1))], false, null);
            source.ByTemplate[sibling.Code] = new SourceEventResult([Ev("E1", Start, End)], false, null);
            await RunAsync(stand, source);

            Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
            Assert.Equal(["E2"], (await LinksAsync(stand)).Select(l => l.SourceEventId));

            await RunAsync(stand, source, entityId: sibling.EntityId);
            Assert.Equal(["EF-E1", "EF-E2"], (await RowsAsync(stand)).Select(r => r.RowKey).Order(StringComparer.Ordinal));
            Assert.Equal(["E1"], (await LinksOfMapAsync(sibling.MapId)).Select(l => l.SourceEventId));
        }
        finally
        {
            await DeactivateAsync(sibling.EntityId);
        }
    }

    private sealed record Sibling(int EntityId, int MapId, string Code);

    private async Task<Sibling> AddSiblingAsync(Stand stand, string suffix)
    {
        await using var db = sql.CreateContext();
        var dataSourceId = await db.SourceEntities.AsNoTracking()
            .Where(e => e.Id == stand.EntityId).Select(e => e.DataSourceId).SingleAsync();
        var entity = new SourceEntity(dataSourceId, $"{stand.EntityCode}{suffix}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync();

        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == stand.TableDefId);
        var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == stand.TableDefId).ToDictionaryAsync(c => c.Id);
        var map = SourceEventMap.Create(
            entity.Id,
            stand.DocumentId,
            table,
            [
                new(columns[stand.StartColumn], SourceEventMap.StartAttribute),
                new(columns[stand.EndColumn], SourceEventMap.EndAttribute),
                new(columns[stand.NameColumn], SourceEventMap.NameAttribute),
                new(columns[stand.VolumeColumn], "Volume"),
            ],
            SourceEventVolumeMode.EventAttribute);
        db.SourceEventMaps.Add(map);
        await db.SaveChangesAsync();

        return new Sibling(entity.Id, map.Id, entity.Code);
    }

    private Task DeactivateAsync(int entityId)
        => ExecuteAsync($"UPDATE ext.SourceEntity SET IsActive = 0 WHERE Id = {entityId}");

    private async Task<List<SourceEventLink>> LinksOfMapAsync(int mapId)
    {
        await using var db = sql.CreateContext();
        return await db.SourceEventLinks.AsNoTracking().Where(l => l.SourceEventMapId == mapId).ToListAsync();
    }

    private sealed class CollectingLogger : ILogger<SourceEventSyncJob>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
    // ── Стенд ────────────────────────────────────────────────────────────────

    private async Task RunAsync(
        Stand stand, FakeEventSource source, bool freshContainer = false, ICalculationTrigger? trigger = null,
        ILogger<SourceEventSyncJob>? logger = null, int? entityId = null,
        DateTime? from = null, DateTime? to = null, bool confirm = false, bool clearTrackerOnWrite = false)
    {
        // ⚠ Метадані таблиці кешуються в контейнері: зміна стелі рядків у базі видна лише новому контейнеру.
        await using var fresh = freshContainer ? BuildProvider() : null;
        await using var scope = (fresh ?? stand.Provider).CreateAsyncScope();
        var services = scope.ServiceProvider;

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        var db = services.GetRequiredService<EcrDbContext>();
        var patcher = services.GetRequiredService<ICellPatcher>();
        var job = new SourceEventSyncJob(
            db,
            [source],
            clearTrackerOnWrite ? new TrackerClearingPatcher(patcher, db) : patcher,
            services.GetRequiredService<ICoverageJournal>(),
            services.GetRequiredService<IntegrationActor>(),
            clock,
            trigger,
            logger);

        await job.ExecuteAsync(
            new SourceEventSyncRequest(entityId ?? stand.EntityId, from ?? From, to ?? Now, confirm),
            Substitute.For<IJobProgress>(),
            CancellationToken.None);
    }

    private static SourceEvent Ev(
        string id,
        DateTime start,
        DateTime? end,
        string? parent = null,
        string? category = null,
        decimal? volume = null)
    {
        var attributes = new List<SourceEventAttribute>();
        if (category is not null)
        {
            attributes.Add(new SourceEventAttribute("Category", SourceEventAttributeScope.Event, null, category, null));
        }

        if (volume is not null)
        {
            attributes.Add(new SourceEventAttribute("Volume", SourceEventAttributeScope.Event, volume, null, "Sm3"));
        }

        return new SourceEvent(id, "FlareEvent", "Flaring HP", start, end, null, null, parent, attributes);
    }

    /// <summary>Джерело подій, яке віддає задану відповідь і пам'ятає останній запит.</summary>
    /// <summary>Справжній патчер, що після запису скидає трекер спільного контексту (як на гонці CELL-0409).</summary>
    private sealed class TrackerClearingPatcher(ICellPatcher inner, EcrDbContext db) : ICellPatcher
    {
        public Task<IntegrationWriteResult> ApplyIntegrationAsync(
            long documentId, long tableInstanceId, PeriodKey periodKey, IReadOnlyList<IntegrationCellValue> cells, CancellationToken ct)
            => inner.ApplyIntegrationAsync(documentId, tableInstanceId, periodKey, cells, ct);

        public async Task<IntegrationWriteResult> ApplyIntegrationRowsAsync(
            long documentId, long tableInstanceId, PeriodKey periodKey, IReadOnlyList<IntegrationRowUpsert> rows, CancellationToken ct)
        {
            var result = await inner.ApplyIntegrationRowsAsync(documentId, tableInstanceId, periodKey, rows, ct);
            db.ChangeTracker.Clear();
            return result;
        }
    }

    private sealed class FakeEventSource(params SourceEvent[] events) : IExternalDataSource
    {
        public ExternalTransport Transport => ExternalTransport.PiWebApi;

        public SourceEventResult Result { get; set; } = new(events, false, null);

        public SourceEventQuery? LastQuery { get; private set; }

        /// <summary>Відповіді за шаблоном; шаблону немає в словнику — <see cref="Result"/>.</summary>
        public Dictionary<string, SourceEventResult> ByTemplate { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Шаблони, читання яких падає (недоступне джерело).</summary>
        public HashSet<string> FailTemplates { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<SourceEventResult> ReadEventsAsync(SourceEventQuery query, CancellationToken ct)
        {
            LastQuery = query;
            if (FailTemplates.Contains(query.Template))
            {
                throw new InvalidOperationException($"template {query.Template} unavailable");
            }

            return Task.FromResult(ByTemplate.TryGetValue(query.Template, out var byTemplate) ? byTemplate : Result);
        }
    }

    private sealed record Stand(
        ServiceProvider Provider,
        Func<ValueTask> Cleanup,
        int ProjectId,
        int TableDefId,
        int EntityId,
        string EntityCode,
        int MapId,
        long DocumentId,
        long JanuaryInstanceId,
        long FebruaryInstanceId,
        int StartColumn,
        int EndColumn,
        int NameColumn,
        int VolumeColumn,
        string VolumeCode,
        int CategoryColumn,
        string CategoryCode,
        long V8EntryId,
        int RoleId,
        int HumanId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Cleanup();
    }

    private async Task<Stand> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(
            periodKey: 202601, columnCount: 3, rowCount: 0, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Січень відкрито; лютий — власний період з екземпляром (межа доби).
        var january = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == 202601);
        january.TransitionTo(PeriodState.Open, Now);

        var february = new Period(chain.ProjectId, new PeriodKey(202602), 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28));
        february.TransitionTo(PeriodState.Open, Now);
        db.Periods.Add(february);
        await db.SaveChangesAsync(CancellationToken.None);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var februaryInstanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(new PeriodKey(202602), februaryInstanceId, chain.DocumentId, chain.TableDefId, Now));

        var start = new ColumnDef(chain.TableDefId, EcrCode.Create($"START_{tag}"), Name("Start"), 10, CellDataType.Date);
        var end = new ColumnDef(chain.TableDefId, EcrCode.Create($"END_{tag}"), Name("End"), 11, CellDataType.Date);
        db.ColumnDefs.AddRange(start, end);

        var registry = new RegistryDef(EcrCode.Create($"A5B_{tag}"), Name("categories"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync(CancellationToken.None);

        var v8 = new RegistryEntry(registry.Id, EcrCode.Create("V8"), Name("V8"));
        db.RegistryEntries.Add(v8);

        var dataSource = new DataSource(
            EcrCode.Create($"A5BSRC{tag}"), Name("HSE301-A5b"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        var entity = new SourceEntity(dataSource.Id, $"FlareEvent{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        // Третя колонка будівника (Decimal) — категорія: Lookup на власний довідник.
        var ids = chain.ColumnDefIds;
        await ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET DataType = {(int)CellDataType.Lookup}, LookupRegistryDefId = {registry.Id} WHERE Id = {ids[2]}");

        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == chain.TableDefId);
        var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == chain.TableDefId).ToDictionaryAsync(c => c.Id);

        var map = SourceEventMap.Create(
            entity.Id,
            chain.DocumentId,
            table,
            [
                new(columns[start.Id], SourceEventMap.StartAttribute),
                new(columns[end.Id], SourceEventMap.EndAttribute),
                new(columns[ids[0]], SourceEventMap.NameAttribute),
                new(columns[ids[2]], "Category", SourceEventAttributeScope.Event, SourceEventValueKind.LookupByCode),
                new(columns[ids[1]], "Volume"),
            ],
            SourceEventVolumeMode.EventAttribute);
        db.SourceEventMaps.Add(map);
        await db.SaveChangesAsync(CancellationToken.None);

        var roleId = await CreateWriterRoleAsync(db, chain.ProjectId);
        var humanId = await AddHumanAsync(db, roleId);
        var provider = BuildProvider();

        return new Stand(
            provider,
            async () =>
            {
                // Активна сутність у спільній базі фарбувала б SourcesHealthCheck інших тестів.
                await ExecuteAsync($"UPDATE ext.SourceEntity SET IsActive = 0 WHERE Id = {entity.Id}");
                await RevokeAsync(roleId);
                await provider.DisposeAsync();
            },
            chain.ProjectId,
            chain.TableDefId,
            entity.Id,
            entity.Code,
            map.Id,
            chain.DocumentId,
            chain.TableInstanceId,
            februaryInstanceId,
            start.Id,
            end.Id,
            ids[0],
            ids[1],
            columns[ids[1]].Code,
            ids[2],
            columns[ids[2]].Code,
            v8.Id,
            roleId,
            humanId);
    }

    /// <summary>Контейнер як у проді: застосунок + інфраструктура + обгортка автора задачі.</summary>
    private ServiceProvider BuildProvider()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns(sql.ConnectionString);
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);

        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));

        // ⚠ Черга перерахунку — підробка: Quartz тримає планувальник у глобальному репозиторії процесу.
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider();
    }

    /// <summary>Правка людини через той самий обробник, у власному scope.</summary>
    private async Task HumanWriteAsync(Stand stand, string rowKey, PatchCell cell)
    {
        await using var scope = stand.Provider.CreateAsyncScope();
        using var author = scope.ServiceProvider.GetRequiredService<JobActorScope>()
            .Enter(new JobActor(stand.HumanId, "human", "en", [], Guid.NewGuid().ToString("N")));

        var rows = await scope.ServiceProvider.GetRequiredService<IRowStore>()
            .GetRowsAsync(stand.JanuaryInstanceId, new PeriodKey(202601), CancellationToken.None);
        var version = rows.Single(r => r.RowKey == rowKey).RowVersion;

        await scope.ServiceProvider.GetRequiredService<PatchCellsHandler>().HandleAsync(
            new PatchCellsRequest(
                stand.JanuaryInstanceId, 202601, CellChangeOrigins.UserEdit, [new PatchRow(rowKey, version, [cell])]),
            CancellationToken.None);
    }

    private static async Task<int> CreateWriterRoleAsync(EcrDbContext db, int projectId)
    {
        var role = new Role(
            EcrCode.Create($"A5B{Guid.NewGuid():N}"[..16]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "A5b human writer (test)" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync(CancellationToken.None);

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync(CancellationToken.None);

        return role.Id;
    }

    private static async Task<int> AddHumanAsync(EcrDbContext db, int roleId)
    {
        var human = new User($"human_{Guid.NewGuid():N}"[..20], "Test human", AuthProvider.Local);
        human.SetPassword("not-a-real-hash"); // CK_User_Provider: локальному — хеш.
        db.Users.Add(human);
        await db.SaveChangesAsync(CancellationToken.None);

        db.RoleAssignments.Add(new RoleAssignment(roleId, human.Id, principalSid: null));
        await db.SaveChangesAsync(CancellationToken.None);

        return human.Id;
    }

    private Task RevokeAsync(int roleId)
        => ExecuteAsync(
            $"DELETE FROM sec.ResourceGrant WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.RoleAssignment WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.Role WHERE Id = {roleId};");

    // ── Читання стану ────────────────────────────────────────────────────────

    private sealed record StoredRow(string RowKey, long TableInstanceId);

    private sealed record Stored(string? Text, decimal? Numeric, DateTime? Date, long? EntryId);

    /// <summary>Рядки подій (<c>EF-…</c>) у обох екземплярах таблиці.</summary>
    private async Task<List<StoredRow>> RowsAsync(Stand stand)
    {
        await using var db = sql.CreateContext();
        var instances = new[] { stand.JanuaryInstanceId, stand.FebruaryInstanceId };

        return (await db.TableRows.AsNoTracking()
                .Where(r => instances.Contains(r.TableInstanceId) && !r.IsDeleted)
                .Select(r => new { r.RowKeyValue, r.TableInstanceId })
                .ToListAsync())
            .Where(r => r.RowKeyValue.StartsWith(SourceEventPrefix, StringComparison.Ordinal))
            .Select(r => new StoredRow(r.RowKeyValue, r.TableInstanceId))
            .ToList();
    }

    private const string SourceEventPrefix = "EF-";

    private async Task<List<SourceEventLink>> LinksAsync(Stand stand)
    {
        await using var db = sql.CreateContext();
        return await db.SourceEventLinks.AsNoTracking().Where(l => l.SourceEventMapId == stand.MapId).ToListAsync();
    }

    private async Task<Dictionary<int, Stored>> CellsAsync(Stand stand, string rowKey)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT v.ColumnDefId, v.ValueString, v.ValueNumeric, v.ValueDate, v.ValueRegistryEntryId "
            + "FROM doc.CellValue v JOIN doc.TableRow r ON r.PeriodKey = v.PeriodKey AND r.Id = v.TableRowId "
            + "WHERE r.TableInstanceId IN (@january, @february) AND r.RowKey = @rowKey";
        command.Parameters.AddWithValue("@january", stand.JanuaryInstanceId);
        command.Parameters.AddWithValue("@february", stand.FebruaryInstanceId);
        command.Parameters.AddWithValue("@rowKey", rowKey);
        await using var reader = await command.ExecuteReaderAsync();

        var result = new Dictionary<int, Stored>();
        while (await reader.ReadAsync())
        {
            result[reader.GetInt32(0)] = new Stored(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                reader.IsDBNull(4) ? null : Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture));
        }

        return result;
    }

    private async Task<string> RowVersionAsync(Stand stand, string rowKey)
        => Convert.ToBase64String((byte[])(await ScalarAsync(
            "SELECT RowVersion FROM doc.TableRow "
            + $"WHERE TableInstanceId IN ({stand.JanuaryInstanceId}, {stand.FebruaryInstanceId}) AND RowKey = N'{rowKey}'"))!);

    private async Task<int> AuditCountAsync(Stand stand)
        => Convert.ToInt32(
            await ScalarAsync($"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {stand.DocumentId}"),
            CultureInfo.InvariantCulture);

    private async Task<object?> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary><c>Ecr.Api.Auth.CurrentUser</c> поза HTTP-запитом: анонімний.</summary>
    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
