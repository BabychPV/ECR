// tests/Ecr.Infrastructure.Tests/Jobs/PeriodStateJobTransactionTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Services;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Взаємовиключення <c>PeriodStateJob</c> і <c>Reopen</c> (ФВ-1.10a): читання
/// під <c>UPDLOCK</c> має бути ВСЕРЕДИНІ явної транзакції, інакше блокування не
/// тримає нічого.
/// </summary>
/// <remarks>
/// ⛔ Аудит 2026-09-16, §6.1. Коментар у задачі стверджував, що блокування
/// «тримається до кінця транзакції» — а транзакції не було зовсім:
/// <c>SaveChangesAsync</c> викликався ОДИН раз ПІСЛЯ циклу по всіх активних
/// проєктах, тож <c>UPDLOCK</c> звільнявся щойно завершувався сам
/// <c>SELECT</c>, задовго до <c>AdvanceTo</c>.
///
/// Сценарій: задача читає період о T1 і вирішує перевести в Closed. До власного
/// коміту користувач відкриває період через Reopen — бачить ще Open, дозволяє,
/// комітить. Задача потім комітить уже обчислений перехід у Closed, тихо
/// перекриваючи Reopen: жодного конфлікту, бо в <c>Period</c> немає RowVersion.
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodStateJobTransactionTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 4, 1, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.10a")]
    public async Task Читання_періодів_під_UPDLOCK_іде_всередині_транзакції()
    {
        var chain = new TestDocumentBuilder(sql.ConnectionString);
        var document = await chain.BuildAsync();

        // Задача обходить лише АКТИВНІ проєкти — інакше вона не дійшла б до
        // блокування, і тест нічого не довів би.
        await using (var seed = chain.CreateContext())
        {
            var project = await seed.Projects.SingleAsync(p => p.Id == document.ProjectId);
            project.Activate(Now);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var watcher = new UpdlockTransactionWatcher();

        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(watcher)
            .Options);

        var job = new PeriodStateJob(db, new PeriodStateCalculator(), new UnitOfWork(db), new TestClock(Now));
        await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

        Assert.True(watcher.Seen, "Запит із UPDLOCK не спостерігався — тест нічого не довів.");

        // ⛔ Головне твердження: транзакція була ВІДКРИТА в момент блокування.
        // Без `ExecuteInTransactionAsync` тут `false`, і `UPDLOCK` відпускався
        // одразу після `SELECT`.
        Assert.True(
            watcher.TransactionWasOpen,
            "UPDLOCK-читання періодів іде поза транзакцією: блокування звільняється після SELECT.");
    }

    /// <summary>
    /// Фіксує, чи була транзакція відкрита в момент виконання запиту з
    /// <c>UPDLOCK</c> по <c>doc.Period</c>.
    /// </summary>
    private sealed class UpdlockTransactionWatcher : DbCommandInterceptor
    {
        /// <summary>Чи спостерігався такий запит узагалі.</summary>
        public bool Seen { get; private set; }

        /// <summary>Чи була транзакція відкрита саме тоді.</summary>
        public bool TransactionWasOpen { get; private set; }

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command,
            CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Seen
                && command.CommandText.Contains("UPDLOCK", StringComparison.Ordinal)
                && command.CommandText.Contains("doc.Period", StringComparison.Ordinal))
            {
                Seen = true;
                TransactionWasOpen = eventData.Context?.Database.CurrentTransaction is not null;
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
