// tests/Ecr.Infrastructure.Tests/Jobs/AbandonedWorkSweeperPurgeLoopTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// J1-05: ретенція <c>itg.JobProgress</c> видаляє пакет за пакетом, доки є кандидати, а не один
/// пакет на прохід (≤ 120 тис. рядків на добу при потоці задач понад це).
/// </summary>
/// <remarks>
/// ⚠ Сховище прогресу — підміна: тест перевіряє цикл проходу, а не SQL видалення (його
/// покривають тести <c>JobProgressStore</c>). Решта проходу йде справжньою базою; момент — 2031
/// рік, як у <see cref="AbandonedWorkSweeperTests"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class AbandonedWorkSweeperPurgeLoopTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2031, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "J1-05")]
    public async Task Ретенція_прибирає_більше_за_один_пакет_за_прохід()
    {
        var progress = Substitute.For<IJobProgressStore>();
        progress
            .PurgeFinishedAsync(Arg.Any<DateTime>(), AbandonedWorkSweeper.PurgeBatch, Arg.Any<CancellationToken>())
            .Returns(AbandonedWorkSweeper.PurgeBatch, AbandonedWorkSweeper.PurgeBatch, 2_000, 0);

        await using var db = sql.CreateContext();
        var outcome = await new AbandonedWorkSweeper(db, progress)
            .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, Now, purge: true, CancellationToken.None);

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути один виклик `PurgeFinishedAsync` на прохід — Purged = 5 000,
        // викликів 1. Неповний пакет (2 000) — кінець кандидатів: четвертого виклику немає.
        Assert.Equal(2 * AbandonedWorkSweeper.PurgeBatch + 2_000, outcome.Purged);
        await progress.Received(3)
            .PurgeFinishedAsync(Arg.Any<DateTime>(), AbandonedWorkSweeper.PurgeBatch, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "J1-05")]
    public async Task Без_ретенції_нічого_не_видаляється()
    {
        var progress = Substitute.For<IJobProgressStore>();

        await using var db = sql.CreateContext();
        var outcome = await new AbandonedWorkSweeper(db, progress)
            .SweepAsync(AbandonedWorkSweeper.AbandonedJobReason, Now, purge: false, CancellationToken.None);

        Assert.Equal(0, outcome.Purged);
        await progress.DidNotReceive()
            .PurgeFinishedAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
