// tests/Ecr.Infrastructure.Tests/Security/AccessDecisionYearScopedTableTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
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

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// b14 document-move P2 (H4, рішення людини 25.11, D-579): таблиця «екземпляр на період» версії «рік + місячні колонки»
/// (HSE401, таблиця 775) у МІСЯЧНОМУ проєкті — одна копія на рік у якорі (202601). Закритий січень LANDUI2026 її не
/// замикає: шаблон із «вікном року» правиться весь рік проєкту + зміщення (до архівації), без вікна — за річним
/// агрегатом місяців (як немісячний ввід MonthsInColumns).
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально, 26.11): (1) роутер без гілки PerPeriodInstance (поведінка бази 4980a1837) — червоні тести, що чекають
/// дозволу при закритому січні (зокрема «Вікно_року…», «Прапорець_вікна…»); (2) у <c>AccessDecisionService</c> ігнорувати
/// <c>YearWindowState</c> (завжди агрегат місяців) — червоні «Вікно_року…», «Після_року_й_зміщення…», «Пізня_правка…»;
/// контролі («Без_вікна…», «Звичайна_версія…») зелені.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "b14-document-move-P2")]
public sealed class AccessDecisionYearScopedTableTests(SqlServerFixture sql) : IDisposable
{
    private const int Jan = 202601;
    private const int Nov = 202611;
    private const int UserId = 1;

    private static readonly DateTime InYear = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime YearGrace = new(2027, 1, 10, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterWindow = new(2027, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    [Fact]
    public async Task Вікно_року_дозволяє_правку_якоря_при_закритому_січні()
    {
        var w = await ArrangeAsync(window: true, novState: PeriodState.Closed, decState: PeriodState.Closed);

        var decision = await DecideAsync(w, InYear);

        Assert.True(decision.IsAllowed, decision.Reason.ToString());
        Assert.Contains(Jan, decision.AnnualPeriods!);
    }

    [Fact]
    public async Task Після_року_й_зміщення_правка_відхиляється_навіть_з_відкритим_місяцем()
    {
        // Вікно — від меж року проєкту, не від станів місяців: листопад, лишений Open, правки не відкриває.
        var w = await ArrangeAsync(window: true, novState: PeriodState.Open);

        var decision = await DecideAsync(w, AfterWindow);

        Assert.Equal(EditDenyReason.PeriodClosed, decision.Reason);
    }

    [Fact]
    public async Task Пізня_правка_в_річному_пільговому_вікні_дозволена()
    {
        var w = await ArrangeAsync(window: true, novState: PeriodState.Closed, decState: PeriodState.Closed);

        var decision = await DecideAsync(w, YearGrace);

        Assert.True(decision.IsAllowed, decision.Reason.ToString());
    }

    [Fact]
    public async Task Без_вікна_року_правка_за_агрегатом_місяців()
    {
        var open = await ArrangeAsync(window: false, novState: PeriodState.Open);
        Assert.True((await DecideAsync(open, InYear)).IsAllowed);

        var closed = await ArrangeAsync(window: false, novState: PeriodState.Closed, decState: PeriodState.Closed);
        Assert.Equal(EditDenyReason.PeriodClosed, (await DecideAsync(closed, InYear)).Reason);
    }

    [Fact]
    public async Task Звичайна_версія_без_річної_розкладки_як_доти()
    {
        // Реєстр розкладок не знає аркуша: таблиця «екземпляр на період» не річна — закритий січень замикає.
        var w = await ArrangeAsync(window: true, novState: PeriodState.Open, yearLayout: false);

        Assert.Equal(EditDenyReason.PeriodClosed, (await DecideAsync(w, InYear)).Reason);
    }

    [Fact]
    public async Task Прапорець_вікна_року_пишеться_карткою_шаблону_і_не_змінює_правила_одного_документа()
    {
        var w = await ArrangeAsync(window: false, novState: PeriodState.Closed);

        await using (var db = w.Builder.CreateContext())
        {
            var card = await PolicyHandler(db).HandleAsync(w.Doc.TemplateId, single: true, yearEditWindow: true, CancellationToken.None);
            Assert.True(card.YearEditWindow);
            Assert.True(card.SingleDocumentPerProject);
        }

        Assert.True((await DecideAsync(w, InYear)).IsAllowed);

        await using (var db = w.Builder.CreateContext())
        {
            // null — без змін (клієнт до b14 шле лише singleDocumentPerProject).
            var card = await PolicyHandler(db).HandleAsync(w.Doc.TemplateId, false, CancellationToken.None);
            Assert.True(card.YearEditWindow);
            Assert.False(card.SingleDocumentPerProject);
        }
    }

    // ─────────────────────────────── допоміжне ───────────────────────────────

    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc, bool YearLayout);

    private async Task<World> ArrangeAsync(
        bool window, PeriodState novState, PeriodState decState = PeriodState.Scheduled, bool yearLayout = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(Jan, columnCount: 2, rowCount: 1, layoutKind: TableLayoutKind.PerPeriodInstance);

        await using var db = builder.CreateContext();
        for (var m = 2; m <= 12; m++)
        {
            db.Periods.Add(new Period(
                doc.ProjectId, new PeriodKey((2026 * 100) + m), (byte)m,
                new DateOnly(2026, m, 1), new DateOnly(2026, m, DateTime.DaysInMonth(2026, m))));
        }

        await db.SaveChangesAsync();

        // LANDUI2026 (§0 дизайну): Jan–Oct закриті, листопад і грудень — за сценарієм (на live: Open і Scheduled).
        foreach (var period in await db.Periods.Where(p => p.ProjectId == doc.ProjectId).ToListAsync())
        {
            var month = period.PeriodKeyValue % 100;
            var target = month == 12 ? decState : month == 11 ? novState : PeriodState.Closed;
            if (target != PeriodState.Scheduled)
            {
                period.AdvanceTo(PeriodState.Open, InYear);
            }

            if (target == PeriodState.Closed)
            {
                period.AdvanceTo(PeriodState.Grace, InYear);
                period.AdvanceTo(PeriodState.Closed, InYear);
            }
        }

        var project = await db.Projects.SingleAsync(p => p.Id == doc.ProjectId);
        project.Activate(InYear);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cfg.TemplateVersion SET Layout = {(byte)TemplateLayout.YearWithMonthColumns}, WorkflowMode = {(byte)WorkflowMode.None} WHERE Id = {doc.TemplateVersionId}");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE cfg.Template SET YearEditWindow = {window} WHERE Id = {doc.TemplateId}");

        return new World(builder, doc, yearLayout);
    }

    private async Task<EditDecision> DecideAsync(World w, DateTime now)
    {
        await using var db = w.Builder.CreateContext();
        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(true);
        registry.IsYearVersion(w.Doc.TemplateVersionId).Returns(w.YearLayout);
        registry.IsYearSheet(w.Doc.SheetDefId).Returns(w.YearLayout);
        var router = new MonthColumnRouter(
            db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(), registry);

        var service = new AccessDecisionService(
            db, new MetadataCache(_memory, db), new AccessProfileCache(_memory), new TestClock(now),
            Substitute.For<ICurrentUser>(), new WorkflowStore(db), router);
        var profile = new AccessBuilder { UserId = UserId }.Grant(ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Write).Build();
        var cell = new CellAddress(w.Doc.PeriodKey, w.Doc.RowIds[0], w.Doc.ColumnDefIds[1]);

        var decisions = await service.CanEditCellsAsync(profile, w.Doc.TableInstanceId, w.Doc.PeriodKey, [cell], CancellationToken.None);
        return decisions[cell];
    }

    private static SetTemplateDocumentPolicyHandler PolicyHandler(EcrDbContext db)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId }.Permission(SetTemplateDocumentPolicyHandler.Permission).Build());

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(InYear);

        return new SetTemplateDocumentPolicyHandler(
            new TemplateVersionStore(db), access, new UnitOfWork(db), Substitute.For<IAuditWriter>(), user, clock);
    }
}
