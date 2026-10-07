// tests/Ecr.Infrastructure.Tests/Jobs/PeriodStateJobResilienceTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <c>PeriodStateJob</c> після зміни політики періоду і на «битому» проєкті:
/// зворотний перехід пропускається ВИДИМО, а збій одного проєкту не зупиняє
/// решту.
/// </summary>
/// <remarks>
/// ⛔ Дефект: адміністратор подовжив пільговий строк, межі перераховано, і
/// за новими межами період у <c>Grace</c> «мав би» бути <c>Open</c>. План
/// пропускав зворотний перехід лише з <c>Closed</c>; <c>Grace → Open</c>
/// потрапляв у план, <c>Period.TransitionTo</c> кидав <c>ECR-PRD-0409</c>, і
/// задача падала на цьому проєкті щогодини — разом з усіма проєктами, що
/// йшли в обході після нього.
///
/// ⚠ <c>PeriodStateJob</c> обходить УСІ активні проєкти спільної тестової
/// бази. Тому моменти прогонів — ті самі, що в <c>MaterializeOnPeriodOpenTests</c>
/// (20 лютого 2026), свої проєкти архівуються у <c>finally</c>, а знахідки
/// шукаються за кодом СВОГО проєкту, не «останній рядок».
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodStateJobResilienceTests(SqlServerFixture sql)
{
    /// <summary>Прогін задачі станів; 20 лютого 06:00 UTC.</summary>
    private static readonly DateTime StateRun = new(2026, 2, 20, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>Січень: відкриття 2 січня, пільга з 5 лютого, закриття 12 березня.</summary>
    private static PeriodPolicy ShortGrace()
        => new(EcrCode.Create("GRC05"), openOffsetDays: 1, graceOffsetDays: 5,
            hardCloseOffsetDays: 40, yearGraceOffsetDays: 5);

    /// <summary>Та сама, але пільговий строк подовжено: <c>Grace</c> лише з 25 лютого.</summary>
    private static PeriodPolicy ExtendedGrace()
        => new(EcrCode.Create("GRC25"), openOffsetDays: 1, graceOffsetDays: 25,
            hardCloseOffsetDays: 40, yearGraceOffsetDays: 25);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Зміна_політики_не_валить_задачу_стан_лишається_а_пропуск_видно()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var baseline = await LastRunIdAsync(builder);

        try
        {
            await using (var seed = builder.CreateContext())
            {
                await ArmPeriodAsync(seed, chain, ShortGrace());
            }

            await using (var first = builder.CreateContext())
            {
                await RunAsync(first, StateRun, new ListLogger());
            }

            Assert.Equal(PeriodState.Grace, await StateAsync(builder, chain));

            // Адміністратор подовжив пільговий строк — межі перераховано.
            await using (var edit = builder.CreateContext())
            {
                var project = await edit.Projects.SingleAsync(p => p.Id == chain.ProjectId);
                var period = await PeriodAsync(edit, chain);
                period.RecomputeBoundaries(
                    ExtendedGrace(), SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());
                await edit.SaveChangesAsync(CancellationToken.None);
            }

            var next = StateRun.AddHours(1);
            var logger = new ListLogger();
            string code;

            // ⛔ МУТАЦІЙНИЙ ДОКАЗ: повернути в `PeriodStateCalculator.PlanTransitions`
            // перевірку «лише з Closed» → `Grace → Open` у плані, задача кидає
            // `ECR-PRD-0409` на цьому проєкті (і без ізоляції — на всіх після нього).
            await using (var db = builder.CreateContext())
            {
                code = (await db.Projects.SingleAsync(p => p.Id == chain.ProjectId)).Code;
                await RunAsync(db, next, logger);
            }

            // Стан не змінено: назад — лише ручним Reopen.
            Assert.Equal(PeriodState.Grace, await StateAsync(builder, chain));

            // Журнал — Warning з періодом, напрямком і причиною.
            var warning = Assert.Single(
                logger.Entries,
                e => e.Level == LogLevel.Warning && e.Message.Contains(code, StringComparison.Ordinal));
            Assert.Contains(chain.PeriodKey.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), warning.Message, StringComparison.Ordinal);
            Assert.Contains("Grace → Open", warning.Message, StringComparison.Ordinal);
            Assert.Contains(SkippedPeriodTransition.Reason, warning.Message, StringComparison.Ordinal);

            // Адміністратор бачить це у зведенні: рядок журналу обслуговування
            // `Degraded` (зведення бере `Status != "Succeeded"`).
            var run = Assert.Single(await RunsMentioningAsync(builder, baseline, code));
            Assert.Equal("Degraded", run.Status);
            Assert.Equal(next, run.FinishedAt);
            Assert.Contains(
                $"\"period\":{chain.PeriodKey.Value},\"from\":\"Grace\",\"to\":\"Open\"",
                run.DetailsJson!,
                StringComparison.Ordinal);
            Assert.Contains("Reopen", run.DetailsJson!, StringComparison.Ordinal);

            // Повторний прогін тієї ж доби — журнал пише знову, а зведення не
            // засмічується однаковим рядком щогодини.
            await using (var again = builder.CreateContext())
            {
                await RunAsync(again, next.AddHours(1), new ListLogger());
            }

            Assert.Single(await RunsMentioningAsync(builder, baseline, code));
            Assert.Equal(PeriodState.Grace, await StateAsync(builder, chain));
        }
        finally
        {
            await RetireAsync(builder, chain);
            await ForgetRunsAsync(builder, baseline);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.12")]
    public async Task Збій_одного_проєкту_не_зупиняє_решту_і_видимий()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // ⚠ «Битий» проєкт заводиться ПЕРШИМ: обхід іде за ключем, тож без
        // ізоляції виняток на ньому зупинив би прогін до здорового.
        var broken = await builder.BuildAsync(ct: CancellationToken.None);
        var healthy = await builder.BuildAsync(ct: CancellationToken.None);
        Assert.True(broken.ProjectId < healthy.ProjectId);

        var baseline = await LastRunIdAsync(builder);

        try
        {
            string brokenCode;
            await using (var seed = builder.CreateContext())
            {
                await ArmPeriodAsync(seed, broken, ShortGrace());
                await ArmPeriodAsync(seed, healthy, ShortGrace());
                brokenCode = (await seed.Projects.SingleAsync(p => p.Id == broken.ProjectId)).Code;
            }

            var logger = new ListLogger();

            await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(new FailPeriodSave(broken.ProjectId))
                .Options))
            {
                // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати try/catch навколо проєкту в
                // `PeriodStateJob.ExecuteAsync` → здоровий лишається `Scheduled`.
                var error = await Record.ExceptionAsync(() => RunAsync(db, StateRun, logger));

                // Збій не проковтнуто: виняток іде нагору (→ `Failed` у /jobs).
                Assert.NotNull(error);
                Assert.Contains(
                    FailPeriodSave.Message,
                    error is AggregateException many
                        ? string.Join("|", many.InnerExceptions.Select(e => e.Message))
                        : error.Message,
                    StringComparison.Ordinal);
            }

            // Здоровий проєкт оброблено, «битий» — відкотився цілим.
            Assert.Equal(PeriodState.Grace, await StateAsync(builder, healthy));
            Assert.Equal(PeriodState.Scheduled, await StateAsync(builder, broken));

            Assert.Contains(
                logger.Entries,
                e => e.Level == LogLevel.Error
                     && e.Message.Contains(brokenCode, StringComparison.Ordinal)
                     && e.Exception?.Message == FailPeriodSave.Message);

            var run = Assert.Single(await RunsMentioningAsync(builder, baseline, brokenCode));
            Assert.Equal("Failed", run.Status);
            // SEC (TIER2): текст винятку в зведення не йде — лише відсилка до журналу.
            Assert.DoesNotContain(FailPeriodSave.Message, run.DetailsJson!, StringComparison.Ordinal);
            Assert.Contains("server log", run.DetailsJson!, StringComparison.Ordinal);
        }
        finally
        {
            await RetireAsync(builder, broken);
            await RetireAsync(builder, healthy);
            await ForgetRunsAsync(builder, baseline);
        }
    }

    private static Task RunAsync(EcrDbContext db, DateTime at, ILogger<PeriodStateJob> logger)
        => new PeriodStateJob(
                db, new PeriodStateCalculator(), new UnitOfWork(db), new TestClock(at),
                Substitute.For<IMaterializationScheduler>(), logger)
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

    /// <summary>Межі за політикою і активний проєкт — щоб задача станів його бачила.</summary>
    private static async Task ArmPeriodAsync(EcrDbContext db, TestDocument chain, PeriodPolicy policy)
    {
        var project = await db.Projects.SingleAsync(p => p.Id == chain.ProjectId);
        project.Activate(StateRun.AddDays(-30));

        var period = await PeriodAsync(db, chain);
        period.RecomputeBoundaries(policy, SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());

        await db.SaveChangesAsync(CancellationToken.None);
        Assert.Equal(PeriodState.Scheduled, period.State);
    }

    private static Task<Period> PeriodAsync(EcrDbContext db, TestDocument chain)
        => db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);

    private static async Task<PeriodState> StateAsync(TestDocumentBuilder builder, TestDocument chain)
    {
        await using var db = builder.CreateContext();
        return await db.Periods
            .Where(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value)
            .Select(p => p.State)
            .SingleAsync();
    }

    private static async Task<long> LastRunIdAsync(TestDocumentBuilder builder)
    {
        await using var db = builder.CreateContext();
        return await db.MaintenanceRuns.Select(r => (long?)r.Id).MaxAsync() ?? 0;
    }

    /// <summary>Рядки задачі станів після <paramref name="baseline"/>, що згадують проєкт.</summary>
    private static async Task<List<Ecr.Domain.Entities.Integration.MaintenanceRun>> RunsMentioningAsync(
        TestDocumentBuilder builder, long baseline, string projectCode)
    {
        await using var db = builder.CreateContext();
        var needle = $"\"project\":\"{projectCode}\"";
        return await db.MaintenanceRuns
            .AsNoTracking()
            .Where(r => r.Id > baseline && r.JobCode == PeriodStateJob.Code
                        && r.DetailsJson != null && r.DetailsJson.Contains(needle))
            .OrderBy(r => r.Id)
            .ToListAsync();
    }

    /// <summary>Прибирає рядки задачі станів, написані за час тесту.</summary>
    private static async Task ForgetRunsAsync(TestDocumentBuilder builder, long baseline)
    {
        await using var db = builder.CreateContext();
        await db.MaintenanceRuns
            .Where(r => r.Id > baseline && r.JobCode == PeriodStateJob.Code)
            .ExecuteDeleteAsync(CancellationToken.None);
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

    /// <summary>Валить збереження змінених періодів одного проєкту — імітація збою коміту.</summary>
    private sealed class FailPeriodSave(int projectId) : SaveChangesInterceptor
    {
        public const string Message = "test: збій збереження переходу періоду (ізоляція)";

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var hit = eventData.Context?.ChangeTracker.Entries<Period>()
                .Any(e => e.State == EntityState.Modified && e.Entity.ProjectId == projectId) ?? false;

            return hit
                ? throw new InvalidOperationException(Message)
                : base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Журнал, що зберігає записи для перевірки.</summary>
    private sealed class ListLogger : ILogger<PeriodStateJob>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
