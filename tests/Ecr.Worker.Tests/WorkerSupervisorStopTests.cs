// tests/Ecr.Worker.Tests/WorkerSupervisorStopTests.cs

using System.Collections.Concurrent;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Ecr.Worker.Isolation;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Ecr.Worker.Tests.WorkerProcess;

namespace Ecr.Worker.Tests;

/// <summary>
/// L2-09: зупинка наглядача дає справжньому дочірньому процесу штатно повернути задачу
/// в чергу — рядок <c>Queued</c>, <c>ReclaimCount = 0</c>, а не <c>Running</c> до спливу оренди.
/// </summary>
/// <remarks>
/// ⚠ Окремий клас у колекції <c>SqlServer</c>, а не метод <see cref="WorkerSupervisorTests"/>: тому
/// потрібна тестова база, а клас може бути лише в одній колекції. Доповнює
/// <see cref="WorkerSupervisorTests.Зупинка_наглядача_дає_дочірньому_зупинитися_штатно_до_закриття_Job_Object"/>
/// (там — заглушка і код виходу 0; тут — справжній <c>--child</c> і рядок черги).
/// <para>
/// Детермінізм: задачу перерахунку утримує в <c>Running</c> блокування таблиці
/// <c>calc.CalculationRun</c> з боку тесту — перший же запис прогону дочірнього чекає на ньому, доки
/// наглядача не зупинено. Жодних очікувань «задача вже пішла»: вона не може завершитися до зупинки.
/// Мутація: прибрати <c>stop.Set()</c> у <c>WorkerSupervisor.RunAsync</c> — діти гинуть від
/// закриття Job Object, рядок лишається <c>Running</c>, тест червоний.
/// </para>
/// Тест лише для Windows (Job Object): на інших ОС повертається одразу — відмову платформи
/// перевіряють тести <see cref="WorkerSupervisorTests"/>. Доказ — CI-завдання <c>worker (windows)</c>, окремий
/// крок із LocalDB (<c>worker-windows-l2-09.trx</c>, Y6-03): до R7 тест там відкидав фільтр
/// <c>Category!=Integration</c>, і «20/20» прогону не мав.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "L2-09")]
public sealed class WorkerSupervisorStopTests(SqlServerFixture sql) : ChildWorkerTestsBase(sql)
{
    private const string ConnectionVariable = "ECR_ConnectionStrings__Ecr";

    [Fact]
    public async Task Зупинка_наглядача_повертає_задачу_дочірнього_в_чергу_без_переклейму()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var document = await DocumentAsync();
        var supervisor = new WorkerSupervisor(
            new WorkerPoolOptions { Count = 1, MemoryLimitMb = 512, JobMemoryLimitMb = 1024 },
            Command("--child"),
            NullLogger<WorkerSupervisor>.Instance,
            shutdownGrace: TimeSpan.FromSeconds(25));

        var exits = new ConcurrentQueue<int>();
        var pids = new ConcurrentQueue<int>();
        supervisor.ChildStarted += (_, e) => pids.Enqueue(e.ProcessId);
        supervisor.ChildExited += (_, e) => exits.Enqueue(e.ExitCode!.Value);

        // Рядок підключення дочірнього — з оточення наглядача (D-11), як у службі.
        var previous = Environment.GetEnvironmentVariable(ConnectionVariable);
        Environment.SetEnvironmentVariable(ConnectionVariable, Sql.ConnectionString);

        using var cancellation = new CancellationTokenSource();
        await using var blocker = await TableBlocker.HoldAsync(Sql.ConnectionString);
        Task? run = null;
        string jobId;
        try
        {
            jobId = await EnqueueAsync<IRecalculationJob>(RecalcOf(document));
            run = supervisor.RunAsync(cancellation.Token);

            var running = await WaitForStateAsync(jobId, "Running");
            Assert.Contains($"/{Ecr.Infrastructure.Persistence.JobProgressStore.RoleWorker}/", running.InstanceId, StringComparison.Ordinal);

            // Дати дочірньому дійти до першого запису прогону: далі він стоїть на блокуванні.
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal("Running", (await RowAsync(jobId))!.State);

            await cancellation.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            await cancellation.CancelAsync();
            if (run is not null)
            {
                try
                {
                    await run.WaitAsync(TimeSpan.FromSeconds(60));
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                {
                    // Наглядач не зупинився вчасно: нижче діти все одно прибираються за PID.
                }
            }

            foreach (var pid in pids)
            {
                Kill(pid);
            }

            Environment.SetEnvironmentVariable(ConnectionVariable, previous);
        }

        var row = (await RowAsync(jobId))!;

        // ⛔ Без сигналу дочірній гине від закриття Job Object посеред задачі: рядок стоїть Running
        // до спливу оренди й переклеймлюється з ReclaimCount + 1.
        Assert.Equal("Queued", row.State);
        Assert.Equal(0, row.ReclaimCount ?? 0);

        // Повернення при зупинці — не провал і не відкладення: спробу не зараховано, відліку стелі немає.
        Assert.Equal(0, row.Attempt ?? 0);
        Assert.DoesNotContain(JobDeferral.PayloadProperty, row.Payload ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(row.ClaimToken);
        Assert.Equal(0, Assert.Single(exits));
    }

    /// <summary>Транзакція тесту, що тримає <c>TABLOCKX</c> на <c>calc.CalculationRun</c> до <c>Dispose</c>.</summary>
    private sealed class TableBlocker : IAsyncDisposable
    {
        private readonly SqlConnection connection;
        private readonly SqlTransaction transaction;

        private TableBlocker(SqlConnection connection, SqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
        }

        public static async Task<TableBlocker> HoldAsync(string connectionString)
        {
            var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM calc.CalculationRun WITH (TABLOCKX, HOLDLOCK);";
            await command.ExecuteScalarAsync();
            return new TableBlocker(connection, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
