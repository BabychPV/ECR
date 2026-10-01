// tests/Ecr.Infrastructure.Tests/Jobs/PeriodStateJobRetryTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Аудит 2026-09-28, B5: повтор стратегії виконання в <c>PeriodStateJob</c>
/// не має зберігати системний Reopen (D-204) без рядка аудиту.
/// </summary>
/// <remarks>
/// Справжня база, справжні <c>PeriodStateJob</c>, <c>UnitOfWork</c> і
/// <c>AuditWriter</c>. Транзієнтний збій імітується на ПЕРШІЙ спробі замикання
/// після того, як трекер уже має зміни (<c>Closed → Grace</c>): або на
/// <c>SaveChanges</c> (зміни лишились <c>Modified</c>), або на коміті ПІСЛЯ
/// <c>SaveChanges</c> (зміни вже прийняті трекером). Стратегія — тестова,
/// повторює лише цей збій; у застосунку ту саму роль грає
/// <c>EnableRetryOnFailure</c> (1205, обрив з'єднання).
/// <para>
/// ⛔ До фіксу повтор брав із <c>FromSql</c> уже відстежуваний період у
/// <c>Grace</c>: план не давав Reopen, аудит першої спроби відкотився разом із
/// транзакцією, і (a) <c>SaveChanges</c> зберігав <c>Grace</c> без аудиту, або
/// (b) нічого не зберігав зовсім. Мутація: прибрати <c>DetachPeriods</c> на
/// початку замикання → обидва випадки червоні (прогнано, опис коміту).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodStateJobRetryTests(SqlServerFixture sql)
{
    private const int November = 202611;

    private static readonly TimeZoneInfo Site = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly YearGraceWindow Window = YearGraceWindow.For(new DateOnly(2026, 12, 31), 45, Site);

    /// <summary>Де саме падає перша спроба.</summary>
    public enum FaultAt
    {
        /// <summary>На <c>SaveChanges</c> — зміни в трекері лишаються <c>Modified</c>.</summary>
        SaveChanges,

        /// <summary>На коміті після <c>SaveChanges</c> — зміни вже прийняті трекером.</summary>
        Commit,
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    [Trait("Requirement", "ФВ-1.10")]
    [InlineData(FaultAt.SaveChanges)]
    [InlineData(FaultAt.Commit)]
    public async Task Повтор_після_транзієнтного_збою_зберігає_системний_Reopen_разом_з_аудитом(FaultAt at)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(November, rowCount: 1);
        var baseline = await ScalarAsync("SELECT ISNULL(MAX(Id), 0) FROM itg.MaintenanceRun");

        try
        {
            var periodId = await ArmClosedNovemberAsync(builder, doc);
            var fault = new FirstAttemptFault(periodId, at);
            var materialization = Substitute.For<IMaterializationScheduler>();

            var error = await RunStateJobAsync(fault, SiteTime(2027, 1, 1, 10), materialization);

            Assert.True(fault.Fired, "Збій першої спроби не спрацював — тест нічого не довів.");

            var period = await PeriodAsync(builder, periodId);
            Assert.True(period.State == PeriodState.Grace, $"Після повтору листопад мав бути Grace. Збій прогону: {error}");
            Assert.Equal(Window.EndsAtUtc, period.ReopenedUntil);

            // Головне: перехід і рядок аудиту — разом, рівно один.
            Assert.Equal(1, await ScalarAsync(
                $"""
                SELECT COUNT(*) FROM aud.StructureChange
                 WHERE EntityType = N'Period' AND EntityId = {periodId} AND Operation = N'Reopen'
                   AND CorrelationId = N'{PeriodStateJob.AuditCorrelationId}'
                """));

            // Матеріалізація пропущених точок — з успішної спроби.
            await materialization.Received(1).EnqueueAfterTransitionAsync(
                doc.ProjectId,
                Arg.Is<IReadOnlyCollection<int>>(keys => keys.Contains(November)),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            await ExecuteAsync(
                "UPDATE doc.Project SET Status = @status WHERE Id = @id",
                ("@status", (byte)ProjectStatus.Archived), ("@id", doc.ProjectId));
            await ExecuteAsync(
                "DELETE FROM itg.MaintenanceRun WHERE Id > @id AND JobCode = @code",
                ("@id", baseline), ("@code", PeriodStateJob.Code));
        }
    }

    // ── Підготовка ──────────────────────────────────────────────────────

    private static DateTime SiteTime(int year, int month, int day, int hour)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified), Site);

    /// <summary>Активний проєкт; листопад закритий за короткою політикою до кінця року (як у <c>YearGraceSystemReopenWiringTests</c>).</summary>
    private async Task<int> ArmClosedNovemberAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        var policy = new PeriodPolicy(
            EcrCode.Create($"YR{Guid.NewGuid():N}"[..14].ToUpperInvariant()), openOffsetDays: 0, graceOffsetDays: 15,
            hardCloseOffsetDays: 30, yearGraceOffsetDays: 45);

        await using var db = builder.CreateContext();
        db.PeriodPolicies.Add(policy);

        var project = await db.Projects.SingleAsync(p => p.Id == doc.ProjectId);
        project.Activate(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == November);
        period.RecomputeBoundaries(policy, Site);
        Assert.True(period.ComputedCloseAt < Window.YearEndUtc, "Політика не закриває листопад до 31.12 — тест нічого не доводить.");

        period.AdvanceTo(PeriodState.Closed, period.ComputedCloseAt);
        await db.SaveChangesAsync();

        await ExecuteAsync(
            "UPDATE doc.Project SET PeriodPolicyId = @policy WHERE Id = @id",
            ("@policy", policy.Id), ("@id", doc.ProjectId));

        return period.Id;
    }

    /// <summary>Прогін задачі на контексті з тестовою стратегією повторів і збоєм першої спроби.</summary>
    private async Task<string?> RunStateJobAsync(
        FirstAttemptFault fault, DateTime at, IMaterializationScheduler materialization)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o =>
            {
                o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");
                o.ExecutionStrategy(d => new RetryOnTestFault(d));
            })
            .AddInterceptors(fault)
            .Options);

        try
        {
            await new PeriodStateJob(
                    db, new PeriodStateCalculator(), new UnitOfWork(db), new TestClock(at),
                    materialization, logger: null, audit: new AuditWriter(db))
                .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Чужі активні проєкти спільної бази можуть упасти самі по собі — текстом.
            return ex.ToString();
        }
    }

    private static async Task<Period> PeriodAsync(TestDocumentBuilder builder, int periodId)
    {
        await using var db = builder.CreateContext();
        return await db.Periods.AsNoTracking().SingleAsync(p => p.Id == periodId);
    }

    private async Task<long> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sqlText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    // ── Імітація транзієнтного збою ─────────────────────────────────────

    /// <summary>Збій, який тестова стратегія вважає транзієнтним.</summary>
    private sealed class TransientTestFault() : Exception("Імітований транзієнтний збій (B5).");

    /// <summary>Стратегія, що повторює лише <see cref="TransientTestFault"/>.</summary>
    private sealed class RetryOnTestFault(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestFault;
    }

    /// <summary>
    /// Кидає <see cref="TransientTestFault"/> один раз: на збереженні, у якому
    /// трекер несе змінений цільовий період, або на коміті одразу після нього.
    /// </summary>
    private sealed class FirstAttemptFault(int periodId, FaultAt at) : SaveChangesInterceptor, IDbTransactionInterceptor
    {
        private bool _targetSaved;

        /// <summary>Чи спрацював збій.</summary>
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var carriesTarget = eventData.Context!.ChangeTracker.Entries<Period>()
                .Any(e => e.Entity.Id == periodId && e.State == EntityState.Modified);

            if (!Fired && carriesTarget)
            {
                if (at == FaultAt.SaveChanges)
                {
                    Fired = true;
                    throw new TransientTestFault();
                }

                _targetSaved = true;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && _targetSaved)
            {
                Fired = true;
                throw new TransientTestFault();
            }

            return ValueTask.FromResult(result);
        }
    }
}
