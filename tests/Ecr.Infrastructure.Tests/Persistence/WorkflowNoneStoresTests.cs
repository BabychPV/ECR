// tests/Ecr.Infrastructure.Tests/Persistence/WorkflowNoneStoresTests.cs
using Ecr.Domain.Entities.Workflow;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ B138 (B75 lane 1b): версія шаблону без подання й затвердження (<c>WorkflowMode.None</c>) — старі стани
/// <c>wf.ApprovalState</c> (лишилися після переносу версії) не діють: черга погодження й «повернуті мені» їх не
/// показують, перерахунок не бачить ні станів аркушів, ні «затверджених для пропуску».
/// </summary>
/// <remarks>
/// Кожен тест має КОНТРОЛЬ: до перемикання режиму той самий рядок є. Випадки версії — <see cref="WorkflowNoneCase"/>:
/// без власного шаблону, власний = основний, власний ІНШИЙ і None лише він, власний ІНШИЙ і None лише версія проєкту
/// (тоді документ ЛИШАЄТЬСЯ з погодженням, і рядок не зникає). Періоди — 2099xx (правило 382).
/// ⛔ ДОКАЗ (локально): прибрати <c>ExceptWorkflowNone</c> у відповідному запиті — друга половина червона; завжди брати
/// <c>fn_VersionOf</c> (ігнорувати власний шаблон) — червоні <c>ownNone</c> і <c>projectNone</c>; завжди
/// <c>fn_TemplateVersionOf</c> неможливо без шаблону, тож гілку «без шаблону» ловить <c>plain</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class WorkflowNoneStoresTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 4, 1, 10, 0, 0, DateTimeKind.Utc);
    private const int Period = 209904;
    private const int Author = 5;
    private const int Approver = 6;

    [Theory]
    [InlineData(WorkflowNoneCase.Plain)]
    [InlineData(WorkflowNoneCase.SameTemplate)]
    [InlineData(WorkflowNoneCase.OwnNone)]
    [InlineData(WorkflowNoneCase.ProjectNone)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B138")]
    public async Task Черга_погодження_не_показує_поданий_аркуш_версії_без_погодження(string mode)
    {
        var s = await ArrangeAsync(mode, state => state.Submit(Author, Now)).ConfigureAwait(true);

        Assert.Contains(await SubmittedAsync(s).ConfigureAwait(true), r => r.DocumentId == s.Doc.DocumentId);

        await SwitchAsync(s).ConfigureAwait(true);

        Assert.Equal(
            !s.Binding.DocumentBecomesNone,
            (await SubmittedAsync(s).ConfigureAwait(true)).Any(r => r.DocumentId == s.Doc.DocumentId));
    }

    [Theory]
    [InlineData(WorkflowNoneCase.Plain)]
    [InlineData(WorkflowNoneCase.SameTemplate)]
    [InlineData(WorkflowNoneCase.OwnNone)]
    [InlineData(WorkflowNoneCase.ProjectNone)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B138")]
    public async Task Повернуті_мені_не_показують_відхилений_аркуш_версії_без_погодження(string mode)
    {
        var s = await ArrangeAsync(mode, state =>
        {
            state.Submit(Author, Now);
            state.Reject(Approver, "Невірні значення", Now);
        }).ConfigureAwait(true);

        Assert.Contains(await ReturnedAsync(s).ConfigureAwait(true), r => r.DocumentId == s.Doc.DocumentId);

        await SwitchAsync(s).ConfigureAwait(true);

        Assert.Equal(
            !s.Binding.DocumentBecomesNone,
            (await ReturnedAsync(s).ConfigureAwait(true)).Any(r => r.DocumentId == s.Doc.DocumentId));
    }

    [Theory]
    [InlineData(WorkflowNoneCase.Plain)]
    [InlineData(WorkflowNoneCase.SameTemplate)]
    [InlineData(WorkflowNoneCase.OwnNone)]
    [InlineData(WorkflowNoneCase.ProjectNone)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B138")]
    public async Task Перерахунок_не_бачить_станів_і_затверджених_аркушів_версії_без_погодження(string mode)
    {
        var s = await ArrangeAsync(mode, state =>
        {
            state.Submit(Author, Now);
            state.Approve(Approver, Now);
        }).ConfigureAwait(true);
        var doc = s.Doc;

        await using (var db = s.Chain.CreateContext())
        {
            var store = new WorkflowStore(db);
            Assert.Single(await store.GetSheetsAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None).ConfigureAwait(true));
            Assert.Single(await store.ListApprovedSheetsAsync(null, doc.DocumentId, doc.PeriodKey.Value, 10, CancellationToken.None)
                .ConfigureAwait(true));
        }

        await SwitchAsync(s).ConfigureAwait(true);

        var expected = s.Binding.DocumentBecomesNone ? 0 : 1;
        await using (var db = s.Chain.CreateContext())
        {
            var store = new WorkflowStore(db);
            Assert.Equal(
                expected,
                (await store.GetSheetsAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None).ConfigureAwait(true)).Count);
            Assert.Equal(
                expected,
                (await store.ListApprovedSheetsAsync(null, doc.DocumentId, doc.PeriodKey.Value, 10, CancellationToken.None)
                    .ConfigureAwait(true)).Count);
        }
    }

    /// <summary>
    /// ✎ B144 (замір): у переліках через усі проєкти фільтр додається ЛИШЕ коли версії без погодження є. Немає жодної —
    /// запит той самий об'єкт, що й до B138 (жодної функції версії на рядок стану).
    /// </summary>
    /// <remarks>⛔ ДОКАЗ (локально): прибрати ранній вихід <c>noneVersionIds.Length == 0</c> — перша перевірка червона.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B144")]
    public async Task Без_версій_без_погодження_фільтр_до_переліку_не_додається_а_з_ними_додається()
    {
        var s = await ArrangeAsync(WorkflowNoneCase.Plain, state => state.Submit(Author, Now)).ConfigureAwait(true);
        await using var db = s.Chain.CreateContext();
        var source = db.ApprovalStates.AsNoTracking();

        Assert.Same(source, source.ExceptWorkflowNone(db, []));

        var filtered = source.ExceptWorkflowNone(db, [s.Doc.TemplateVersionId]);
        Assert.NotSame(source, filtered);
        var text = filtered.ToQueryString();
        Assert.Contains("fn_VersionOf", text, StringComparison.Ordinal);
        Assert.Contains("fn_TemplateVersionOf", text, StringComparison.Ordinal);

        // Версії без погодження читаються з бази: до перемикання цієї версії серед них немає, після — є.
        Assert.DoesNotContain(s.Doc.TemplateVersionId, await db.NoneVersionIdsAsync(CancellationToken.None).ConfigureAwait(true));
        await SwitchAsync(s).ConfigureAwait(true);
        Assert.Contains(s.Doc.TemplateVersionId, await db.NoneVersionIdsAsync(CancellationToken.None).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(TestDocumentBuilder Chain, TestDocument Doc, WorkflowNoneCase.Binding Binding);

    private async Task<Scenario> ArrangeAsync(string mode, Action<ApprovalState> shape)
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await chain.BuildAsync(periodKey: Period).ConfigureAwait(false);

        await using var db = chain.CreateContext();
        var binding = await WorkflowNoneCase.BindAsync(mode, sql.ConnectionString, db, doc).ConfigureAwait(false);

        var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
        shape(state);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new Scenario(chain, doc, binding);
    }

    private static async Task SwitchAsync(Scenario s)
    {
        await using var db = s.Chain.CreateContext();
        await WorkflowNoneCase.SwitchAsync(db, s.Binding).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Ecr.Application.Ports.ApprovalQueueCandidate>> SubmittedAsync(Scenario s)
    {
        await using var db = s.Chain.CreateContext();
        return await new ApprovalQueueStore(db).ListSubmittedAsync(
            [s.Doc.ProjectId], requireIncludedSheet: false, after: null, take: 50, participatedDocumentIds: [],
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Ecr.Application.Ports.ReturnedSheetCandidate>> ReturnedAsync(Scenario s)
    {
        await using var db = s.Chain.CreateContext();
        return await new ApprovalQueueStore(db).ListReturnedAsync(
            Author, [s.Doc.ProjectId], requireIncludedSheet: false, after: null, take: 50, participatedDocumentIds: [],
            CancellationToken.None).ConfigureAwait(false);
    }
}
