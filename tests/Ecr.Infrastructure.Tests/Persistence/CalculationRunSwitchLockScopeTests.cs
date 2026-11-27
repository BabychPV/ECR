// tests/Ecr.Infrastructure.Tests/Persistence/CalculationRunSwitchLockScopeTests.cs
using System.Data.Common;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Перемикання актуальності двох документів ОДНОГО проєкту й періоду, що йдуть одночасно, не чекають одне на одного:
/// кожне тримає U-замок на рядку свого прогону (перевірка статусу D-560) до коміту, тож UPDATE «зняти актуальність зі
/// старіших» не має права торкатися рядків чужого документа.
/// </summary>
/// <remarks>
/// <para>
/// Звідки (21.11, <c>RecalculationJobFanOutTests.Два_документи_паралельно_…</c> упав один раз у повному прогоні
/// Jobs): system_health записав дедлок 1205 у тестовій базі — обидва процеси виконували UPDATE витіснення
/// <c>SwitchCurrentRunAsync</c> і чекали U на ключах <c>PK_CalculationRun</c>, якими володів інший. План UPDATE
/// сканував кластерний індекс (обсяг таблиці в повному прогоні), і скан наштовхувався на рядок чужого прогону під
/// U-замком <c>UPDLOCK</c>. Сам тест не ретраїть (продукт ретраїть 1205 стратегією повторів, лише 3 рази).
/// </para>
/// <para>
/// Відтворення цим тестом на старому коді (system_health, 21.11 08:56): та сама пара UPDATE, ключі
/// <c>PK_CalculationRun</c> — один процес чекає U на рядку, який інший тримає U (UPDLOCK), інший — U на рядку, який
/// перший тримає X (витіснений ним старий прогін).
/// </para>
/// <para>
/// Детерміновано: обидва перемикання зустрічаються ПІСЛЯ свого <c>UPDLOCK</c> (перехоплювач команди) — далі кожне
/// робить свій UPDATE, поки інше тримає замок. Мутація: повернути UPDATE предикатом області
/// (<c>previousQuery.Where(r =&gt; r.Id &lt; calculationRunId).ExecuteUpdateAsync</c> без ключів) — випадок
/// <c>withPrevious: true</c> червоніє на 1205 щоразу (UPDATE кожного документа дочитує по ключу старий актуальний
/// прогін ІНШОГО документа, який той уже тримає X-замком витіснення). Випадок <c>false</c> — форма самого fan-out
/// (перший прогін, витісняти нічого): на старому коді червоніє лише за планом зі скануванням PK, тож він — сторож
/// шляху «UPDATE не потрібен», а не мутаційний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationRunSwitchLockScopeTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ПРД-13")]
    public async Task Перемикання_двох_документів_одного_періоду_одночасно_не_чекають_одне_на_одного(bool withPrevious)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: withPrevious ? 202607 : 202608);
        var period = document.PeriodKey.Value;

        var docA = document.DocumentId;
        long docB;
        long? oldA = null;
        long? oldB = null;
        long newA;
        long newB;

        await using (var setup = builder.CreateContext())
        {
            var other = new Document(document.ProjectId, $"SW-{Guid.NewGuid():N}"[..20], 1, T0);
            setup.Documents.Add(other);
            await setup.SaveChangesAsync();
            docB = other.Id;

            if (withPrevious)
            {
                oldA = await AddRunAsync(setup, document.ProjectId, period, docA, current: true);
                oldB = await AddRunAsync(setup, document.ProjectId, period, docB, current: true);
            }

            // Новий прогін A старіший за новий прогін B: UPDATE витіснення B (`Id < newB`) за ключовим діапазоном
            // охоплює рядок newA — саме його тримає перемикання A.
            newA = await AddRunAsync(setup, document.ProjectId, period, docA, current: false);
            newB = await AddRunAsync(setup, document.ProjectId, period, docB, current: false);
        }

        var meet = new AfterStatusLockRendezvous(parties: 2);

        var results = await Task.WhenAll(
            Task.Run(() => CompleteAsync(meet, newA)),
            Task.Run(() => CompleteAsync(meet, newB)));

        Assert.True(meet.Met, "перемикання не зустрілися після UPDLOCK — гонитву не відтворено");
        Assert.All(results, error => Assert.Null(error));

        await using var check = builder.CreateContext();
        var statuses = await check.CalculationRuns.AsNoTracking()
            .Where(r => r.ProjectId == document.ProjectId && r.PeriodKey == period)
            .ToDictionaryAsync(r => r.Id, r => r.Status);

        Assert.Equal(CalculationRun.CurrentStatus, statuses[newA]);
        Assert.Equal(CalculationRun.CurrentStatus, statuses[newB]);
        if (withPrevious)
        {
            Assert.Equal(CalculationRun.SupersededStatus, statuses[oldA!.Value]);
            Assert.Equal(CalculationRun.SupersededStatus, statuses[oldB!.Value]);
        }

        Assert.Equal(2, statuses.Values.Count(s => s == CalculationRun.CurrentStatus));
    }

    private static async Task<long> AddRunAsync(EcrDbContext db, int projectId, int period, long documentId, bool current)
    {
        var run = new CalculationRun(projectId, period, triggeredByUserId: null, T0, documentId);
        if (current)
        {
            run.Complete("Succeeded", T0.AddMinutes(1), "{}", errorMessage: null);
            run.MakeCurrent();
        }

        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>Завершення прогону тим самим шляхом, що й задача (транзакція перемикання); помилка — як значення.</summary>
    private async Task<Exception?> CompleteAsync(AfterStatusLockRendezvous meet, long runId)
    {
        try
        {
            await using var db = sql.CreateContext(meet);
            var clock = new TestClock(T0.AddHours(1));
            var handler = new RunCalculationHandler(
                Substitute.For<IPeriodStore>(),
                Substitute.For<IWorkflowStore>(),
                new CalculationResultStore(db, clock),
                Substitute.For<IBackgroundJobScheduler>(),
                new UnitOfWork(db),
                Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
                Substitute.For<ICurrentUser>(),
                clock);

            await handler.CompleteAsync(runId, new ModuleProfile(), CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// Кожне перемикання, виконавши перевірку статусу під <c>UPDLOCK</c> (замок на своєму прогоні вже взято), чекає,
    /// доки те саме зробить інше: далі обидва йдуть у UPDATE витіснення, тримаючи замки.
    /// </summary>
    private sealed class AfterStatusLockRendezvous(int parties) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int arrived;

        public bool Met => all.Task.IsCompletedSuccessfully;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrived) == parties)
                {
                    all.TrySetResult();
                }

                // Не зустрілися — тест про це скаже (`Met`), а не зависне.
                await Task.WhenAny(all.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false);
            }

            return result;
        }
    }
}
