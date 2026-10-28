// src/Ecr.Infrastructure/Persistence/SupersededResultsPurger.cs
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Спільне ядро прибирання рядків прогонів, що перестали бути актуальними (Q-17, ADR «Deleting results of
/// superseded runs»): швидкий шлях після перемикання (<see cref="CalculationResultStore.PurgeSupersededRunsAsync"/>) і
/// нічна задача (<c>SupersededResultsSweepJob</c>).
/// </summary>
/// <remarks>
/// ⛔ Інваріант: рядки <c>calc.CalculationResult</c>, <c>calc.CalculationStep</c>, <c>calc.CalculationInput</c>
/// існують лише для прогонів <c>Current</c>/<c>Running</c>. Самі рядки <c>calc.CalculationRun</c> не видаляються.
/// <para>
/// ⚠ Форма пакета: keyset по кластерному ключу <c>(PeriodKey, Id)</c>. Спершу верхня межа
/// <c>@hi = MAX(Id)</c> серед перших <c>@batch</c> кандидатів після <c>@last</c>, потім <c>DELETE</c> у вікні
/// <c>(@last, @hi]</c>. Простий <c>DELETE TOP</c> без межі щоразу перечитував би лишені рядки з початку партиції.
/// Кожен пакет — окрема коротка транзакція (autocommit); <c>DEADLOCK_PRIORITY LOW</c>, <c>LOCK_TIMEOUT</c> 5 с,
/// жертва взаємного блокування або таймауту повторюється 3 рази.
/// </para>
/// </remarks>
internal sealed partial class SupersededResultsPurger(
    EcrDbContext db, SupersededPurgeOptions options, ILogger logger)
{
    /// <summary>Таблиці під правилом, у порядку видалення; перша — результати.</summary>
    internal const string ResultTable = "calc.CalculationResult";

    internal const string StepTable = "calc.CalculationStep";

    internal const string InputTable = "calc.CalculationInput";

    private const int DeadlockVictimError = 1205;

    private const int LockTimeoutError = 1222;

    private const int Attempts = 3;

    /// <summary>Статуси, чиї рядки не чіпає ніколи: актуальний і той, що ще рахується.</summary>
    internal const string KeepStatusesSql = "N'Current', N'Running'";

    /// <summary>Таблиці, які зачіпає правило, з урахуванням <see cref="SupersededPurgeOptions.IncludeTrace"/>.</summary>
    internal IReadOnlyList<string> Tables
        => options.IncludeTrace ? [ResultTable, StepTable, InputTable] : [ResultTable];

    /// <summary>Лічильник пакетів, що спільно витрачається кількома таблицями й партиціями одного проходу.</summary>
    internal sealed class Budget(int batches)
    {
        /// <summary>Скільки пакетів лишилося.</summary>
        public int Remaining { get; set; } = batches;

        /// <summary>Скільки пакетів виконано.</summary>
        public int Done { get; set; }

        /// <summary>Чи проходу вказано зупинитися (журнал, скасування користувачем тощо).</summary>
        public bool Stopped { get; set; }

        /// <summary>Причина зупинки.</summary>
        public string? StopReason { get; set; }
    }

    /// <summary>
    /// Швидкий шлях: прибирає рядки щойно замінених прогонів. Повертає скільки видалено.
    /// </summary>
    /// <param name="runs">Прогони з перемикання.</param>
    /// <param name="ct">Скасування.</param>
    internal async Task<int> PurgeRunsAsync(IReadOnlyList<SupersededRun> runs, CancellationToken ct)
    {
        var total = 0;
        var budget = new Budget(options.MaxBatchesPerRun);

        await using var scope = await ConnectionScope.OpenAsync(db, ct).ConfigureAwait(false);

        foreach (var group in runs.GroupBy(r => (r.PeriodKey, r.DocumentId)))
        {
            var runIds = group.Select(r => r.RunId).Distinct().Order().ToList();

            foreach (var table in Tables)
            {
                if (budget.Remaining <= 0)
                {
                    return total;
                }

                var parameters = new List<SqlParameter>(runIds.Count + 1);
                var ids = new StringBuilder();
                for (var i = 0; i < runIds.Count; i++)
                {
                    ids.Append(i == 0 ? "@r" : ",@r").Append(i.ToString(CultureInfo.InvariantCulture));
                    parameters.Add(new SqlParameter("@r" + i.ToString(CultureInfo.InvariantCulture), SqlDbType.BigInt)
                    {
                        Value = runIds[i],
                    });
                }

                // ⚠ Для результатів документного прогону - ще DocumentId: seek по IX_CalculationResult_DocRun
                // (PeriodKey, DocumentId, CalculationRunId). Кроки й входи цього індексу не мають - кластерне вікно.
                var where = new StringBuilder($"t.CalculationRunId IN ({ids})");
                if (group.Key.DocumentId is { } documentId && table == ResultTable)
                {
                    where.Append(" AND t.DocumentId = @doc");
                    parameters.Add(new SqlParameter("@doc", SqlDbType.BigInt) { Value = documentId });
                }

                // Страховка: навіть якщо виклик помилково передав актуальний прогін, його рядки не видаляються.
                where.Append(
                    $" AND NOT EXISTS (SELECT 1 FROM calc.CalculationRun AS ru WHERE ru.Id = t.CalculationRunId AND ru.Status IN ({KeepStatusesSql}))");

                total += (int)await DeleteKeysetAsync(
                        table, group.Key.PeriodKey, where.ToString(), parameters, budget, betweenBatches: null, ct)
                    .ConfigureAwait(false);
            }
        }

        return total;
    }

    /// <summary>
    /// Крок нічного проходу для однієї таблиці й партиції: рядки, чий прогін не <c>Current</c>/<c>Running</c>.
    /// </summary>
    internal Task<long> DeleteNonCurrentAsync(
        string table, int periodKey, Budget budget, Func<CancellationToken, Task<bool>>? betweenBatches,
        CancellationToken ct)
        => DeleteKeysetAsync(
            table, periodKey, NonCurrentWhere, parameters: [], budget, betweenBatches, ct);

    /// <summary>Умова «прогін рядка не актуальний і не працює».</summary>
    internal const string NonCurrentWhere =
        $"EXISTS (SELECT 1 FROM calc.CalculationRun AS ru WHERE ru.Id = t.CalculationRunId AND ru.Status NOT IN ({KeepStatusesSql}))";

    /// <summary>Скільки рядків таблиці в партиції належить неактуальним прогонам (для dry-run).</summary>
    internal async Task<long> CountNonCurrentAsync(string table, int periodKey, CancellationToken ct)
    {
        await using var scope = await ConnectionScope.OpenAsync(db, ct).ConfigureAwait(false);
        await using var command = CreateCommand(
            $"SELECT COUNT_BIG(*) FROM {table} AS t WHERE t.PeriodKey = @p AND {NonCurrentWhere};",
            [new SqlParameter("@p", SqlDbType.Int) { Value = periodKey }]);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private async Task<long> DeleteKeysetAsync(
        string table, int periodKey, string where, List<SqlParameter> parameters, Budget budget,
        Func<CancellationToken, Task<bool>>? betweenBatches, CancellationToken ct)
    {
        long deleted = 0;
        long last = 0;
        var sql = $"""
            SET DEADLOCK_PRIORITY LOW;
            SET LOCK_TIMEOUT 5000;
            DECLARE @hi bigint = (SELECT MAX(x.Id)
                                    FROM (SELECT TOP (@batch) t.Id FROM {table} AS t
                                           WHERE t.PeriodKey = @p AND t.Id > @last AND {where}
                                           ORDER BY t.Id) AS x);
            DECLARE @n int = 0;
            IF @hi IS NOT NULL
            BEGIN
                DELETE t FROM {table} AS t
                 WHERE t.PeriodKey = @p AND t.Id > @last AND t.Id <= @hi AND {where};
                SET @n = @@ROWCOUNT;
            END
            SELECT @hi AS Hi, @n AS Deleted;
            """;

        while (budget.Remaining > 0 && !budget.Stopped)
        {
            long? hi;
            int batchDeleted;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var all = new List<SqlParameter>(parameters.Count + 3)
                    {
                        new("@batch", SqlDbType.Int) { Value = options.BatchSize },
                        new("@p", SqlDbType.Int) { Value = periodKey },
                        new("@last", SqlDbType.BigInt) { Value = last },
                    };
                    all.AddRange(parameters.Select(Clone));

                    await using var command = CreateCommand(sql, all);
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await reader.ReadAsync(ct).ConfigureAwait(false);
                    hi = await reader.IsDBNullAsync(0, ct).ConfigureAwait(false) ? null : reader.GetInt64(0);
                    batchDeleted = reader.GetInt32(1);
                    break;
                }
                catch (SqlException ex)
                    when ((ex.Number is DeadlockVictimError or LockTimeoutError) && attempt < Attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
                }
                finally
                {
                    await ResetSessionAsync().ConfigureAwait(false);
                }
            }

            if (hi is null)
            {
                return deleted;
            }

            deleted += batchDeleted;
            last = hi.Value;
            budget.Remaining--;
            budget.Done++;
            LogBatch(logger, table, periodKey, batchDeleted);

            if (betweenBatches is not null && !await betweenBatches(ct).ConfigureAwait(false))
            {
                budget.Stopped = true;
                return deleted;
            }

            if (options.PauseBetweenBatches > TimeSpan.Zero)
            {
                await Task.Delay(options.PauseBetweenBatches, ct).ConfigureAwait(false);
            }
        }

        return deleted;
    }

    private static SqlParameter Clone(SqlParameter p)
        => new(p.ParameterName, p.SqlDbType) { Value = p.Value };

    private async Task ResetSessionAsync()
    {
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SET DEADLOCK_PRIORITY NORMAL; SET LOCK_TIMEOUT -1;";
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // З'єднання зламане - пул скине його сам; маскувати початкову помилку не можна.
        }
    }

    /// <summary>Команда на з'єднанні контексту, у його поточній транзакції (якщо є).</summary>
    internal DbCommand CreateCommand(string sql, List<SqlParameter> parameters)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 600;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        return command;
    }

    /// <summary>Відкриває з'єднання контексту на час проходу, лише якщо воно ще закрите.</summary>
    internal sealed class ConnectionScope : IAsyncDisposable
    {
        private readonly EcrDbContext db;
        private readonly bool opened;

        private ConnectionScope(EcrDbContext db, bool opened)
        {
            this.db = db;
            this.opened = opened;
        }

        public static async Task<ConnectionScope> OpenAsync(EcrDbContext db, CancellationToken ct)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State == ConnectionState.Open)
            {
                return new ConnectionScope(db, opened: false);
            }

            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            return new ConnectionScope(db, opened: true);
        }

        public async ValueTask DisposeAsync()
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прибирання рядків неактуальних прогонів: {Table}, період {PeriodKey} - пакет видалив {Deleted}.")]
    private static partial void LogBatch(ILogger logger, string table, int periodKey, int deleted);
}
