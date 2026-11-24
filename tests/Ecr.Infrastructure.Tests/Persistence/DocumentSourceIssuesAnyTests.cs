// tests/Ecr.Infrastructure.Tests/Persistence/DocumentSourceIssuesAnyTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ F5-R (D-570, рішення людини P-1122-4): «джерело змінено після затвердження» і за зв'язки в стані <c>Any</c> — затверджений
/// місяць споживача, чиї дані місяця джерела (таблиці зв'язки) змінено після затвердження. Журнал змін пише справжній
/// <see cref="AuditWriter"/>; читання — <see cref="DocumentSourceMonthGate"/> (панель) і
/// <see cref="DocumentSourceMonthGate.ListSourceChangedPeriodsAsync"/> (перелік документів).
/// </summary>
/// <remarks>
/// ⛔ ДОКАЗ (локально, не в коміті): у <c>DocumentSourceIssues.ListAsync</c> гілку Any → <c>(null, null)</c> — червоні
/// «змінено після», «гонитва в межах запасу» й «перелік»; <c>ApprovalRaceMargin</c> → 0 — червона «гонитва»; фільтр колонок
/// зв'язки прибрано — червона «чужа таблиця»; фільтр <c>CalcError</c> прибрано — червона «позначка помилки».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B80")]
[Trait("Decision", "D-570")]
public sealed class DocumentSourceIssuesAnyTests(SqlServerFixture sql)
{
    private const int March = SourceIssueWorld.March;
    private static readonly DateTime Approved = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Змінено_після_затвердження_названо_з_моментом_зміни_і_без_затвердження_джерела()
    {
        var w = await ArrangeAsync();
        await ApproveLandAsync(w);
        Assert.Empty(await ListAsync(w.World.Land.DocumentId));

        await ChangeAsync(w.World.Source.DocumentId, w.BoundColumnId, Approved.AddHours(2));

        var issue = Assert.Single(await ListAsync(w.World.Land.DocumentId));
        Assert.Equal(ISourceIssueReader.SourceChangedAfterApproval, issue.Kind);
        Assert.Equal((March, 3, March), (issue.PeriodKey, issue.MonthNumber, issue.SourcePeriodKey));
        Assert.Equal(Approved.AddHours(2), issue.SourceChangedAt);
        Assert.Null(issue.SourceApprovedAt);
        Assert.Equal(Approved, issue.ApprovedAt);
        Assert.Equal(w.World.Source.DocumentId, issue.SourceDocumentId);

        var finding = Assert.Single(await FindingsAsync(w.World.Land.DocumentId));
        Assert.Equal(DocumentSourceIssues.ReasonChanged, finding.Reason);
    }

    /// <summary>
    /// Гонитва D-567 (6б): правка, закомічена між воротами й комітом затвердження, має мітку до <c>ApprovedAt</c> — у межах
    /// запасу її названо; правка задовго до затвердження (її порахував каскад) — ні.
    /// </summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(-60, false)]
    public async Task Правка_перед_затвердженням_у_межах_запасу_названа_а_давня_ні(int minutes, bool expected)
    {
        var w = await ArrangeAsync();
        await ApproveLandAsync(w);
        await ChangeAsync(w.World.Source.DocumentId, w.BoundColumnId, Approved.AddMinutes(minutes));

        Assert.Equal(expected, (await ListAsync(w.World.Land.DocumentId)).Count == 1);
    }

    /// <summary>
    /// ✎ W15z M7 (репетиція b11 @dc1b38cba; огляд b11 №3): затверджено → правка джерела (T, <c>aud.CellChange.Id</c> = E) →
    /// повернути → F9 → подати й затвердити через ~65 с після T. Позначку знято, лише коли межа входів чинного прогону споживача
    /// (<c>InputsCellChangeWatermark</c>) ≥ E — годинники (StartedAt воркера проти ChangedAt Api) не рахуються.
    /// </summary>
    /// <remarks>
    /// Рядки: (1) M7 — межа покриває правку → знято. (2) (d) годинник воркера ВІДСТАЄ (StartedAt раніше ChangedAt), межа покриває
    /// → знято. (3) (a)/(c)/(d) за часом «прогін після правки», а межа — ні: прогін переніс затверджені результати, правка була в
    /// польоті на старті прогону (мітка — початок транзакції), годинник воркера поспішає → лишається. (4) (b) перенесення з
    /// <c>InputsAsOfUtc</c> до правки → лишається. (5) прогін до міграції (межі немає) → лишається. (6) контроль: повторне
    /// затвердження БЕЗ перерахунку (чинний прогін до правки) → лишається (гонитва D-567 (6б)).
    /// Мутація: <c>@covered</c> → <c>long.MaxValue</c> — червоні (3)–(6); межу не читати (<c>0</c>) — червоні (1), (2).
    /// </remarks>
    [Theory]
    [InlineData(8, 0, true, false, false)]
    [InlineData(-30, 0, true, false, false)]
    [InlineData(8, -1, true, false, true)]
    [InlineData(8, -1, true, true, true)]
    [InlineData(8, 0, false, false, true)]
    [InlineData(-30, -1, true, false, true)]
    public async Task Повторне_затвердження_знімає_позначку_лише_за_межею_входів_прогону(
        int runStartSeconds, int watermarkDelta, bool hasWatermark, bool carried, bool expected)
    {
        var w = await ArrangeAsync();
        await ApproveLandAsync(w);
        var change = Approved.AddHours(1);
        await ChangeAsync(w.World.Source.DocumentId, w.BoundColumnId, change);
        Assert.Single(await ListAsync(w.World.Land.DocumentId));

        // F9: чинний прогін споживача за період, далі повторне затвердження через 65 с після правки.
        await using (var db = sql.CreateContext())
        {
            var edit = (await db.Database
                .SqlQuery<long>($"SELECT MAX(Id) AS Value FROM aud.CellChange WHERE DocumentId = {w.World.Source.DocumentId}")
                .ToListAsync()).Single();
            var run = new Ecr.Domain.Entities.Calculations.CalculationRun(
                w.World.Land.ProjectId, March, 5, change.AddSeconds(runStartSeconds), w.World.Land.DocumentId);
            if (carried)
            {
                run.LimitInputsAsOf(change.AddHours(-1));
            }

            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();
            run.Complete(Ecr.Domain.Entities.Calculations.CalculationRun.CurrentStatus, change.AddSeconds(53), null, null);
            await db.SaveChangesAsync();
            if (hasWatermark)
            {
                var watermark = edit + watermarkDelta;
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE calc.CalculationRun SET InputsCellChangeWatermark = {watermark} WHERE Id = {run.Id}");
            }

            await db.ApprovalStates.Where(s => s.DocumentId == w.World.Land.DocumentId && s.PeriodKey == March)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ApprovedAt, change.AddSeconds(65)));
        }

        Assert.Equal(expected, (await ListAsync(w.World.Land.DocumentId)).Count == 1);

        await using var check = sql.CreateContext();
        var gate = new DocumentSourceMonthGate(check, readOverride: null, DependencyGraphTestKit.Provider(check));
        var changed = await gate.ListSourceChangedPeriodsAsync(
            new Dictionary<long, IReadOnlyList<int>> { [w.World.Land.DocumentId] = [March] }, CancellationToken.None);
        Assert.Equal(expected, changed.ContainsKey(w.World.Land.DocumentId));
    }
    [Fact]
    public async Task Правка_чужої_таблиці_джерела_і_позначка_помилки_формули_не_зміна()
    {
        var w = await ArrangeAsync();
        await ApproveLandAsync(w);

        // Таблиця документа-джерела поза зв'язкою (власна таблиця будівника) — не склад.
        await ChangeAsync(w.World.Source.DocumentId, w.World.Source.ColumnDefIds[0], Approved.AddHours(1));
        // Позначка помилки формули в таблиці зв'язки — не зміна чисел.
        await ChangeAsync(w.World.Source.DocumentId, w.BoundColumnId, Approved.AddHours(1), origin: "CalcError");

        Assert.Empty(await ListAsync(w.World.Land.DocumentId));
    }

    [Fact]
    public async Task Не_затверджений_місяць_споживача_за_Any_проблем_не_має()
    {
        var w = await ArrangeAsync();
        await SourceIssueWorld.AddStateAsync(sql, w.World.Land.DocumentId, w.World.Land.SheetDefId, March, Approved);
        await ChangeAsync(w.World.Source.DocumentId, w.BoundColumnId, Approved.AddHours(1));

        Assert.Empty(await ListAsync(w.World.Land.DocumentId));
    }

    [Fact]
    public async Task Читання_з_документа_вимкнено_бітом_зв_язки_проблем_немає()
    {
        var w = await ArrangeAsync();
        await ApproveLandAsync(w);
        await ChangeAsync(w.World.Source.DocumentId, w.BoundColumnId, Approved.AddHours(1));
        Assert.Single(await ListAsync(w.World.Land.DocumentId));

        await using (var db = sql.CreateContext())
        {
            await db.DocumentSourceBindings.Where(b => b.Id == w.World.BindingId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.ReadFromDocument, false));
        }

        Assert.Empty(await ListAsync(w.World.Land.DocumentId));
    }

    [Fact]
    public async Task Перелік_документів_називає_затверджений_період_зі_зміненим_джерелом()
    {
        var w = await ArrangeAsync();
        await ApproveLandAsync(w);
        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: March);

        await using var db = sql.CreateContext();
        var gate = new DocumentSourceMonthGate(db, readOverride: null, DependencyGraphTestKit.Provider(db));
        var request = new Dictionary<long, IReadOnlyList<int>> { [w.World.Land.DocumentId] = [March], [other.DocumentId] = [March] };
        Assert.Empty(await gate.ListSourceChangedPeriodsAsync(request, CancellationToken.None));

        await ChangeAsync(w.World.Source.DocumentId, w.BoundColumnId, Approved.AddHours(3));

        var changed = await gate.ListSourceChangedPeriodsAsync(request, CancellationToken.None);
        Assert.Equal([March], changed[w.World.Land.DocumentId]);
        Assert.False(changed.ContainsKey(other.DocumentId));
    }

    /// <summary>
    /// Нічна перевірка STALE_APPROVED_DEPENDENT лишається правилом ApprovedOnly: рядок Any туди не йде — і тоді, коли
    /// документ ще й споживач зв'язки ApprovedOnly (кандидат перевірки), чий місяць джерела затверджено до нього.
    /// </summary>
    [Fact]
    public async Task Нічна_перевірка_не_бере_рядка_Any()
    {
        var w = await ArrangeAsync();
        var approvedOnly = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);
        await SourceIssueWorld.AddStateAsync(
            sql, approvedOnly.Source.DocumentId, approvedOnly.Source.SheetDefId, March, Approved.AddHours(-3), Approved.AddHours(-2));
        await ApproveLandAsync(w);
        await ChangeAsync(w.World.Source.DocumentId, w.BoundColumnId, Approved.AddHours(1));

        await using var db = sql.CreateContext();

        // Шаблон Land — ще й споживач зв'язки ApprovedOnly (опубліковану версію формулою не доповнити — ребро графа додано
        // до справжнього знімка): документ стає кандидатом нічної перевірки.
        var real = await DependencyGraphTestKit.Provider(db).GetAsync(CancellationToken.None);
        var registryId = await db.DocumentSourceBindings.Where(b => b.Id == approvedOnly.BindingId).Select(b => b.RegistryDefId).SingleAsync();
        var graph = NSubstitute.Substitute.For<Ecr.Application.Ports.IDependencyGraphProvider>();
        graph.GetAsync(Arg.Any<CancellationToken>()).Returns(real with
        {
            TemplateDependencies =
            [
                .. real.TemplateDependencies,
                new Ecr.Application.DocumentDependencies.TemplateDependency(
                    approvedOnly.Source.TemplateId, w.World.Land.TemplateId, approvedOnly.BindingId, registryId,
                    new Ecr.Expressions.Binding.PeriodScope(Ecr.Expressions.Binding.PeriodScopeKind.Month, 0), true, false),
            ],
        });

        var findings = await new DocumentSourceIssues(db, readOverride: null, graph).ListAsync(w.World.Land.DocumentId, [March], CancellationToken.None);
        var only = Assert.Single(findings);
        Assert.Equal(DocumentSourceIssues.ReasonChanged, only.Reason);

        var stale = await new Ecr.Infrastructure.Jobs.StaleApprovedDependentCheck(db, readOverride: null, graph)
            .RunAsync(10_000, CancellationToken.None);
        Assert.DoesNotContain(stale, s => s.DocumentId == w.World.Land.DocumentId);
    }
    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record AnyWorld(SourceIssueWorld.World World, int BoundColumnId);

    private async Task<AnyWorld> ArrangeAsync()
    {
        var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.Any);
        await using var db = sql.CreateContext();
        var stream = await db.TableDefs.SingleAsync(t => t.SheetDefId == world.Source.SheetDefId && t.Code == "FG_Internal");
        var column = new ColumnDef(
            stream.Id, EcrCode.Create("GCV"), new LocalizedText(new Dictionary<string, string> { ["en"] = "GCV" }), 1,
            CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();
        return new AnyWorld(world, column.Id);
    }

    private Task<long> ApproveLandAsync(AnyWorld w)
        => SourceIssueWorld.AddStateAsync(sql, w.World.Land.DocumentId, w.World.Land.SheetDefId, March, Approved.AddHours(-1), Approved);

    private async Task ChangeAsync(long documentId, int columnDefId, DateTime at, string origin = "UserEdit")
    {
        await using var db = sql.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync(
            [new CellChangeRecord(at, new CellAddress(new PeriodKey(March), 1, columnDefId), documentId, "R1", "1", "2", 5, origin, false, null)],
            CancellationToken.None);
    }

    private async Task<IReadOnlyList<SourceIssueRow>> ListAsync(long documentId)
    {
        await using var db = sql.CreateContext();
        return await new DocumentSourceMonthGate(db, readOverride: null, DependencyGraphTestKit.Provider(db))
            .ListSourceIssuesAsync(documentId, [March], CancellationToken.None);
    }

    private async Task<List<SourceIssueFinding>> FindingsAsync(long documentId)
    {
        await using var db = sql.CreateContext();
        return await new DocumentSourceIssues(db, readOverride: null, DependencyGraphTestKit.Provider(db))
            .ListAsync(documentId, [March], CancellationToken.None);
    }
}
