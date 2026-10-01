// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationFanOutStatusTests.cs
using Ecr.Application.Common;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// P4 ФВ-9.8: батьківська задача перерахунку проєкту лише РОЗКЛАДАЄ документні
/// задачі й лишається <c>Succeeded</c>; оператор бачить похідний стан
/// («розкладено» → «виконано» / «з помилками») із дочірніх рядків черги.
/// </summary>
/// <remarks>
/// Мутація: <c>GetJobStatusHandler</c> без <c>FanOut</c>/<c>EffectiveState</c> (повертає лише
/// збережений <c>Succeeded</c>) — тести стану червоні.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class RecalculationFanOutStatusTests(SqlServerFixture sql)
{
    private const int UserId = 5;

    [Fact]
    public async Task Одразу_після_батька_стан_Розкладено_і_виконано_нуль()
    {
        var parent = await ArrangeAsync(queued: 3, running: 0, succeeded: 0, failed: 0);

        var status = await StatusAsync(parent);

        Assert.Equal("Succeeded", status.State);
        Assert.Equal(new FanOutStatus(3, 3, 0, 0, 0), status.FanOut);
        Assert.Equal(FanOutStatus.StateFannedOut, status.EffectiveState);
    }

    [Fact]
    public async Task Частково_виконано_лишається_Розкладено_з_лічильниками()
    {
        var parent = await ArrangeAsync(queued: 1, running: 1, succeeded: 1, failed: 0);

        var status = await StatusAsync(parent);

        Assert.Equal(new FanOutStatus(3, 1, 1, 1, 0), status.FanOut);
        Assert.Equal(FanOutStatus.StateFannedOut, status.EffectiveState);
    }

    [Fact]
    public async Task Усі_виконано_дає_Виконано()
    {
        var parent = await ArrangeAsync(queued: 0, running: 0, succeeded: 3, failed: 0);

        var status = await StatusAsync(parent);

        Assert.Equal(FanOutStatus.StateSucceeded, status.EffectiveState);
    }

    [Fact]
    public async Task Один_Failed_дає_Виконано_з_помилками_і_скасована_рахується_помилкою()
    {
        var parent = await ArrangeAsync(queued: 0, running: 0, succeeded: 2, failed: 1);
        await InsertChildAsync(parent, "Cancelled", 9);

        var status = await StatusAsync(parent);

        Assert.Equal(new FanOutStatus(4, 0, 0, 2, 2), status.FanOut);
        Assert.Equal(FanOutStatus.StateSucceededWithErrors, status.EffectiveState);
    }

    [Fact]
    public async Task Діти_іншого_батька_не_рахуються_і_повторний_розклад_не_подвоює_лічильник()
    {
        var parent = await ArrangeAsync(queued: 2, running: 0, succeeded: 0, failed: 0);
        var other = await ArrangeAsync(queued: 0, running: 0, succeeded: 5, failed: 1);

        // Повторний фан-аут (коалесценція) поглинає постановку: нових рядків цього
        // батька немає, старі лишаються — лічильник не змінюється.
        var status = await StatusAsync(parent);

        Assert.Equal(new FanOutStatus(2, 2, 0, 0, 0), status.FanOut);

        var otherStatus = await StatusAsync(other);
        Assert.Equal(new FanOutStatus(6, 0, 0, 5, 1), otherStatus.FanOut);
    }

    [Fact]
    public async Task Задача_без_дочірніх_не_отримує_похідного_стану()
    {
        var parent = await ArrangeAsync(queued: 0, running: 0, succeeded: 0, failed: 0);

        var status = await StatusAsync(parent);

        Assert.Null(status.FanOut);
        Assert.Null(status.EffectiveState);
    }

    [Fact]
    public async Task Провалений_батько_не_маскується_дочірніми()
    {
        var parent = await ArrangeAsync(queued: 0, running: 0, succeeded: 2, failed: 0, parentState: "Failed");

        var status = await StatusAsync(parent);

        Assert.Equal("Failed", status.State);
        Assert.Null(status.FanOut);
    }

    private async Task<JobStatus> StatusAsync(string parentJobId)
    {
        await using var db = sql.CreateContext();
        var store = new JobProgressStore(db);

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.GetStatusAsync(parentJobId, Arg.Any<CancellationToken>())
            .Returns(_ => store.FindAsync(parentJobId, CancellationToken.None)!);
        jobs.GetCreatedByUserIdAsync(parentJobId, Arg.Any<CancellationToken>()).Returns(UserId);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        user.Language.Returns("en");

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }.Build());

        var handler = new GetJobStatusHandler(jobs, access, user, new FakeUiStringCatalog(), store);

        return (await handler.HandleAsync(parentJobId, CancellationToken.None))!;
    }

    private async Task<string> ArrangeAsync(int queued, int running, int succeeded, int failed, string parentState = "Succeeded")
    {
        var parent = $"IRecalculationJob-{Guid.NewGuid():N}";

        await ExecAsync($"""
            INSERT INTO itg.JobProgress (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt)
            VALUES (N'{parent}', N'IRecalculationJob', '{parentState}', 100, SYSUTCDATETIME(), SYSUTCDATETIME(),
                    SYSUTCDATETIME(), SYSUTCDATETIME());
            """);

        foreach (var (state, count, offset) in new[]
                 { ("Queued", queued, 0), ("Running", running, 100), ("Succeeded", succeeded, 200), ("Failed", failed, 300) })
        {
            for (var i = 0; i < count; i++)
            {
                await InsertChildAsync(parent, state, offset + i);
            }
        }

        return parent;
    }

    private Task InsertChildAsync(string parent, string state, int n)
        => ExecAsync($$"""
            INSERT INTO itg.JobProgress
                (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt,
                 Lane, Payload, AvailableAt, TargetKey)
            VALUES (N'IRecalculationJob-{{Guid.NewGuid():N}}', N'IRecalculationJob', '{{state}}', 0,
                    SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(),
                    'default', N'{"projectId":1,"documentId":{{n}},"fanOutParentJobId":"{{parent}}"}',
                    SYSUTCDATETIME(), N'IRecalculationJob~doc{{n}}-{{Guid.NewGuid():N}}-year');
            """);

    private async Task ExecAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(text, connection);
        await command.ExecuteNonQueryAsync();
    }
}
