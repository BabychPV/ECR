// tests/Ecr.Api.Tests/Health/RecalculationWorkerProbeTests.cs
using Ecr.Api.Health;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// I2-2: SQL перевірки <c>worker</c> на справжній <c>itg.JobProgress</c> —
/// застряглою вважає лише <c>Queued</c> лейну перерахунку, доступну довше за
/// межу, а живою орендою — лише <c>Running</c> цього лейну з майбутньою
/// <c>LeaseUntil</c>.
/// </summary>
/// <remarks>
/// ⚠ База спільна: лічильники глобальні, тож перевіряється ПРИРІСТ від власних
/// рядків, а не абсолютне число. Мутації (прогнано): (1) прибрати умову
/// <c>AvailableAt &lt;= …</c> → свіжа задача рахується застряглою, червоний;
/// (2) прибрати умову лейну → задача лейну default рахується, червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationWorkerProbeTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застій_і_живі_оренди_рахуються_лише_в_лейні_перерахунку()
    {
        var prefix = $"probe-{Guid.NewGuid():N}";
        var stallAfter = RecalculationWorkerHealthCheck.StallAfter;

        await using var db = sql.CreateContext();
        var probe = new RecalculationWorkerProbe(db);

        try
        {
            var before = await probe.ReadQueueAsync(stallAfter, CancellationToken.None);

            // Не рахуються: свіжа задача, стара задача ІНШОГО лейну, прострочена оренда.
            await InsertAsync(db, $"{prefix}-fresh", JobLanes.Recalc, "Queued", availableMinutesAgo: 1, leaseMinutesAhead: null);
            await InsertAsync(db, $"{prefix}-default", JobLanes.Default, "Queued", availableMinutesAgo: 60, leaseMinutesAhead: null);
            await InsertAsync(db, $"{prefix}-expired", JobLanes.Recalc, "Running", availableMinutesAgo: 60, leaseMinutesAhead: -10);

            var noise = await probe.ReadQueueAsync(stallAfter, CancellationToken.None);
            Assert.Equal(before, noise);

            // Рахуються: стара задача лейну перерахунку і жива оренда в ньому.
            await InsertAsync(db, $"{prefix}-stalled", JobLanes.Recalc, "Queued", availableMinutesAgo: 60, leaseMinutesAhead: null);
            await InsertAsync(db, $"{prefix}-live", JobLanes.Recalc, "Running", availableMinutesAgo: 60, leaseMinutesAhead: 60);

            var after = await probe.ReadQueueAsync(stallAfter, CancellationToken.None);
            Assert.Equal(before.Stalled + 1, after.Stalled);
            Assert.Equal(before.LiveLeases + 1, after.LiveLeases);
        }
        finally
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM itg.JobProgress WHERE JobId LIKE {prefix + "%"}");
        }
    }

    private static Task<int> InsertAsync(
        Infrastructure.Persistence.EcrDbContext db, string jobId, string lane, string state,
        int availableMinutesAgo, int? leaseMinutesAhead)
    {
        // ⚠ null-параметр SqlClient типізує як nvarchar, а DATEADD його не приймає:
        // ознака й число окремо.
        var hasLease = leaseMinutesAhead is null ? 0 : 1;
        var lease = leaseMinutesAhead ?? 0;
        return db.Database.ExecuteSqlAsync($"""
            INSERT INTO itg.JobProgress (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, CreatedAt,
                                         Lane, AvailableAt, LeaseUntil, ReclaimCount)
            VALUES ({jobId}, N'Ecr.Test.ProbeJob', {state}, 0, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(),
                    {lane}, DATEADD(minute, -{availableMinutesAgo}, SYSUTCDATETIME()),
                    CASE WHEN {hasLease} = 0 THEN NULL ELSE DATEADD(minute, {lease}, SYSUTCDATETIME()) END,
                    0)
            """);
    }
}
