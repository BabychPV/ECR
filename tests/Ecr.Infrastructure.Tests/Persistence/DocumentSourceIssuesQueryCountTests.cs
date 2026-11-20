// tests/Ecr.Infrastructure.Tests/Persistence/DocumentSourceIssuesQueryCountTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B80 N5, F13 (продуктивність): <see cref="DocumentSourceIssues.ListAsync"/> раніше викликав
/// <see cref="DocumentSourceMonthGate.ConsumerBindingsAsync"/> на КОЖЕН період документа (запит документа + запит зв'язок —
/// 2 на період). Тепер зв'язки всіх періодів читаються двома запитами поза циклом: кількість звернень до БД не росте з
/// кількістю періодів (панель проблем розрахунку річного документа показує 12 місяців).
/// </summary>
/// <remarks>
/// Мутація: повернути виклик <c>ConsumerBindingsAsync</c> усередину циклу по періодах → 12 періодів дають на 22+
/// звернень більше, ніж 1 — тест червоніє.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B80")]
public sealed class DocumentSourceIssuesQueryCountTests(SqlServerFixture sql)
{
    private const int Year = 2099;

    [Fact]
    public async Task Кількість_звернень_до_БД_не_росте_з_кількістю_періодів_документа()
    {
        var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);

        // Реальний документ має періоди проєкту в doc.Period: заводимо 12 місяців року.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        foreach (var month in Enumerable.Range(1, 12))
        {
            await builder.AddPeriodAsync(world.Land, new Ecr.Domain.ValueObjects.PeriodKey((Year * 100) + month));
        }

        var one = await CountAsync(world.Land.DocumentId, [(Year * 100) + 3]);
        var twelve = await CountAsync(world.Land.DocumentId, [.. Enumerable.Range(1, 12).Select(m => (Year * 100) + m)]);

        Assert.True(
            twelve.Total <= one.Total,
            $"12 періодів: {twelve.Total} звернень, 1 період: {one.Total} — вартість росте з кількістю періодів.\n"
            + $"12: {twelve.Format()}\n1: {one.Format()}");
    }

    /// <summary>Поведінка не змінилась: кожен період із розбіжністю названо окремо (місяць джерела не затверджено).</summary>
    [Fact]
    public async Task Усі_дванадцять_місяців_названо_кожен_за_своїм_періодом()
    {
        var world = await SourceIssueWorld.ArrangeAsync(sql, DocumentSourceState.ApprovedOnly);

        await using var db = sql.CreateContext();
        var issues = await new DocumentSourceMonthGate(db, readOverride: null, DependencyGraphTestKit.Provider(db))
            .ListSourceIssuesAsync(
                world.Land.DocumentId, [.. Enumerable.Range(1, 12).Select(m => (Year * 100) + m)], CancellationToken.None);

        Assert.Equal(
            Enumerable.Range(1, 12).Select(m => (Year * 100) + m).ToArray(),
            issues.Select(i => i.PeriodKey).ToArray());
        Assert.All(issues, i => Assert.Equal(world.Source.DocumentId, i.SourceDocumentId));
    }

    private async Task<CommandTallySnapshot> CountAsync(long documentId, int[] periods)
    {
        var counter = new DbCommandCounter();
        await using var db = sql.CreateContext(counter);
        var issues = new DocumentSourceIssues(db, readOverride: null, DependencyGraphTestKit.Provider(db));
        counter.Tally.Reset();
        await issues.ListAsync(documentId, periods, CancellationToken.None);
        return counter.Tally.Snapshot();
    }
}
