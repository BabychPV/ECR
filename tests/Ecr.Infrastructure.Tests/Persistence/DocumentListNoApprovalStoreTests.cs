// tests/Ecr.Infrastructure.Tests/Persistence/DocumentListNoApprovalStoreTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ B147 (B75, рішення людини 16.11): документ, чия версія шаблону за період — без подання й затвердження
/// (<c>WorkflowMode.None</c>), у переліку документів має ВЛАСНИЙ стан «без погодження» — ОДНЕ правило в чотирьох
/// місцях: смуга лічильників (<c>DocumentListSummaryStore</c>), фільтр стану (<c>DocumentStore.WhereState</c> /
/// <c>WhereYearState</c>), рядок переліку й картка (<c>workflowMode</c>, стани аркушів), клітинки року
/// (<c>PeriodStatesBatchAsync</c>).
/// </summary>
/// <remarks>
/// Кожен тест має КОНТРОЛЬ: до перемикання версії в <c>None</c> ті самі документи рахуються за п'ятьма станами.
/// Версія — чотири випадки <see cref="WorkflowNoneCase"/> (власний шаблон документа першим). Періоди — 2099xx.
/// ⛔ Мутаційні докази (локально, кожна окремо) — у звіті B147: прибрати будь-яку умову <c>IsNone</c> / ключа
/// «без погодження» в одному з місць — червоніє «узгодженість» або пряме твердження цього місця.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentListNoApprovalStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 20, 9, 0, 0, DateTimeKind.Utc);

    private const string NoApproval = "NoApproval";
    private const string Partly = "PartlySubmitted";

    /// <summary>Шість станів документа в переліку: п'ять станів погодження і «без погодження».</summary>
    private static readonly string[] States = ["Draft", Partly, "Submitted", "Approved", "Rejected", NoApproval];

    public static TheoryData<string> Modes => new()
    {
        WorkflowNoneCase.Plain, WorkflowNoneCase.SameTemplate, WorkflowNoneCase.OwnNone, WorkflowNoneCase.ProjectNone,
    };

    // ───────────────────────────── місяць ─────────────────────────────

    [Theory]
    [MemberData(nameof(Modes))]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Місяць_смуга_фільтр_і_рядок_дають_без_погодження_одним_правилом(string mode)
    {
        const int Key = 209903;
        var stage = await ArrangeAsync(Key, started: 0).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();

        // Проєкт 1: по одному документу в кожному з п'яти станів; проєкт 2 (контрольний, завжди з погодженням) — затверджений.
        var draft = await AttachAsync(db, stage.One.DocumentId, [stage.One.SheetDefId]).ConfigureAwait(true);
        var partly = await DocumentAsync(db, stage.One, "NP", [stage.One.SheetDefId, stage.Sheet2],
            (stage.One.SheetDefId, Key, DocumentStatus.Approved)).ConfigureAwait(true);
        var submitted = await DocumentAsync(db, stage.One, "NS", [stage.One.SheetDefId],
            (stage.One.SheetDefId, Key, DocumentStatus.Submitted)).ConfigureAwait(true);
        var approved = await DocumentAsync(db, stage.One, "NA", [stage.One.SheetDefId],
            (stage.One.SheetDefId, Key, DocumentStatus.Approved)).ConfigureAwait(true);
        var rejected = await DocumentAsync(db, stage.One, "NR", [stage.One.SheetDefId],
            (stage.One.SheetDefId, Key, DocumentStatus.Rejected)).ConfigureAwait(true);
        var control = await AttachAsync(db, stage.Two.DocumentId, [stage.Two.SheetDefId],
            (stage.Two.SheetDefId, Key, DocumentStatus.Approved)).ConfigureAwait(true);
        long[] own = [draft, partly, submitted, approved, rejected];

        var binding = await WorkflowNoneCase.BindAsync(mode, sql.ConnectionString, db, stage.One).ConfigureAwait(true);

        // КОНТРОЛЬ: до перемикання — п'ять станів, «без погодження» немає ніде.
        var before = await MonthAsync(stage, Key).ConfigureAwait(true);
        Assert.Equal((1, 1, 1, 2, 1, 0), Tiles(before.Summary));
        Assert.Equal((3, 7), (before.Summary.SheetsApproved, before.Summary.SheetsTotal));
        Assert.Equal(Ids(approved, control), before.ByState["Approved"]);
        Assert.Equal([draft], before.ByState["Draft"]);
        Assert.Equal([partly], before.ByState[Partly]);
        Assert.Equal([submitted], before.ByState["Submitted"]);
        Assert.Equal([rejected], before.ByState["Rejected"]);
        Assert.Empty(before.ByState[NoApproval]);
        Assert.All(before.Rows, r => Assert.Equal("SubmitApprove", r.WorkflowMode));
        Assert.Equal("Submitted", before.Rows.Single(r => r.Id == submitted).Sheets!.Single().State);
        AssertConsistent(before);

        await WorkflowNoneCase.SwitchAsync(db, binding).ConfigureAwait(true);

        var after = await MonthAsync(stage, Key).ConfigureAwait(true);
        AssertConsistent(after);

        if (!binding.DocumentBecomesNone)
        {
            // Версія проєкту — без погодження, але документ має ВЛАСНИЙ шаблон з погодженням: нічого не змінилося.
            Assert.Equal((1, 1, 1, 2, 1, 0), Tiles(after.Summary));
            Assert.Empty(after.ByState[NoApproval]);
            Assert.Equal(Ids(approved, control), after.ByState["Approved"]);
            Assert.All(after.Rows, r => Assert.Equal("SubmitApprove", r.WorkflowMode));
            Assert.Equal("Submitted", after.Rows.Single(r => r.Id == submitted).Sheets!.Single().State);
            return;
        }

        // Смуга: п'ять документів проєкту 1 — «без погодження» і поза п'ятьма плитками; контрольний — як був.
        Assert.Equal((0, 0, 0, 1, 0, 5), Tiles(after.Summary));
        Assert.Equal((1, 1), (after.Summary.SheetsApproved, after.Summary.SheetsTotal));

        // Фільтр: «без погодження» — рівно ці п'ять; у п'яти станах їх немає (і старі стани не діють).
        Assert.Equal(Ids(own), after.ByState[NoApproval]);
        Assert.Equal([control], after.ByState["Approved"]);
        Assert.Empty(after.ByState["Draft"]);
        Assert.Empty(after.ByState[Partly]);
        Assert.Empty(after.ByState["Submitted"]);
        Assert.Empty(after.ByState["Rejected"]);

        // Рядок: режим версії за період і аркуші без станів погодження; контрольний документ — без змін.
        Assert.All(after.Rows.Where(r => own.Contains(r.Id)), r =>
        {
            Assert.Equal("None", r.WorkflowMode);
            Assert.All(r.Sheets!, s => Assert.Equal("Draft", s.State));
            Assert.All(r.SheetStates.Values, s => Assert.Equal("Draft", s));
        });
        var controlRow = after.Rows.Single(r => r.Id == control);
        Assert.Equal("SubmitApprove", controlRow.WorkflowMode);
        Assert.Equal("Approved", controlRow.Sheets!.Single().State);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Перелік_без_періоду_й_року_режиму_не_несе_і_лишається_без_зайвих_запитів()
    {
        const int Key = 209904;
        var stage = await ArrangeAsync(Key, started: 0).ConfigureAwait(true);
        await using (var db = stage.Builder.CreateContext())
        {
            await DocumentAsync(db, stage.One, "NQ", [stage.One.SheetDefId]).ConfigureAwait(true);
            await WorkflowNoneCase.SwitchAsync(db, new WorkflowNoneCase.Binding(stage.One.TemplateVersionId, true)).ConfigureAwait(true);
        }

        var counter = new DbCommandCounter();
        await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString).AddInterceptors(counter).Options);

        var bare = await new DocumentStore(counted).ListAsync(
            stage.One.ProjectId, new PeriodKeyFilter(null), default, new CursorRequest(Limit: 50), null, CancellationToken.None)
            .ConfigureAwait(true);
        var withoutPeriod = counter.Tally.Snapshot().Total;
        counter.Tally.Reset();

        // Без періоду стан документа не визначений (D-93): режиму в рядку немає.
        Assert.All(bare.Items, r => Assert.Null(r.WorkflowMode));

        // З періодом — рівно ОДИН додатковий запит ключів «без погодження» (стани, зауваження, застарілість — як і доти),
        // незалежно від того, скільки документів без погодження.
        var month = await new DocumentStore(counted).ListAsync(
            stage.One.ProjectId, new PeriodKeyFilter(Key), default, new CursorRequest(Limit: 50), null, CancellationToken.None)
            .ConfigureAwait(true);
        Assert.All(month.Items, r => Assert.Equal("None", r.WorkflowMode));

        counter.Tally.Reset();
        await new DocumentStore(counted).ListAsync(
            stage.Two.ProjectId, new PeriodKeyFilter(Key), default, new CursorRequest(Limit: 50), null, CancellationToken.None)
            .ConfigureAwait(true);
        var plainProject = counter.Tally.Snapshot().Total;
        counter.Tally.Reset();
        await new DocumentStore(counted).ListAsync(
            stage.One.ProjectId, new PeriodKeyFilter(Key), default, new CursorRequest(Limit: 50), null, CancellationToken.None)
            .ConfigureAwait(true);
        Assert.True(
            counter.Tally.Snapshot().Total == plainProject,
            $"проєкт без погодження {counter.Tally.Snapshot().Total}, звичайний {plainProject}, без періоду {withoutPeriod}");
    }

    // ───────────────────────────── рік ─────────────────────────────

    [Theory]
    [MemberData(nameof(Modes))]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Рік_усі_розпочаті_періоди_без_погодження_документ_без_погодження(string mode)
    {
        const int Year = 2099;
        var stage = await ArrangeAsync(209901, started: 2).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();

        var draft = await AttachAsync(db, stage.One.DocumentId, [stage.One.SheetDefId]).ConfigureAwait(true);
        var submitted = await DocumentAsync(db, stage.One, "YS", [stage.One.SheetDefId],
            (stage.One.SheetDefId, 209901, DocumentStatus.Submitted), (stage.One.SheetDefId, 209902, DocumentStatus.Approved))
            .ConfigureAwait(true);
        var approved = await DocumentAsync(db, stage.One, "YA", [stage.One.SheetDefId],
            (stage.One.SheetDefId, 209901, DocumentStatus.Approved), (stage.One.SheetDefId, 209902, DocumentStatus.Approved))
            .ConfigureAwait(true);
        var rejected = await DocumentAsync(db, stage.One, "YR", [stage.One.SheetDefId],
            (stage.One.SheetDefId, 209901, DocumentStatus.Approved), (stage.One.SheetDefId, 209902, DocumentStatus.Rejected))
            .ConfigureAwait(true);
        var control = await AttachAsync(db, stage.Two.DocumentId, [stage.Two.SheetDefId],
            (stage.Two.SheetDefId, 209901, DocumentStatus.Approved), (stage.Two.SheetDefId, 209902, DocumentStatus.Approved))
            .ConfigureAwait(true);
        long[] own = [draft, submitted, approved, rejected];

        var binding = await WorkflowNoneCase.BindAsync(mode, sql.ConnectionString, db, stage.One).ConfigureAwait(true);

        var before = await YearAsync(stage, Year).ConfigureAwait(true);
        Assert.Equal((1, 0, 1, 2, 1, 0), Tiles(before.Summary));
        Assert.Equal(Ids(approved, control), before.ByState["Approved"]);
        Assert.Empty(before.ByState[NoApproval]);
        Assert.DoesNotContain(before.Rows.SelectMany(r => r.PeriodStates!), c => c.State == NoApproval);
        AssertTilesMatchFilter(before);

        await WorkflowNoneCase.SwitchAsync(db, binding).ConfigureAwait(true);

        var after = await YearAsync(stage, Year).ConfigureAwait(true);
        AssertTilesMatchFilter(after);

        if (!binding.DocumentBecomesNone)
        {
            Assert.Equal((1, 0, 1, 2, 1, 0), Tiles(after.Summary));
            Assert.Empty(after.ByState[NoApproval]);
            Assert.DoesNotContain(after.Rows.SelectMany(r => r.PeriodStates!), c => c.State == NoApproval);
            return;
        }

        Assert.Equal((0, 0, 0, 1, 0, 4), Tiles(after.Summary));
        Assert.Equal((2, 2), (after.Summary.SheetsApproved, after.Summary.SheetsTotal));
        Assert.Equal(Ids(own), after.ByState[NoApproval]);
        Assert.Equal([control], after.ByState["Approved"]);
        Assert.Empty(after.ByState["Draft"]);
        Assert.Empty(after.ByState["Submitted"]);
        Assert.Empty(after.ByState["Rejected"]);

        // Клітинки року: усі дванадцять місяців документа — «без погодження» (і розпочаті, і ні); контрольний — як був.
        Assert.All(after.Rows.Where(r => own.Contains(r.Id)), r =>
        {
            Assert.Equal(12, r.PeriodStates!.Count);
            Assert.All(r.PeriodStates!, c => Assert.Equal(NoApproval, c.State));
            Assert.Null(r.WorkflowMode);
        });
        Assert.Equal(
            (string[])["Approved", "Approved", .. Enumerable.Repeat("Draft", 10)],
            after.Rows.Single(r => r.Id == control).PeriodStates!.Select(c => c.State));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Рік_версія_без_погодження_лише_з_лютого_рахується_по_періодах()
    {
        const int Year = 2099;
        var stage = await ArrangeAsync(209901, started: 3).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();

        // Січень — з погодженням; лютий і березень (розпочаті) — версія без погодження.
        var onlyJanuary = await AttachAsync(db, stage.One.DocumentId, [stage.One.SheetDefId],
            (stage.One.SheetDefId, 209901, DocumentStatus.Approved)).ConfigureAwait(true);
        var plain = stage.Two.DocumentId; // документ контрольного проєкту без складу — чернетка і до, і після
        var staleRejected = await DocumentAsync(db, stage.One, "MR", [stage.One.SheetDefId],
            (stage.One.SheetDefId, 209901, DocumentStatus.Approved), (stage.One.SheetDefId, 209902, DocumentStatus.Rejected))
            .ConfigureAwait(true);
        var staleSubmitted = await DocumentAsync(db, stage.One, "MS", [stage.One.SheetDefId],
            (stage.One.SheetDefId, 209902, DocumentStatus.Submitted)).ConfigureAwait(true);

        // КОНТРОЛЬ: шкали версій ще немає — усі три місяці з погодженням.
        var before = await YearAsync(stage, Year).ConfigureAwait(true);
        Assert.Equal(Ids(onlyJanuary, staleSubmitted), before.ByState[Partly]);
        Assert.Equal([staleRejected], before.ByState["Rejected"]);
        Assert.Equal((1, 2, 0, 0, 1, 0), Tiles(before.Summary));
        AssertTilesMatchFilter(before);

        var second = new TemplateVersion(stage.One.TemplateId, $"9.{Guid.NewGuid():N}"[..12], 1, Now);
        db.TemplateVersions.Add(second);
        await db.SaveChangesAsync().ConfigureAwait(true);
        db.ProjectTemplateVersions.AddRange(
            new ProjectTemplateVersion(stage.One.ProjectId, stage.One.TemplateId, 209901, stage.One.TemplateVersionId),
            new ProjectTemplateVersion(stage.One.ProjectId, stage.One.TemplateId, 209902, second.Id));
        await db.SaveChangesAsync().ConfigureAwait(true);
        await WorkflowNoneCase.SwitchAsync(db, new WorkflowNoneCase.Binding(second.Id, true)).ConfigureAwait(true);

        var after = await YearAsync(stage, Year).ConfigureAwait(true);
        AssertTilesMatchFilter(after);

        // Періоди без погодження не рахуються: січень затверджено — документ затверджений (а не «частково»);
        // старий «відхилено» лютого не діє; поданий лише в лютому — чернетка за січнем.
        Assert.Equal(Ids(onlyJanuary, staleRejected), after.ByState["Approved"]);
        Assert.Equal(Ids(staleSubmitted, plain), after.ByState["Draft"]);
        Assert.Empty(after.ByState[Partly]);
        Assert.Empty(after.ByState["Rejected"]);
        Assert.Empty(after.ByState[NoApproval]);
        Assert.Equal((2, 0, 0, 2, 0, 0), Tiles(after.Summary));
        Assert.Equal((2, 3), (after.Summary.SheetsApproved, after.Summary.SheetsTotal));

        // Клітинки: січень — стан погодження, лютий–грудень — «без погодження».
        Assert.Equal(
            (string[])["Approved", .. Enumerable.Repeat(NoApproval, 11)],
            after.Rows.Single(r => r.Id == staleRejected).PeriodStates!.Select(c => c.State));
        Assert.Equal(
            (string[])["Draft", .. Enumerable.Repeat(NoApproval, 11)],
            after.Rows.Single(r => r.Id == staleSubmitted).PeriodStates!.Select(c => c.State));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Рік_без_розпочатих_періодів_лишається_чернеткою()
    {
        const int Year = 2099;
        var stage = await ArrangeAsync(209901, started: 0).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();
        var document = await AttachAsync(db, stage.One.DocumentId, [stage.One.SheetDefId]).ConfigureAwait(true);

        await WorkflowNoneCase.SwitchAsync(db, new WorkflowNoneCase.Binding(stage.One.TemplateVersionId, true)).ConfigureAwait(true);

        // Стан року — за РОЗПОЧАТИМИ періодами; їх немає — «чернетка», як і доти (а не «без погодження»).
        // Другий рядок — документ контрольного проєкту без складу.
        var year = await YearAsync(stage, Year).ConfigureAwait(true);
        AssertTilesMatchFilter(year);
        Assert.Equal(Ids(document, stage.Two.DocumentId), year.ByState["Draft"]);
        Assert.Empty(year.ByState[NoApproval]);
        Assert.Equal((2, 0, 0, 0, 0, 0), Tiles(year.Summary));

        // Клітинки при цьому — по періодах: версія кожного місяця без погодження.
        Assert.All(year.Rows.Single(r => r.Id == document).PeriodStates!, c => Assert.Equal(NoApproval, c.State));
    }

    /// <summary>
    /// Розпочаті січень і лютий — версія без погодження; з березня (ще не розпочатого) — знову версія з погодженням.
    /// Стан року — за РОЗПОЧАТИМИ періодами: документ «без погодження», майбутній місяць цього не змінює.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Рік_майбутній_період_з_погодженням_не_робить_документ_чернеткою()
    {
        const int Year = 2099;
        var stage = await ArrangeAsync(209901, started: 2).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();
        var document = await AttachAsync(db, stage.One.DocumentId, [stage.One.SheetDefId]).ConfigureAwait(true);

        var none = new TemplateVersion(stage.One.TemplateId, $"8.{Guid.NewGuid():N}"[..12], 1, Now);
        db.TemplateVersions.Add(none);
        await db.SaveChangesAsync().ConfigureAwait(true);
        db.ProjectTemplateVersions.AddRange(
            new ProjectTemplateVersion(stage.One.ProjectId, stage.One.TemplateId, 209901, none.Id),
            new ProjectTemplateVersion(stage.One.ProjectId, stage.One.TemplateId, 209903, stage.One.TemplateVersionId));
        await db.SaveChangesAsync().ConfigureAwait(true);

        // КОНТРОЛЬ: версія ще з погодженням — чернетка.
        var before = await YearAsync(stage, Year).ConfigureAwait(true);
        Assert.Contains(document, before.ByState["Draft"]);
        Assert.Empty(before.ByState[NoApproval]);

        await WorkflowNoneCase.SwitchAsync(db, new WorkflowNoneCase.Binding(none.Id, true)).ConfigureAwait(true);

        var after = await YearAsync(stage, Year).ConfigureAwait(true);
        AssertTilesMatchFilter(after);
        Assert.Equal([document], after.ByState[NoApproval]);
        Assert.Equal(1, after.Summary.NoApproval);
        Assert.DoesNotContain(document, after.ByState["Draft"]);
        Assert.Equal(
            (string[])[NoApproval, NoApproval, .. Enumerable.Repeat("Draft", 10)],
            after.Rows.Single(r => r.Id == document).PeriodStates!.Select(c => c.State));
    }

    /// <summary>
    /// Два документи ОДНОГО проєкту: один без власного шаблону (версія проєкту), другий — з власним іншим шаблоном
    /// (B74). «Без погодження» стає лише той, чия версія без погодження, — ключ звіряє і проєкт, і шаблон документа.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Документи_одного_проєкту_з_різними_шаблонами_кожен_за_своєю_версією(bool ownVersionIsNone)
    {
        const int Key = 209901;
        var stage = await ArrangeAsync(Key, started: 1).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();

        var byProject = await AttachAsync(db, stage.One.DocumentId, [stage.One.SheetDefId],
            (stage.One.SheetDefId, Key, DocumentStatus.Approved)).ConfigureAwait(true);
        var byOwn = await DocumentAsync(db, stage.One, "TO", [stage.One.SheetDefId],
            (stage.One.SheetDefId, Key, DocumentStatus.Approved)).ConfigureAwait(true);

        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 209912).ConfigureAwait(true);
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId) VALUES ({stage.One.ProjectId}, {other.TemplateId}, {other.TemplateVersionId})")
            .ConfigureAwait(true);
        await db.Database.ExecuteSqlAsync($"UPDATE doc.Document SET TemplateId = {other.TemplateId} WHERE Id = {byOwn}")
            .ConfigureAwait(true);

        // КОНТРОЛЬ: обидві версії з погодженням.
        var before = await MonthAsync(stage, Key).ConfigureAwait(true);
        AssertConsistent(before);
        Assert.Equal(Ids(byProject, byOwn), before.ByState["Approved"]);
        Assert.Empty(before.ByState[NoApproval]);

        await WorkflowNoneCase.SwitchAsync(
                db, new WorkflowNoneCase.Binding(ownVersionIsNone ? other.TemplateVersionId : stage.One.TemplateVersionId, true))
            .ConfigureAwait(true);
        var (none, approved) = ownVersionIsNone ? (byOwn, byProject) : (byProject, byOwn);

        var month = await MonthAsync(stage, Key).ConfigureAwait(true);
        AssertConsistent(month);
        Assert.Equal([none], month.ByState[NoApproval]);
        Assert.Equal([approved], month.ByState["Approved"]);
        Assert.Equal((1, 1), (month.Summary.NoApproval, month.Summary.Approved));
        Assert.Equal("None", month.Rows.Single(r => r.Id == none).WorkflowMode);
        Assert.Equal("SubmitApprove", month.Rows.Single(r => r.Id == approved).WorkflowMode);

        var year = await YearAsync(stage, 2099).ConfigureAwait(true);
        AssertTilesMatchFilter(year);
        Assert.Equal([none], year.ByState[NoApproval]);
        Assert.Equal([approved], year.ByState["Approved"]);
        Assert.Equal((1, 1), (year.Summary.NoApproval, year.Summary.Approved));
        Assert.All(year.Rows.Single(r => r.Id == none).PeriodStates!, c => Assert.Equal(NoApproval, c.State));
        Assert.Equal("Approved", year.Rows.Single(r => r.Id == approved).PeriodStates![0].State);
    }

    // ───────────────────────────── картка ─────────────────────────────

    [Theory]
    [MemberData(nameof(Modes))]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Картка_з_періодом_несе_режим_версії_за_період_і_аркуші_без_станів(string mode)
    {
        const int Key = 209905;
        var stage = await ArrangeAsync(Key, started: 0).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();
        var document = await DocumentAsync(db, stage.One, "CD", [stage.One.SheetDefId],
            (stage.One.SheetDefId, Key, DocumentStatus.Submitted)).ConfigureAwait(true);
        var binding = await WorkflowNoneCase.BindAsync(mode, sql.ConnectionString, db, stage.One).ConfigureAwait(true);

        async Task<DocumentSummary> CardAsync()
        {
            await using var read = stage.Builder.CreateContext();
            return (await new DocumentStore(read).FindAsync(document, new PeriodKeyFilter(Key), CancellationToken.None)
                .ConfigureAwait(false))!;
        }

        var before = await CardAsync().ConfigureAwait(true);
        Assert.Equal(("SubmitApprove", "Submitted"), (before.WorkflowMode, before.Sheets!.Single().State));

        await WorkflowNoneCase.SwitchAsync(db, binding).ConfigureAwait(true);

        var after = await CardAsync().ConfigureAwait(true);
        Assert.Equal(
            binding.DocumentBecomesNone ? ("None", "Draft") : ("SubmitApprove", "Submitted"),
            (after.WorkflowMode, after.Sheets!.Single().State));
        Assert.Equal(after.Sheets!.Single().State, after.SheetStates.Values.Single());

        // Без періоду картка, як і доти, несе режим ТИПОВОЇ версії проєкту (стану аркушів без періоду немає).
        await using var plain = stage.Builder.CreateContext();
        var withoutPeriod = (await new DocumentStore(plain)
            .FindAsync(document, new PeriodKeyFilter(null), CancellationToken.None).ConfigureAwait(true))!;
        var projectVersionIsNone = mode is WorkflowNoneCase.Plain or WorkflowNoneCase.SameTemplate or WorkflowNoneCase.ProjectNone;
        Assert.Equal(projectVersionIsNone ? "None" : "SubmitApprove", withoutPeriod.WorkflowMode);
        Assert.Empty(withoutPeriod.SheetStates);
    }

    // ───────────────────── вісь періоду (F13 AU, B147) ─────────────────────

    /// <summary>
    /// Шкала версій в одному році: січень — версія A (з погодженням), з лютого — версія B без погодження. Місяць
    /// 209901 — НЕ «без погодження», місяць 209902 — «без погодження»: смуга, фільтр і рядок беруть версію САМЕ
    /// запитаного місяця. Два документи: без власного шаблону (<c>doc.fn_VersionOf</c>) і з власним тим самим
    /// шаблоном (<c>doc.fn_TemplateVersionOf</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Мутації (F13 AU): у <c>WorkflowNoneScope.LoadAsync</c> узяти версію іншого періоду (<c>999912</c> замість
    /// <c>pk.PeriodKey</c>) — січень стає «без погодження», червоний; узяти <c>pk.PeriodKey - 1</c> — лютий лишається з
    /// погодженням, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Місяць_бере_версію_саме_цього_місяця_січень_з_погодженням_лютий_без()
    {
        var stage = await ArrangeAsync(209901, started: 2).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();

        var byProject = await AttachAsync(db, stage.One.DocumentId, [stage.One.SheetDefId],
            (stage.One.SheetDefId, 209901, DocumentStatus.Approved), (stage.One.SheetDefId, 209902, DocumentStatus.Approved))
            .ConfigureAwait(true);
        var byOwn = await DocumentAsync(db, stage.One, "TL", [stage.One.SheetDefId],
            (stage.One.SheetDefId, 209901, DocumentStatus.Approved), (stage.One.SheetDefId, 209902, DocumentStatus.Approved))
            .ConfigureAwait(true);
        await db.Database.ExecuteSqlAsync($"UPDATE doc.Document SET TemplateId = {stage.One.TemplateId} WHERE Id = {byOwn}")
            .ConfigureAwait(true);
        var second = await TimelineAsync(db, stage).ConfigureAwait(true);
        await WorkflowNoneCase.SwitchAsync(db, new WorkflowNoneCase.Binding(second, true)).ConfigureAwait(true);
        long[] own = [byProject, byOwn];

        var january = await MonthAsync(stage, 209901).ConfigureAwait(true);
        AssertConsistent(january);
        Assert.Empty(january.ByState[NoApproval]);
        Assert.Equal(0, january.Summary.NoApproval);
        Assert.All(own, id => Assert.Contains(id, january.ByState["Approved"]));
        Assert.All(january.Rows.Where(r => own.Contains(r.Id)), r => Assert.Equal("SubmitApprove", r.WorkflowMode));

        var february = await MonthAsync(stage, 209902).ConfigureAwait(true);
        AssertConsistent(february);
        Assert.Equal(Ids(own), february.ByState[NoApproval]);
        Assert.Equal(2, february.Summary.NoApproval);
        Assert.All(own, id => Assert.DoesNotContain(id, february.ByState["Approved"]));
        Assert.All(february.Rows.Where(r => own.Contains(r.Id)), r => Assert.Equal("None", r.WorkflowMode));
    }

    /// <summary>
    /// Та сама шкала версій для картки: картка за січнем — режим версії A, за лютим — версії B без погодження.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація (F13 AU): у <c>DocumentStore.FindAsync</c> узяти період <c>999912</c> замість запитаного — картка
    /// за січнем показує «None», червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Картка_бере_версію_запитаного_місяця_а_не_останнього()
    {
        var stage = await ArrangeAsync(209901, started: 2).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();
        var document = await AttachAsync(db, stage.One.DocumentId, [stage.One.SheetDefId],
            (stage.One.SheetDefId, 209901, DocumentStatus.Submitted), (stage.One.SheetDefId, 209902, DocumentStatus.Submitted))
            .ConfigureAwait(true);
        var second = await TimelineAsync(db, stage).ConfigureAwait(true);
        await WorkflowNoneCase.SwitchAsync(db, new WorkflowNoneCase.Binding(second, true)).ConfigureAwait(true);

        async Task<DocumentSummary> CardAsync(int key)
        {
            await using var read = stage.Builder.CreateContext();
            return (await new DocumentStore(read).FindAsync(document, new PeriodKeyFilter(key), CancellationToken.None)
                .ConfigureAwait(false))!;
        }

        var january = await CardAsync(209901).ConfigureAwait(true);
        Assert.Equal(("SubmitApprove", "Submitted"), (january.WorkflowMode, january.Sheets!.Single().State));

        var february = await CardAsync(209902).ConfigureAwait(true);
        Assert.Equal(("None", "Draft"), (february.WorkflowMode, february.Sheets!.Single().State));
    }

    /// <summary>
    /// Документ без погодження БЕЗ жодного аркуша на смузі місяця: рахується в «без погодження» (не в чернетки), і
    /// плитка дорівнює фільтру.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація (F13 AU): у <c>DocumentListSummaryStore.SummarizeAsync</c> рахувати «без погодження» лише документи з
    /// аркушами — плитка 0 при фільтрі 1, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B147")]
    public async Task Документ_без_погодження_без_аркушів_на_смузі_місяця_без_погодження()
    {
        const int Key = 209906;
        var stage = await ArrangeAsync(Key, started: 0).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();

        // Документ ланцюга 1 — без складу (нуль аркушів); документ ланцюга 2 — контрольний, теж без складу.
        await WorkflowNoneCase.SwitchAsync(db, new WorkflowNoneCase.Binding(stage.One.TemplateVersionId, true)).ConfigureAwait(true);

        var month = await MonthAsync(stage, Key).ConfigureAwait(true);
        AssertConsistent(month);
        Assert.Equal([stage.One.DocumentId], month.ByState[NoApproval]);
        Assert.Equal(1, month.Summary.NoApproval);
        Assert.Equal([stage.Two.DocumentId], month.ByState["Draft"]);
        Assert.Equal((1, 0, 0, 0, 0, 1), Tiles(month.Summary));
        Assert.Equal((0, 0), (month.Summary.SheetsApproved, month.Summary.SheetsTotal));
    }

    /// <summary>
    /// ✎ B162 (F13 AU до B147): ключі «без погодження» — не межа видимості. Читач, якому гранти дають лише проєкт 2,
    /// не бачить документів проєкту 1 «без погодження» ні на смузі, ні у фільтрі <c>NoApproval</c>, ні в рядках, ні в
    /// клітинках року — хоча <c>WorkflowNoneScope</c> ці ключі завантажує. Allow-пара: той самий етап з обома
    /// проєктами показує їх.
    /// </summary>
    /// <remarks>
    /// Deny-мутація (для AU, автор межі грантів не мутує): у <c>DocumentStore.ListAsync</c> / <c>WhereState</c> або
    /// <c>DocumentListSummaryStore</c> прибрати обмеження видимими проєктами для гілки «без погодження» → червоний цей тест
    /// (Deny-частина), Allow-частина лишається зеленою.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B162")]
    public async Task Без_погодження_не_виходить_за_межу_грантів_місяць_і_рік()
    {
        const int Key = 209901;
        const int Year = 2099;
        var stage = await ArrangeAsync(Key, started: 1).ConfigureAwait(true);
        await using var db = stage.Builder.CreateContext();

        var own = await DocumentAsync(db, stage.One, "GD", [stage.One.SheetDefId],
            (stage.One.SheetDefId, Key, DocumentStatus.Submitted)).ConfigureAwait(true);
        var control = await AttachAsync(db, stage.Two.DocumentId, [stage.Two.SheetDefId],
            (stage.Two.SheetDefId, Key, DocumentStatus.Approved)).ConfigureAwait(true);
        await WorkflowNoneCase.SwitchAsync(db, new WorkflowNoneCase.Binding(stage.One.TemplateVersionId, true)).ConfigureAwait(true);
        long[] noneOfOne = Ids(own, stage.One.DocumentId);

        // Allow: обидва проєкти видимі — документи проєкту 1 «без погодження» в місяці й році.
        var monthAll = await MonthAsync(stage, Key).ConfigureAwait(true);
        Assert.Equal(noneOfOne, monthAll.ByState[NoApproval]);
        Assert.Equal(2, monthAll.Summary.NoApproval);
        var yearAll = await YearAsync(stage, Year).ConfigureAwait(true);
        Assert.Equal(noneOfOne, yearAll.ByState[NoApproval]);
        Assert.Equal(2, yearAll.Summary.NoApproval);

        // Deny: гранти лише на проєкт 2 — проєкту 1 немає ніде, «без погодження» порожнє.
        int[] onlyTwo = [stage.Two.ProjectId];
        var month = await MonthAsync(stage, Key, onlyTwo).ConfigureAwait(true);
        AssertConsistent(month);
        Assert.Empty(month.ByState[NoApproval]);
        Assert.Equal(0, month.Summary.NoApproval);
        Assert.Equal([control], month.Rows.Select(r => r.Id));
        Assert.Equal((0, 0, 0, 1, 0, 0), Tiles(month.Summary));

        var year = await YearAsync(stage, Year, onlyTwo).ConfigureAwait(true);
        AssertTilesMatchFilter(year);
        Assert.Empty(year.ByState[NoApproval]);
        Assert.Equal(0, year.Summary.NoApproval);
        Assert.Equal([control], year.Rows.Select(r => r.Id));
        Assert.DoesNotContain(year.Rows.SelectMany(r => r.PeriodStates!), c => c.State == NoApproval);
    }

    /// <summary>
    /// Шкала версій ланцюга 1: січень — його версія (A), з лютого — нова версія того самого шаблону; повертає її Id.
    /// </summary>
    private static async Task<int> TimelineAsync(EcrDbContext db, Stage stage)
    {
        var second = new TemplateVersion(stage.One.TemplateId, $"7.{Guid.NewGuid():N}"[..12], 1, Now);
        db.TemplateVersions.Add(second);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ProjectTemplateVersions.AddRange(
            new ProjectTemplateVersion(stage.One.ProjectId, stage.One.TemplateId, 209901, stage.One.TemplateVersionId),
            new ProjectTemplateVersion(stage.One.ProjectId, stage.One.TemplateId, 209902, second.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return second.Id;
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    /// <summary>Два проєкти (два ланцюги) в одному періоді й другий аркуш версії першого.</summary>
    private sealed record Stage(TestDocumentBuilder Builder, TestDocument One, TestDocument Two, int Sheet2)
    {
        public int[] Projects => [One.ProjectId, Two.ProjectId];
    }

    /// <summary>Знімок переліку: смуга, рядки без фільтра й ідентифікатори рядків під кожним фільтром стану.</summary>
    private sealed record Snapshot(
        DocumentListSummaryResponse Summary, IReadOnlyList<DocumentSummary> Rows, Dictionary<string, long[]> ByState);

    /// <summary>
    /// Два ланцюги будівника в періоді <paramref name="periodKey"/>; за <paramref name="started"/> &gt; 0 — ще й 12
    /// періодів року з першими <paramref name="started"/> розпочатими (без розпочатих і з січнем — 12 запланованих).
    /// ⚠ Власні документи ланцюгів (без складу) теж у переліку: тести дають їм склад і стани через
    /// <see cref="AttachAsync"/> або рахують їх чернетками.
    /// </summary>
    private async Task<Stage> ArrangeAsync(int periodKey, int started)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var one = await builder.BuildAsync(periodKey: periodKey).ConfigureAwait(false);
        var two = await builder.BuildAsync(periodKey: periodKey).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        if (started > 0)
        {
            foreach (var chain in new[] { one, two })
            {
                var year = chain.PeriodKey.Year;
                var existing = await db.Periods.Where(p => p.ProjectId == chain.ProjectId).ToListAsync().ConfigureAwait(false);
                for (var m = 1; m <= 12; m++)
                {
                    var key = new PeriodKey(year * 100 + m);
                    var period = existing.SingleOrDefault(p => p.PeriodKeyValue == key.Value);
                    if (period is null)
                    {
                        period = new Period(
                            chain.ProjectId, key, (byte)m, new DateOnly(year, m, 1), new DateOnly(year, m, DateTime.DaysInMonth(year, m)));
                        db.Periods.Add(period);
                    }

                    if (m <= started)
                    {
                        period.AdvanceTo(PeriodState.Open, Now);
                    }
                }
            }
        }
        else if (periodKey % 100 == 1)
        {
            // Рік без розпочатих періодів: дванадцять запланованих.
            foreach (var chain in new[] { one, two })
            {
                var year = chain.PeriodKey.Year;
                for (var m = 2; m <= 12; m++)
                {
                    db.Periods.Add(new Period(
                        chain.ProjectId, new PeriodKey(year * 100 + m), (byte)m, new DateOnly(year, m, 1),
                        new DateOnly(year, m, DateTime.DaysInMonth(year, m))));
                }
            }
        }

        var sheet2 = new SheetDef(
            one.TemplateVersionId, EcrCode.Create($"NS2_{Guid.NewGuid():N}"[..12].ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet 2" }), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stage(builder, one, two, sheet2.Id);
    }

    /// <summary>Документ зі складом і станами аркушів (аркуш, період, стан).</summary>
    private static async Task<long> DocumentAsync(
        EcrDbContext db, TestDocument chain, string key, int[] sheets,
        params (int Sheet, int PeriodKey, DocumentStatus State)[] states)
    {
        var document = new Document(chain.ProjectId, $"{key}-{Guid.NewGuid():N}"[..20], 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return await AttachAsync(db, document.Id, sheets, states).ConfigureAwait(false);
    }

    /// <summary>Склад і стани аркушів наявного документа (документ самого ланцюга будівника).</summary>
    private static async Task<long> AttachAsync(
        EcrDbContext db, long documentId, int[] sheets, params (int Sheet, int PeriodKey, DocumentStatus State)[] states)
    {
        foreach (var sheet in sheets)
        {
            db.DocumentSheets.Add(new DocumentSheet(documentId, sheet));
        }

        foreach (var (sheet, periodKey, wanted) in states)
        {
            var state = new ApprovalState(documentId, sheet, periodKey);
            state.Submit(1, Now);
            if (wanted == DocumentStatus.Approved)
            {
                state.Approve(1, Now);
            }
            else if (wanted == DocumentStatus.Rejected)
            {
                state.Reject(1, "B147", Now);
            }

            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return documentId;
    }

    private static long[] Ids(params long[] ids) => [.. ids.Order()];

    private static DocumentListFilter FilterOf(string state)
        => state switch
        {
            NoApproval => new DocumentListFilter(null, null, NoApproval: true),
            Partly => new DocumentListFilter(null, null, PartlySubmitted: true),
            _ => new DocumentListFilter(Enum.Parse<DocumentStatus>(state), null),
        };

    // visible — межа грантів читача (видимі проєкти); null — обидва проєкти етапу.
    private static async Task<Snapshot> MonthAsync(Stage stage, int periodKey, int[]? visible = null)
    {
        visible ??= stage.Projects;
        await using var db = stage.Builder.CreateContext();
        var summary = await new DocumentListSummaryStore(db)
            .SummarizeAsync(null, periodKey, visible, restrictions: null, currentUserId: null, CancellationToken.None)
            .ConfigureAwait(false);

        return new Snapshot(
            summary,
            await RowsAsync(db, visible, new PeriodKeyFilter(periodKey), default).ConfigureAwait(false),
            await ByStateAsync(db, visible, new PeriodKeyFilter(periodKey), year: null).ConfigureAwait(false));
    }

    // visible — межа грантів читача (видимі проєкти); null — обидва проєкти етапу.
    private static async Task<Snapshot> YearAsync(Stage stage, int year, int[]? visible = null)
    {
        visible ??= stage.Projects;
        await using var db = stage.Builder.CreateContext();
        var summary = await new DocumentListSummaryStore(db)
            .SummarizeYearAsync(null, year, visible, restrictions: null, currentUserId: null, CancellationToken.None)
            .ConfigureAwait(false);

        return new Snapshot(
            summary,
            await RowsAsync(db, visible, new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: year)).ConfigureAwait(false),
            await ByStateAsync(db, visible, new PeriodKeyFilter(null), year).ConfigureAwait(false));
    }

    private static async Task<Dictionary<string, long[]>> ByStateAsync(
        EcrDbContext db, int[] visible, PeriodKeyFilter period, int? year)
    {
        var result = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var state in States)
        {
            var rows = await RowsAsync(db, visible, period, FilterOf(state) with { Year = year }).ConfigureAwait(false);
            result[state] = [.. rows.Select(r => r.Id).Order()];
        }

        return result;
    }

    private static async Task<IReadOnlyList<DocumentSummary>> RowsAsync(
        EcrDbContext db, int[] visible, PeriodKeyFilter period, DocumentListFilter filter)
        => (await new DocumentStore(db).ListAsync(
                null, period, filter, new CursorRequest(Limit: 50), visible, CancellationToken.None)
            .ConfigureAwait(false)).Items;

    private static (int Draft, int Partly, int Submitted, int Approved, int Rejected, int NoApproval) Tiles(
        DocumentListSummaryResponse s)
        => (s.Draft, s.PartlySubmitted, s.Submitted, s.Approved, s.Rejected, s.NoApproval);

    private static int TileOf(DocumentListSummaryResponse s, string state)
        => state switch
        {
            "Draft" => s.Draft,
            Partly => s.PartlySubmitted,
            "Submitted" => s.Submitted,
            "Approved" => s.Approved,
            "Rejected" => s.Rejected,
            _ => s.NoApproval,
        };

    /// <summary>
    /// Стан документа з рядка переліку — так, як його виводить клієнт: «без погодження» за <c>workflowMode</c>, інакше
    /// найгірший зі станів аркушів (<c>documentSheets.ts</c>).
    /// </summary>
    private static string RowState(DocumentSummary row)
    {
        if (row.WorkflowMode == "None")
        {
            return NoApproval;
        }

        var states = row.SheetStates.Values.ToList();
        if (states.Contains("Rejected"))
        {
            return "Rejected";
        }

        var sent = states.Count(s => s is "Submitted" or "Approved");
        if (sent == 0)
        {
            return "Draft";
        }

        if (sent < states.Count)
        {
            return Partly;
        }

        return states.TrueForAll(s => s == "Approved") ? "Approved" : "Submitted";
    }

    /// <summary>U-03: число плитки = кількість рядків під фільтром цього стану.</summary>
    private static void AssertTilesMatchFilter(Snapshot snapshot)
    {
        foreach (var state in States)
        {
            Assert.True(
                TileOf(snapshot.Summary, state) == snapshot.ByState[state].Length,
                $"{state}: плитка {TileOf(snapshot.Summary, state)}, фільтр {snapshot.ByState[state].Length}");
        }

        // Шість станів не перетинаються і разом дають усі рядки переліку.
        Assert.Equal(
            snapshot.Rows.Select(r => r.Id).Order(),
            snapshot.ByState.Values.SelectMany(ids => ids).Order());
    }

    /// <summary>U-03 за місяць: плитка = фільтр = стан рядка (три відповіді про один документ збігаються).</summary>
    private static void AssertConsistent(Snapshot snapshot)
    {
        AssertTilesMatchFilter(snapshot);
        foreach (var state in States)
        {
            Assert.Equal(
                snapshot.ByState[state],
                snapshot.Rows.Where(r => RowState(r) == state).Select(r => r.Id).Order());
        }
    }
}
