// tests/Ecr.Infrastructure.Tests/Jobs/YearGraceArchiveGateTests.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ФВ-1.8: offset на рівні року — <c>Project.YearGraceOffsetDays</c> — стримує
/// перенесення року в архів, доки він не сплив.
/// </summary>
/// <remarks>
/// ⚠ Вимога реалізована ЧАСТКОВО, і тест фіксує саме реалізовану частину.
/// Задум (<c>reference/design/06</c> §ФВ-1.8, АРХ-1): відлік від кінця року
/// (<c>31.12 + 45</c>), протягом якого дані минулого року ще правляться, а
/// після — проєкт закривається. Код (<c>ArchiveJob.ExecuteAsync</c>) рахує від
/// <c>Project.ClosedAt</c> — моменту, коли людина позначила проєкт
/// заархівованим, — і стримує лише фізичне перенесення в <c>arc.*</c>; на право
/// редагування рік-offset не впливає (його дають межі періодів).
///
/// Значення 90, а не типові 45, узято навмисно: інакше тест не відрізнив би
/// значення проєкту від літерала.
///
/// Мутаційний доказ: в <c>ArchiveJob.ExecuteAsync</c> замінити
/// <c>project.ClosedAt?.AddDays(project.YearGraceOffsetDays)</c> на
/// <c>project.ClosedAt?.AddDays(45)</c> — на 50-й день задача вже не
/// відмовляє, і тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class YearGraceArchiveGateTests(SqlServerFixture sql)
{
    private static readonly DateTime MarkedArchived = new(2027, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Архівація_року_чекає_на_YearGraceOffsetDays_проєкту()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);

        await MarkArchivedAsync(doc.ProjectId, yearGraceOffsetDays: 90);

        // День 50: більше за типові 45, менше за 90 проєкту — ще рано.
        var early = Recorder();
        await using (var db = Context(early))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Job(db, MarkedArchived.AddDays(50)).ExecuteAsync(
                    new ArchiveRequest(doc.ProjectId, 2026), Substitute.For<IJobProgress>(), CancellationToken.None));

            Assert.Contains("2027-04-10", error.Message, StringComparison.Ordinal);
        }

        Assert.Empty(early.Matching("usp_ArchiveYear"));

        // День 91: грейс сплив — задача доходить до процедури (вона приглушена:
        // тест про ворота, а не про саме перенесення; його доводить ArchiveJobTests).
        var late = Recorder();
        await using (var db = Context(late))
        {
            await Job(db, MarkedArchived.AddDays(91)).ExecuteAsync(
                new ArchiveRequest(doc.ProjectId, 2026), Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        Assert.Single(late.Matching("usp_ArchiveYear"));
    }

    private static CommandRecorder Recorder()
        => new(suppressWhen: text => text.Contains("usp_ArchiveYear", StringComparison.Ordinal));

    private static ArchiveJob Job(EcrDbContext db, DateTime utcNow)
    {
        var capabilities = Substitute.For<ISqlCapabilities>();
        capabilities.ArchiveBatchSize.Returns(50_000);

        return new ArchiveJob(db, capabilities, new TestClock(utcNow));
    }

    private EcrDbContext Context(CommandRecorder recorder)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(recorder)
            .Options);

    /// <summary>Позначка «заархівовано» і річний грейс — сирим UPDATE (приватні сетери).</summary>
    private async Task MarkArchivedAsync(int projectId, int yearGraceOffsetDays)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE doc.Project SET Status = 4, ClosedAt = @closedAt, YearGraceOffsetDays = @grace WHERE Id = @id;";
        command.Parameters.AddWithValue("@closedAt", MarkedArchived);
        command.Parameters.AddWithValue("@grace", yearGraceOffsetDays);
        command.Parameters.AddWithValue("@id", projectId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
