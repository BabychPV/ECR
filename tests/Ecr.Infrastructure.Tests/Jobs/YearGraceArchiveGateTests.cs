// tests/Ecr.Infrastructure.Tests/Jobs/YearGraceArchiveGateTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ФВ-1.8 / АРХ-1: фізична архівація року в <c>arc.*</c> — не раніше кінця
/// річного вікна, відлік якого йде від <b>31.12 року проєкту</b>
/// (<c>Project.PeriodEnd</c>), а не від моменту позначки «заархівовано».
/// </summary>
/// <remarks>
/// Три точки:
/// <list type="bullet">
/// <item>день 51 після 31.12 (більше за типові 45, менше за 90 проєкту) —
/// відмова;</item>
/// <item>останній момент вікна — відмова;</item>
/// <item>перший момент після вікна — пропуск, хоча позначку «заархівовано»
/// поставлено ПІЗНО (<c>ClosedAt</c> + 90 днів ще попереду).</item>
/// </list>
/// Мутаційні докази (перевірено 2026-09-28, червоний → зелений після повернення):
/// в <c>ArchiveJob.ExecuteAsync</c> (1) ворота від
/// <c>project.ClosedAt?.AddDays(project.YearGraceOffsetDays)</c> (стара
/// формула) → червоний; (2) <c>YearGraceWindow.For(project.PeriodEnd, 45, …)</c>
/// замість значення проєкту → червоний. Мутацію «кінець вікна без останньої
/// доби» доведено в домені (<c>YearGracePeriodStateTests</c>), тут окремо не
/// ганялась.
/// Значення 90, а не типові 45, — щоб тест відрізняв значення проєкту від літерала.
/// </remarks>
[Collection("SqlServer")]
public sealed class YearGraceArchiveGateTests(SqlServerFixture sql)
{
    /// <summary>Позначку поставлено пізно — через 80 днів після кінця року.</summary>
    private static readonly DateTime MarkedArchived = new(2027, 3, 21, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Архівація_року_чекає_кінця_вікна_від_31_12_а_не_від_ClosedAt()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(periodKey: 202601, rowCount: 1, ct: CancellationToken.None);

        var original = await ReadProjectAsync(doc.ProjectId);

        try
        {
            await SetProjectAsync(doc.ProjectId, status: 4, closedAt: MarkedArchived, yearGraceOffsetDays: 90);

            // Кінець вікна рахується в поясі проєкту: 31.12.2026 + 90 діб →
            // останній день 31.03.2027, зачиняється опівночі 01.04.2027 за
            // майданчиком. Пояс — з тієї ж бази, що й у задачі; тест — про
            // відлік, а не про таблиці поясів ОС.
            var zone = SiteTimeZone.Create(original.TimeZoneId).ToTimeZoneInfo();
            var windowEnd = TimeZoneInfo.ConvertTimeToUtc(
                new DateTime(2027, 4, 1, 0, 0, 0, DateTimeKind.Unspecified), zone);
            var day51 = TimeZoneInfo.ConvertTimeToUtc(
                new DateTime(2027, 2, 20, 12, 0, 0, DateTimeKind.Unspecified), zone);

            foreach (var early in new[] { day51, windowEnd.AddSeconds(-1) })
            {
                var recorder = Recorder();
                await using (var db = Context(recorder))
                {
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => Job(db, early).ExecuteAsync(
                            new ArchiveRequest(doc.ProjectId, 2026), Substitute.For<IJobProgress>(), CancellationToken.None));

                    Assert.Contains("2027-03-31", error.Message, StringComparison.Ordinal);
                }

                Assert.Empty(recorder.Matching("usp_ArchiveYear"));
            }

            // Вікно зачинилось — задача доходить до процедури (вона приглушена:
            // тест про ворота, а не про перенесення; його доводить ArchiveJobTests).
            // ⚠ Від ClosedAt (21.03) + 90 до кінця ще ~80 днів: стара формула
            // тут відмовила б.
            var late = Recorder();
            await using (var db = Context(late))
            {
                await Job(db, windowEnd).ExecuteAsync(
                    new ArchiveRequest(doc.ProjectId, 2026), Substitute.For<IJobProgress>(), CancellationToken.None);
            }

            Assert.Single(late.Matching("usp_ArchiveYear"));
        }
        finally
        {
            // ⚠ База спільна для колекції: проєкт повертається в той стан, у
            // якому його віддав будівельник, — інакше задачі, що обходять УСІ
            // проєкти, бачили б чужий «заархівований».
            await SetProjectAsync(
                doc.ProjectId, original.Status, original.ClosedAt, original.YearGraceOffsetDays);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-1.8")]
    public void Вікно_архівації_і_вікно_станів_періоду_одне_й_те_саме()
    {
        // Задача рахує ворота тим самим `YearGraceWindow`, що й калькулятор
        // станів: для типового проєкту 2026 (+45) останній день — 14.02.2027,
        // як у прикладі reference/design/06 §ФВ-1.8.
        var window = YearGraceWindow.For(new DateOnly(2026, 12, 31), 45, TimeZoneInfo.Utc);

        Assert.Equal(new DateOnly(2027, 2, 14), window.LastDay);
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), window.YearEndUtc);
        Assert.Equal(new DateTime(2027, 2, 15, 0, 0, 0, DateTimeKind.Utc), window.EndsAtUtc);
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

    private async Task<ProjectRow> ReadProjectAsync(int projectId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Status, ClosedAt, YearGraceOffsetDays, TimeZoneId FROM doc.Project WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", projectId);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return new ProjectRow(
            Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? null : reader.GetDateTime(1),
            reader.GetInt32(2),
            reader.GetString(3));
    }

    /// <summary>Стан, позначка й річний грейс — сирим UPDATE (приватні сетери).</summary>
    private async Task SetProjectAsync(int projectId, int status, DateTime? closedAt, int yearGraceOffsetDays)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE doc.Project SET Status = @status, ClosedAt = @closedAt, YearGraceOffsetDays = @grace WHERE Id = @id;";
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@closedAt", (object?)closedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("@grace", yearGraceOffsetDays);
        command.Parameters.AddWithValue("@id", projectId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private sealed record ProjectRow(int Status, DateTime? ClosedAt, int YearGraceOffsetDays, string TimeZoneId);
}
