// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// <c>DAT-06</c>: подання і затвердження — стан і побічні записи ОДНИМ комітом.
/// </summary>
/// <remarks>
/// ⛔ Обидва обробники писали в базу кількома комітами. У <c>Submit</c> їх було
/// три: <c>WorkflowStore.SaveSnapshotAsync</c> кличе <c>SaveChangesAsync</c>
/// сам (йому потрібен <c>IDENTITY</c> зрізу), потім
/// <c>ReportSnapshotSync.MarkSubmittedAsync</c>, і аж наприкінці
/// <c>uow.SaveChangesAsync</c>. У <c>Approve</c> транзакції не було зовсім, а
/// <c>AuditWriter</c> поза транзакцією комітить свій <c>INSERT</c> одразу
/// (<c>AuditWriter.CreateCommand</c>).
///
/// ⚠ Доказ у кожному тесті — ЗБІЙ у середині, не успішний прогін: успіх
/// виглядає однаково і з транзакцією, і без неї. Кожному тесту на збій
/// відповідає контрольний тест на успіх — інакше «у базі нічого» було б
/// зеленим і на системі, яка не пише нічого ніколи.
/// </remarks>
[Collection("SqlServer")]
public sealed partial class WorkflowTransactionTests(SqlServerFixture sql)
{
    private const int PeriodKeyValue = 202601;
    private const int UserId = 9;
    private const int RoleId = 77;
    private static readonly DateTime Now = new(2026, 4, 1, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>Текст, за яким тести впізнають саме внесений збій.</summary>
    private const string Marker = "збій одразу після побічного запису";

    // ───────────────────────────── Submit ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Збій_після_збереження_зрізу_не_лишає_зрізу_в_базі()
    {
        // ⛔ `SaveSnapshotAsync` комітив зріз сам, тож падіння НАСТУПНОГО кроку
        // лишало в `calc.SubmissionSnapshot` зріз подання, якого не було: аркуш
        // у `wf.ApprovalState` — чернетка, а «доказ того, що пішло
        // регуляторові» — вже є. Розбіжність тиха: обидві таблиці окремо
        // виглядають справними.
        //
        // ⚠ Збій вноситься в `access.CurrentApprovalStepAsync` — перший виклик
        // ПІСЛЯ збереження зрізу (`SubmitSheetHandler:221`). Тобто відтворює
        // рівно те вікно, у якому дефект і жив.
        var world = await ArrangeAsync().ConfigureAwait(true);

        await using var db = CreateContext();
        var access = Access();
        access.CurrentApprovalStepAsync(
                  world.DocumentId, world.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns<Task<ApprovalStepView?>>(_ => throw new InvalidOperationException(Marker));

        var handler = Submit(world, db, access);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None))
            .ConfigureAwait(true);

        Assert.Equal(Marker, error.Message);

        // ⛔ Це твердження падає, якщо прибрати `ExecuteInTransactionAsync` із
        // `SubmitSheetHandler`: зріз лишається закоміченим власним
        // `SaveChangesAsync` усередині сховища.
        Assert.Equal(0, await SnapshotsAsync(world.DocumentId).ConfigureAwait(true));

        // ⚠ І аркуш не поданий — інакше «зрізу немає» означало б лише те, що до
        // зрізу не дійшло.
        Assert.NotEqual(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Успішне_подання_лишає_рівно_один_зріз_і_поданий_аркуш()
    {
        // Контроль до тесту вище.
        var world = await ArrangeAsync().ConfigureAwait(true);

        await using var db = CreateContext();
        await Submit(world, db, Access())
            .HandleAsync(world.DocumentId, world.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(1, await SnapshotsAsync(world.DocumentId).ConfigureAwait(true));
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
    }

    // ──────────────────────────── Approve ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Збій_після_запису_проміжного_підпису_не_лишає_його_в_журналі()
    {
        // ⛔ `ApproveSheetHandler` не відкривав транзакції зовсім, а
        // `IAuditWriter` пише сирим `INSERT` і поза транзакцією комітить його
        // одразу. Тобто `ApprovalStepPassed` лягав у `aud.SecurityEvent` ще до
        // `SaveChangesAsync`, і збій нижче лишав у журналі підпис під кроком,
        // якого не сталося. Проміжні кроки заводять саме заради
        // відповідальності — журнал, що розходиться зі станом, її підробляє.
        //
        // ⚠ Збій вноситься в `ReportSnapshotSync` (через його `IReportSnapshotBuilder`)
        // — перший виклик ПІСЛЯ запису в аудит (`ApproveSheetHandler:108`).
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);

        await using var db = CreateContext();
        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<Task<IReadOnlyList<ReportSnapshotSummary>>>(
                     _ => throw new InvalidOperationException(Marker));

        var handler = Approve(world, db, snapshots);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(
                world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: true, reason: null,
                CancellationToken.None))
            .ConfigureAwait(true);

        Assert.Equal(Marker, error.Message);

        // ⛔ Це твердження падає, якщо прибрати `ExecuteInTransactionAsync` із
        // `ApproveSheetHandler`.
        Assert.Equal(0, await SecurityEventsAsync(world.DocumentId).ConfigureAwait(true));

        // ⚠ І стан аркуша не зрушив: підпис і стан або разом, або ніяк.
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.17")]
    public async Task Успішне_затвердження_лишає_рівно_один_проміжний_підпис()
    {
        // Контроль до тесту вище.
        var world = await ArrangeAsync(submitted: true).ConfigureAwait(true);

        await using var db = CreateContext();
        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);

        await Approve(world, db, snapshots)
            .HandleAsync(
                world.DocumentId, world.SheetDefId, PeriodKeyValue, approved: true, reason: null,
                CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(1, await SecurityEventsAsync(world.DocumentId).ConfigureAwait(true));
    }

    // ────────────────────────────── збірка ────────────────────────────

    private SubmitSheetHandler Submit(World world, EcrDbContext db, IAccessDecisionService access)
    {
        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(new List<CellRecord>());

        var rows = Substitute.For<IRowStore>();
        rows.GetOrphanedRowIdsAsync(world.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<long>>([]);
        rows.GetTableInstancesAsync(world.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TableInstanceRef>>(
                [new TableInstanceRef(
                    TableInstanceId: 1, DocumentId: world.DocumentId, TableDefId: 1,
                    TemplateVersionId: world.TemplateVersionId, PeriodKey: PeriodKeyValue)]);
        rows.GetRowIdsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long>());

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(world.DocumentId, world.SheetDefId, Arg.Any<CancellationToken>())
                 .Returns(true);
        documents.FindProjectIdAsync(world.DocumentId, Arg.Any<CancellationToken>())
                 .Returns(world.ProjectId);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(world.TemplateVersionId, Arg.Any<CancellationToken>())
                .Returns(new TemplateVersionSnapshot(
                    world.TemplateVersionId, PresentationRevision: 1, Sheets: [],
                    ColumnsById: new Dictionary<int, ColumnDef>(),
                    RowsByKey: new Dictionary<(int, string), RowDef>()));

        // ⚠ Зрізів звітності у цього проєкту немає — `ReportSnapshotSync` тоді
        // законно не робить нічого. Предмет тесту — межа коміту, а не побудова
        // зрізів, і зайвий рухомий шматок лише зробив би падіння двозначним.
        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new SubmitSheetHandler(
            cells, rows, new WorkflowStore(db), documents, metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            headers,
            new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), User(), new TestClock(Now), new SheetEditGate(db), NSubstitute.Substitute.For<Ecr.Application.Recalculation.ISubmitRecalculation>());
    }

    private ApproveSheetHandler Approve(World world, EcrDbContext db, IReportSnapshotBuilder snapshots)
    {
        var documents = Substitute.For<IDocumentStore>();
        documents.FindProjectIdAsync(world.DocumentId, Arg.Any<CancellationToken>())
                 .Returns(world.ProjectId);

        return new ApproveSheetHandler(
            new WorkflowStore(db), Access(), new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), User(), new TestClock(Now), new AuditWriter(db));
    }

    /// <summary>Дозволяє все і повертає маршрут із ДВОХ кроків.</summary>
    /// <remarks>
    /// ⚠ Саме два: аудит проміжного підпису пишеться лише тоді, коли крок НЕ
    /// останній (<c>step is { NextStepId: not null }</c>). З одноетапним
    /// маршрутом запису в журнал не було б узагалі — і тест на його відкат
    /// лишався б зеленим ні про що.
    /// </remarks>
    private static IAccessDecisionService Access()
    {
        var access = Substitute.For<IAccessDecisionService>();

        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder { UserId = UserId }.Build());
        access.CanSubmitAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanApproveAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CurrentApprovalStepAsync(
                  Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(new ApprovalStepView(StepId: 1, Ordinal: 1, RoleId: RoleId, NextStepId: 2, TotalSteps: 2));

        return access;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        return user;
    }

    // ────────────────────────────── перевірки ─────────────────────────

    private async Task<int> SnapshotsAsync(long documentId)
    {
        await using var db = CreateContext();
        return await db.SubmissionSnapshots.AsNoTracking()
                       .CountAsync(s => s.DocumentId == documentId).ConfigureAwait(false);
    }

    private async Task<DocumentStatus> StatusAsync(World world)
    {
        await using var db = CreateContext();
        var state = await db.ApprovalStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.DocumentId == world.DocumentId
                                      && s.SheetDefId == world.SheetDefId
                                      && s.PeriodKey == PeriodKeyValue)
            .ConfigureAwait(false);

        return state?.Status ?? DocumentStatus.Draft;
    }

    /// <summary>Скільки проміжних підписів по цьому документу лежить у журналі.</summary>
    private async Task<int> SecurityEventsAsync(long documentId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.SecurityEvent "
            + "WHERE EventType = N'ApprovalStepPassed' AND DetailsJson LIKE @pattern;";

        // ⚠ Документ у кожного прогону свій (IDENTITY), тож шаблон адресує саме
        // цей тест і не бачить сусідніх: база одна на всю збірку.
        command.Parameters.AddWithValue(
            "@pattern",
            $"%\"documentId\":{documentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}%");

        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    // ────────────────────────────── підготовка ────────────────────────

    private async Task<World> ArrangeAsync(bool submitted = false)
    {
        await using var db = CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10];

        var template = new Template(
            EcrCode.Create($"T{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Workflow tx" }),
            createdByUserId: 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new SheetDef(
            version.Id, EcrCode.Create($"S{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        db.SheetDefs.Add(sheet);

        var project = new Project(
            EcrCode.Create($"P{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Workflow tx" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        db.Projects.Add(project);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var period = new Period(
            project.Id, new PeriodKey(PeriodKeyValue), 1,
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        period.RecomputeBoundaries(ProjectBuilder.Policy(), ProjectBuilder.Zone());
        period.AdvanceTo(PeriodState.Open, Now);
        db.Periods.Add(period);

        var document = new Document(project.Id, $"DOC-{Guid.NewGuid():N}"[..20], 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (submitted)
        {
            var state = new ApprovalState(document.Id, sheet.Id, PeriodKeyValue);
            state.Submit(UserId, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new World(project.Id, version.Id, document.Id, sheet.Id);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(30))
            .Options);

    private sealed record World(int ProjectId, int TemplateVersionId, long DocumentId, int SheetDefId);
}
