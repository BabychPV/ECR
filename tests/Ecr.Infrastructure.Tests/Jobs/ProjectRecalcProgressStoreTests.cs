using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// UAT recalc-progress (08.11): прогрес перерахунку проєкту в типовому режимі Quartz і перелік задач за проєктом,
/// батьком розкладу та видом — на справжній базі.
/// </summary>
/// <remarks>
/// ⛔ Слово людини: «як мені зрозуміти що перерахунок завершен?». Тост «finished» з'являвся за секунду після
/// розкладу, поки 92 документи ще рахувались: дзеркала Quartz дочірніх задач не несли батька, і
/// <c>GetFanOutAsync</c> (лише рядки черги в базі) не бачив жодної дитини.
/// <para>
/// Мутаційні докази: (1) прибрати <c>entry.TagFanOutParent</c> у <c>JobProgressStore.QueueAsync</c> — перший тест
/// червоніє (<c>FanOut</c> = null); (2) повернути <c>Lane IS NOT NULL</c> у <c>GetFanOutAsync</c> — теж перший;
/// (3) прибрати гілку <c>ProjectId</c> у <c>ListRecentAsync</c> — другий; (4) прибрати <c>HideChildren</c> —
/// третій.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ProjectRecalcProgressStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string RecalcCode = JobListKinds.RecalculationJobCode;

    /// <summary>Проєкт поза діапазоном сіду й інших тестів: перелік за ним бачить лише рядки цього тесту.</summary>
    private readonly int _projectId = 700_000 + Random.Shared.Next(0, 99_999);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];

    private string ParentId => $"IRecalculationJob~project{_projectId}-year~{_tag}";

    private string ChildId(int n) => $"IRecalculationJob~doc{n}{_tag}-year~{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Дзеркала_Quartz_дітей_рахуються_батьком_і_завершено_лише_коли_всі_діти_завершились()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        await store.QueueAsync(ParentId, RecalcCode, Now, CancellationToken.None, createdByUserId: 5);
        await store.FinishAsync(ParentId, "Succeeded", null, Now.AddSeconds(1), CancellationToken.None);

        for (var n = 1; n <= 3; n++)
        {
            await store.QueueAsync(
                ChildId(n), RecalcCode, Now.AddSeconds(1), CancellationToken.None, createdByUserId: 5,
                documentId: 9_000_000 + n, fanOutParentJobId: ParentId);
        }

        var queued = await store.GetFanOutAsync(ParentId, CancellationToken.None);

        Assert.NotNull(queued);
        Assert.Equal(3, queued.Total);
        Assert.Equal(3, queued.Queued);
        Assert.Equal(FanOutStatus.StateFannedOut, queued.EffectiveStateOf("Succeeded"));

        // Двоє виконано (один пропустив 2 затверджені аркуші), один провалився.
        await store.StartAsync(ChildId(1), RecalcCode, Now.AddSeconds(2), CancellationToken.None);
        await store.ReportAsync(
            ChildId(1), 100,
            JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
                "jobs.recalcSheetLog",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["skipped"] = "2", ["recalculatedSubmitted"] = "0" })),
            Now.AddSeconds(3), CancellationToken.None);
        await store.FinishAsync(ChildId(1), "Succeeded", null, Now.AddSeconds(3), CancellationToken.None);
        await store.FinishAsync(ChildId(2), "Succeeded", null, Now.AddSeconds(4), CancellationToken.None);

        var partial = await store.GetFanOutAsync(ParentId, CancellationToken.None);
        Assert.NotNull(partial);
        Assert.Equal(FanOutStatus.StateFannedOut, partial.EffectiveStateOf("Succeeded"));

        await store.FinishAsync(ChildId(3), "Failed", "boom", Now.AddSeconds(5), CancellationToken.None, "ECR-SYS-0500");

        var done = await store.GetFanOutAsync(ParentId, CancellationToken.None);

        Assert.NotNull(done);
        Assert.Equal(2, done.Succeeded);
        Assert.Equal(1, done.Failed);
        Assert.Equal(2, done.SkippedSheets);
        Assert.Equal(FanOutStatus.StateSucceededWithErrors, done.EffectiveStateOf("Succeeded"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_за_проєктом_і_видом_знаходить_перерахунок_проєкту_з_кодом_і_лічильниками()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        await store.QueueAsync(ParentId, RecalcCode, Now, CancellationToken.None, createdByUserId: 5);
        await store.FinishAsync(ParentId, "Succeeded", null, Now.AddSeconds(1), CancellationToken.None);
        await store.QueueAsync(
            ChildId(1), RecalcCode, Now.AddSeconds(1), CancellationToken.None, createdByUserId: 5,
            documentId: 9_100_001, fanOutParentJobId: ParentId);

        // Чужий проєкт — не в переліку.
        var other = $"IRecalculationJob~project{_projectId + 1}-year~{_tag}";
        await store.QueueAsync(other, RecalcCode, Now, CancellationToken.None, createdByUserId: 5);

        var list = await store.ListRecentAsync(
            new JobListFilter(
                CreatedByUserId: 5, ProjectId: _projectId, Kind: JobListKinds.ProjectRecalculation),
            10, CancellationToken.None);

        var parent = Assert.Single(list);
        Assert.Equal(ParentId, parent.JobId);
        Assert.Equal(_projectId, parent.ProjectId);
        Assert.NotNull(parent.FanOut);
        Assert.Equal(1, parent.FanOut.Total);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_дітей_перелік_показує_батька_одним_рядком_а_за_батьком_лише_його_дітей()
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);
        var author = 800_000 + Random.Shared.Next(0, 99_999);

        await store.QueueAsync(ParentId, RecalcCode, Now, CancellationToken.None, createdByUserId: author);
        for (var n = 1; n <= 4; n++)
        {
            await store.QueueAsync(
                ChildId(n), RecalcCode, Now.AddSeconds(n), CancellationToken.None, createdByUserId: author,
                documentId: 9_200_000 + n, fanOutParentJobId: ParentId);
        }

        var top = await store.ListRecentAsync(
            new JobListFilter(CreatedByUserId: author, HideChildren: true), 20, CancellationToken.None);
        var children = await store.ListRecentAsync(
            new JobListFilter(CreatedByUserId: author, ParentJobId: ParentId), 20, CancellationToken.None);

        Assert.Equal([ParentId], top.Select(j => j.JobId));
        Assert.Equal(4, children.Count);
        Assert.All(children, c => Assert.Equal(ParentId, c.FanOutParentJobId));
    }
}
