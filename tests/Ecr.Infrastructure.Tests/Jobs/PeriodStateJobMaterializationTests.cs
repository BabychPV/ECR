// tests/Ecr.Infrastructure.Tests/Jobs/PeriodStateJobMaterializationTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L2-06 (аудит 2026-10-03): збій постановки матеріалізації з переходу періоду
/// не губить її назавжди — повтор задачі станів переходу вже не бачить.
/// </summary>
/// <remarks>
/// ⚠ Моменти прогонів і архівування проєкту в <c>finally</c> — як у
/// <see cref="PeriodStateJobResilienceTests"/>: задача обходить усі активні
/// проєкти спільної бази. Мутаційні докази — в описі коміту.
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodStateJobMaterializationTests(SqlServerFixture sql)
{
    /// <summary>Прогін задачі станів; 20 лютого 06:00 UTC.</summary>
    private static readonly DateTime StateRun = new(2026, 2, 20, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L2-06")]
    public async Task Збій_постановки_після_коміту_повторюється_і_не_губить_її()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        try
        {
            await ArmAsync(builder, chain);
            var scheduler = new FlakyMaterialization(failures: 1, enlists: false);

            await using (var db = builder.CreateContext())
            {
                await RunAsync(db, scheduler);
            }

            Assert.NotEqual(PeriodState.Scheduled, await StateAsync(builder, chain));
            Assert.Equal(2, scheduler.Calls.Count);
            Assert.Contains(chain.PeriodKey.Value, scheduler.Delivered);
        }
        finally
        {
            await RetireAsync(builder, chain);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L2-06")]
    public async Task Черга_в_базі_збій_постановки_відкочує_перехід_і_повтор_ставить_її()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        try
        {
            await ArmAsync(builder, chain);

            // Перший прогін: постановка падає всередині транзакції переходу.
            var broken = new FlakyMaterialization(failures: int.MaxValue, enlists: true);
            await using (var db = builder.CreateContext())
            {
                await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(db, broken));
            }

            Assert.Equal(PeriodState.Scheduled, await StateAsync(builder, chain));

            // Повтор задачі бачить перехід знову — і ставить матеріалізацію в його ж транзакції.
            var healthy = new FlakyMaterialization(failures: 0, enlists: true);
            await using (var db = builder.CreateContext())
            {
                await RunAsync(db, healthy);
            }

            Assert.NotEqual(PeriodState.Scheduled, await StateAsync(builder, chain));
            Assert.Contains(chain.PeriodKey.Value, healthy.Delivered);
            Assert.All(healthy.Calls.Where(c => c.Keys.Contains(chain.PeriodKey.Value)), c => Assert.True(c.InTransaction));
        }
        finally
        {
            await RetireAsync(builder, chain);
        }
    }

    private static Task RunAsync(EcrDbContext db, FlakyMaterialization materialization)
    {
        materialization.Context = db;
        return new PeriodStateJob(
                db, new PeriodStateCalculator(), new UnitOfWork(db), new TestClock(StateRun), materialization)
            {
                MaterializationRetryDelay = _ => TimeSpan.FromMilliseconds(10),
            }
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
    }

    /// <summary>Межі за політикою і активний проєкт: на <see cref="StateRun"/> — перехід з <c>Scheduled</c>.</summary>
    private static async Task ArmAsync(TestDocumentBuilder builder, TestDocument chain)
    {
        await using var db = builder.CreateContext();
        var project = await db.Projects.SingleAsync(p => p.Id == chain.ProjectId);
        project.Activate(StateRun.AddDays(-30));

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.RecomputeBoundaries(
            new PeriodPolicy(EcrCode.Create("MAT05"), openOffsetDays: 1, graceOffsetDays: 5,
                hardCloseOffsetDays: 40, yearGraceOffsetDays: 5),
            SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());

        await db.SaveChangesAsync(CancellationToken.None);
        Assert.Equal(PeriodState.Scheduled, period.State);
    }

    private static async Task<PeriodState> StateAsync(TestDocumentBuilder builder, TestDocument chain)
    {
        await using var db = builder.CreateContext();
        return await db.Periods
            .Where(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value)
            .Select(p => p.State)
            .SingleAsync();
    }

    /// <summary>Архівує проєкт тесту: задача станів обходить лише активні.</summary>
    private static async Task RetireAsync(TestDocumentBuilder builder, TestDocument chain)
    {
        await using var db = builder.CreateContext();
        var project = await db.Projects.SingleAsync(p => p.Id == chain.ProjectId);
        if (project.Status == ProjectStatus.Active)
        {
            project.Archive(StateRun.AddDays(30));
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Постановка, що перші <c>failures</c> викликів з непорожнім переліком кидає
    /// транзієнтну помилку і запам'ятовує, чи була відкрита транзакція <see cref="Context"/>.
    /// </summary>
    private sealed class FlakyMaterialization(int failures, bool enlists) : IMaterializationScheduler
    {
        private int failed;

        public bool EnlistsInCallerTransaction => enlists;

        public List<(int[] Keys, bool InTransaction)> Calls { get; } = [];

        public List<int> Delivered { get; } = [];

        public EcrDbContext? Context { get; set; }

        public Task EnqueueAfterTransitionAsync(int projectId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
        {
            if (periodKeys.Count == 0)
            {
                return Task.CompletedTask;
            }

            Calls.Add(([.. periodKeys], Context?.Database.CurrentTransaction is not null));
            if (failed < failures)
            {
                failed++;
                throw new TimeoutException("test: транзієнтна відмова постановки");
            }

            Delivered.AddRange(periodKeys);
            return Task.CompletedTask;
        }
    }
}
