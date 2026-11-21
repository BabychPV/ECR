// src/Ecr.Infrastructure/Persistence/SupersededResultsPurger.cs
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Спільне ядро прибирання прогонів, що перестали бути актуальними (Q-17; <c>D-560</c> / PSS-01): швидкий шлях після
/// перемикання (<see cref="CalculationResultStore.PurgeSupersededRunsAsync"/>) і нічна задача
/// (<c>SupersededResultsSweepJob</c>).
/// </summary>
/// <remarks>
/// ⛔ Інваріант (<c>D-560</c>): для «документ × період» лишається лише ОСТАННІЙ ланцюг — актуальний (<c>Current</c>)
/// прогін зі своїми результатами, кроками й входами, і прогони, що ще рахуються (<c>Running</c>). Прогін, що
/// підлягає прибиранню (<see cref="EligibleSql"/>), зникає ПОВНІСТЮ: рядки <c>calc.CalculationResult</c>,
/// <c>calc.CalculationStep</c>, <c>calc.CalculationInput</c> і сам рядок <c>calc.CalculationRun</c>.
/// <para>
/// ⛔ <c>Current</c> і <c>Running</c> не чіпаються НІКОЛИ: умова <see cref="EligibleSql"/> стоїть у КОЖНОМУ операторі
/// <c>DELETE</c> (атомарно з ним), а не лише у виборі кандидатів, тож прогін, що став актуальним між вибором і
/// видаленням, уціліє. Новий прогін стає актуальним лише на успіху (одна транзакція перемикання), тож період ніколи
/// не лишається без результату — і під час перерахунку теж.
/// </para>
/// <para>
/// ⚠ Дві форми пакета. (1) Основна — ЗА ПРОГОНОМ: <c>DELETE TOP (@batch) … WHERE CalculationRunId = @run</c> по
/// індексу з провідним <c>CalculationRunId</c> (<c>IX_CalculationResult_Run</c>, <c>IX_CalculationStep_Run</c>,
/// <c>IX_CalculationInput_Run</c>, <c>07-partition-tables.sql</c>), далі рядок прогону. Ті самі індекси роблять
/// дешевою перевірку FK при видаленні рядка прогону: без них SQL Server сканував би всю
/// <c>calc.CalculationStep</c> на кожен прогін блокувальним читанням. (2) Запасна — keyset по кластерному ключу
/// <c>(PeriodKey, Id)</c>, лише рядки (рядок прогону лишається), коли індексів немає (база без повторного
/// <c>07-partition-tables.sql</c>). Кожен пакет — окрема коротка транзакція (autocommit); <c>DEADLOCK_PRIORITY LOW</c>,
/// <c>LOCK_TIMEOUT</c> 5 с, жертва взаємного блокування або таймауту повторюється 3 рази.
/// </para>
/// </remarks>
internal sealed partial class SupersededResultsPurger(
    EcrDbContext db, SupersededPurgeOptions options, IClock clock, ILogger logger)
{
    /// <summary>Таблиці під правилом, у порядку видалення; перша — результати.</summary>
    internal const string ResultTable = "calc.CalculationResult";

    internal const string StepTable = "calc.CalculationStep";

    internal const string InputTable = "calc.CalculationInput";

    /// <summary>Рядки самих прогонів (ключ підсумку видалень).</summary>
    internal const string RunTable = "calc.CalculationRun";

    private const int DeadlockVictimError = 1205;

    private const int LockTimeoutError = 1222;

    private const int ForeignKeyViolationError = 547;

    private const int Attempts = 3;

    /// <summary>Статуси, чиї рядки не чіпає ніколи: актуальний і той, що ще рахується.</summary>
    internal const string KeepStatusesSql = "N'Current', N'Running'";

    /// <summary>Параметр умови <see cref="EligibleSql"/>: старт <c>Failed</c>-прогону раніше за цю мить.</summary>
    internal const string FailedBeforeParameter = "@failedBefore";

    /// <summary>
    /// Умова «прогін <paramref name="alias"/> підлягає прибиранню» (<c>D-560</c>). Параметр
    /// <see cref="FailedBeforeParameter"/>.
    /// </summary>
    /// <param name="alias">Псевдонім <c>calc.CalculationRun</c> у запиті.</param>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>Superseded</c> — завжди: стан кінцевий (назад у <c>Current</c> не повертається) і за побудовою старіший
    /// за актуальний — його витіснив новіший прогін або новіший уже був актуальним (відмова «старіший за актуальний»).</item>
    /// <item><c>Failed</c> — лише коли (а) для того самого «документ × період» є НОВІШИЙ <c>Current</c> за будь-якою
    /// віссю: того самого документа (рівно, з <c>NULL</c>) або прогін усього проєкту (<c>DocumentId IS NULL</c>), що
    /// покриває документ; і (б) прогін стартував раніше за <see cref="SupersededPurgeOptions.FailedRunGrace"/>.
    /// Актуальним такий прогін уже не стане: <c>SwitchCurrentRunAsync</c> перечитує статус під <c>UPDLOCK</c> і відмовляє
    /// всьому, що не <c>Running</c>. (б) — додаткова страховка. <c>Failed</c> без новішого <c>Current</c> (остання
    /// спроба; або актуального немає зовсім) лишається — це єдина діагностика ланцюга.</item>
    /// <item><c>Current</c>, <c>Running</c> і будь-який інший статус — ніколи.</item>
    /// </list>
    /// </remarks>
    internal static string EligibleSql(string alias) => $"""
        ({alias}.Status = N'Superseded'
         OR ({alias}.Status = N'Failed'
             AND {alias}.StartedAt < {FailedBeforeParameter}
             AND EXISTS (SELECT 1 FROM calc.CalculationRun AS cu
                          WHERE cu.Status = N'Current'
                            AND cu.ProjectId = {alias}.ProjectId
                            AND cu.Id > {alias}.Id
                            AND (cu.PeriodKey = {alias}.PeriodKey OR (cu.PeriodKey IS NULL AND {alias}.PeriodKey IS NULL))
                            AND (cu.DocumentId = {alias}.DocumentId OR cu.DocumentId IS NULL))))
        """;

    /// <summary>Таблиці, які зачіпає правило, з урахуванням <see cref="SupersededPurgeOptions.IncludeTrace"/>.</summary>
    internal IReadOnlyList<string> Tables
        => options.IncludeTrace ? [ResultTable, StepTable, InputTable] : [ResultTable];

    /// <summary>Чи видаляється й рядок прогону: лише разом із кроками й входами (на нього посилаються FK).</summary>
    internal bool DeletesRunRows => options.IncludeTrace;

    /// <summary>Лічильник пакетів, що спільно витрачається кількома таблицями й прогонами одного проходу.</summary>
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

        /// <summary>Чи можна виконати ще один пакет.</summary>
        public bool CanContinue => Remaining > 0 && !Stopped;
    }

    /// <summary>Прогін-кандидат нічного проходу.</summary>
    /// <param name="RunId">Прогін.</param>
    /// <param name="PeriodKey">Період; <c>0</c> — прогін без періоду.</param>
    /// <param name="Status">Статус на момент вибору.</param>
    internal sealed record EligibleRun(long RunId, int PeriodKey, string Status);

    /// <summary>Що прибрано в одного прогону.</summary>
    internal sealed class RunPurge
    {
        /// <summary>Видалено рядків за таблицями.</summary>
        public Dictionary<string, long> Deleted { get; } = new(StringComparer.Ordinal);

        /// <summary>Чи видалено сам рядок <c>calc.CalculationRun</c>.</summary>
        public bool RunDeleted { get; set; }

        /// <summary>Чи прогін доїдено (інакше бюджет/стоп обірвав посередині; наступний прохід продовжить).</summary>
        public bool Complete { get; set; }

        /// <summary>Скільки рядків видалено всього (без рядка прогону).</summary>
        public long Rows => Deleted.Values.Sum();
    }

    /// <summary>
    /// Чи є індекси з провідним <c>CalculationRunId</c> на всіх трьох таблицях (<c>07-partition-tables.sql</c>).
    /// </summary>
    internal async Task<bool> RunIndexesPresentAsync(CancellationToken ct)
    {
        await using var scope = await ConnectionScope.OpenAsync(db, ct).ConfigureAwait(false);
        await using var command = CreateCommand(
            """
            SELECT COUNT(DISTINCT i.object_id)
              FROM sys.indexes AS i
              JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
              JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE i.object_id IN (OBJECT_ID(N'calc.CalculationResult'), OBJECT_ID(N'calc.CalculationStep'),
                                   OBJECT_ID(N'calc.CalculationInput'))
               AND c.name = N'CalculationRunId' AND i.is_disabled = 0 AND i.has_filter = 0;
            """,
            []);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 3;
    }

    /// <summary>Прогони, що підлягають прибиранню, від найстаршого.</summary>
    internal async Task<List<EligibleRun>> ListEligibleRunsAsync(CancellationToken ct)
    {
        await using var scope = await ConnectionScope.OpenAsync(db, ct).ConfigureAwait(false);
        await using var command = CreateCommand(
            $"SELECT ru.Id, ISNULL(ru.PeriodKey, 0), ru.Status FROM calc.CalculationRun AS ru WHERE {EligibleSql("ru")} ORDER BY ru.Id;",
            [FailedBefore()]);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var runs = new List<EligibleRun>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            runs.Add(new EligibleRun(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2)));
        }

        return runs;
    }

    /// <summary>Скільки рядків таблиці належить прогонам, що підлягають прибиранню (dry-run, по індексу прогону).</summary>
    internal async Task<long> CountEligibleRowsAsync(string table, CancellationToken ct)
    {
        await using var scope = await ConnectionScope.OpenAsync(db, ct).ConfigureAwait(false);
        await using var command = CreateCommand(
            $"""
            SELECT COUNT_BIG(*) FROM {table} AS t WITH (FORCESEEK)
             WHERE t.CalculationRunId IN (SELECT ru.Id FROM calc.CalculationRun AS ru WHERE {EligibleSql("ru")});
            """,
            [FailedBefore()]);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Прибирає ОДИН прогін: рядки таблиць пакетами за індексом прогону, далі (з <see cref="DeletesRunRows"/>) сам рядок
    /// прогону. Кожен оператор перевіряє <see cref="EligibleSql"/> заново.
    /// </summary>
    /// <param name="runId">Прогін.</param>
    /// <param name="budget">Спільний бюджет пакетів.</param>
    /// <param name="betweenBatches">Перевірка між пакетами (журнал, CHECKPOINT); <c>false</c> — зупинитися.</param>
    /// <param name="ct">Скасування.</param>
    internal async Task<RunPurge> PurgeRunAsync(
        long runId, Budget budget, Func<CancellationToken, Task<bool>>? betweenBatches, CancellationToken ct)
    {
        var purge = new RunPurge();
        await using var scope = await ConnectionScope.OpenAsync(db, ct).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            var sql = $"""
                SET DEADLOCK_PRIORITY LOW;
                SET LOCK_TIMEOUT 5000;
                DELETE TOP (@batch) t FROM {table} AS t WITH (FORCESEEK)
                 WHERE t.CalculationRunId = @run
                   AND EXISTS (SELECT 1 FROM calc.CalculationRun AS ru WHERE ru.Id = @run AND {EligibleSql("ru")});
                SELECT @@ROWCOUNT;
                """;

            while (true)
            {
                if (!budget.CanContinue)
                {
                    return purge;
                }

                var deleted = await ExecuteCountAsync(
                        sql,
                        () =>
                        [
                            new SqlParameter("@batch", SqlDbType.Int) { Value = options.BatchSize },
                            new SqlParameter("@run", SqlDbType.BigInt) { Value = runId },
                            FailedBefore(),
                        ],
                        ct)
                    .ConfigureAwait(false);

                if (deleted > 0)
                {
                    purge.Deleted[table] = purge.Deleted.GetValueOrDefault(table) + deleted;
                    budget.Remaining--;
                    budget.Done++;
                    LogBatch(logger, table, runId, deleted);

                    if (!await AfterBatchAsync(budget, betweenBatches, ct).ConfigureAwait(false))
                    {
                        return purge;
                    }
                }

                // Неповний пакет — рядків прогону в таблиці більше немає (нових не з'явиться: прогін кінцевий).
                if (deleted < options.BatchSize)
                {
                    break;
                }
            }
        }

        if (DeletesRunRows)
        {
            if (!budget.CanContinue)
            {
                return purge;
            }

            // Рядок прогону — теж пакет бюджету: прохід лишається обмеженим і на базі, де рядків уже немає, а
            // прогонів-«оболонок» тисячі (прибирання до D-560 лишало рядки calc.CalculationRun).
            purge.RunDeleted = await DeleteRunRowAsync(runId, ct).ConfigureAwait(false);
            budget.Remaining--;
            budget.Done++;
        }

        purge.Complete = true;
        return purge;
    }

    /// <summary>
    /// Рядок <c>calc.CalculationRun</c> — лише якщо прогін досі підлягає прибиранню. FK (кроки, входи, результати)
    /// перевіряється по індексах прогону; лишився рядок — <c>547</c>, прогін лишається до наступного проходу.
    /// </summary>
    private async Task<bool> DeleteRunRowAsync(long runId, CancellationToken ct)
    {
        try
        {
            var deleted = await ExecuteCountAsync(
                    $"""
                    SET DEADLOCK_PRIORITY LOW;
                    SET LOCK_TIMEOUT 5000;
                    DELETE ru FROM calc.CalculationRun AS ru WHERE ru.Id = @run AND {EligibleSql("ru")};
                    SELECT @@ROWCOUNT;
                    """,
                    () => [new SqlParameter("@run", SqlDbType.BigInt) { Value = runId }, FailedBefore()],
                    ct)
                .ConfigureAwait(false);
            return deleted > 0;
        }
        catch (SqlException ex) when (ex.Number == ForeignKeyViolationError)
        {
            LogRunRowKept(logger, runId);
            return false;
        }
    }

    /// <summary>
    /// Швидкий шлях: прибирає прогони, які щойно замінило перемикання. Повертає скільки рядків видалено (без рядків
    /// самих прогонів).
    /// </summary>
    /// <param name="runs">Прогони з перемикання.</param>
    /// <param name="ct">Скасування.</param>
    internal async Task<int> PurgeRunsAsync(IReadOnlyList<SupersededRun> runs, CancellationToken ct)
    {
        // ✎ D-560: власна мала стеля швидкого шляху; накопиченого він не чіпає (лише передані прогони).
        var budget = new Budget(SupersededPurgeOptions.FastPathMaxBatches);
        await using var scope = await ConnectionScope.OpenAsync(db, ct).ConfigureAwait(false);

        if (await RunIndexesPresentAsync(ct).ConfigureAwait(false))
        {
            long rows = 0;
            foreach (var runId in runs.Select(r => r.RunId).Distinct().Order())
            {
                var purge = await PurgeRunAsync(runId, budget, betweenBatches: null, ct).ConfigureAwait(false);
                rows += purge.Rows;
                if (purge.Complete)
                {
                    LogRunPurged(logger, runId, purge.Rows, purge.RunDeleted);
                }

                if (!budget.CanContinue)
                {
                    break;
                }
            }

            return (int)Math.Min(int.MaxValue, rows);
        }

        LogRunIndexesMissing(logger);
        return await PurgeRunsKeysetAsync(runs, budget, ct).ConfigureAwait(false);
    }

    /// <summary>Запасна форма швидкого шляху (індексів прогону немає): лише рядки, keyset по кластерному ключу.</summary>
    private async Task<int> PurgeRunsKeysetAsync(IReadOnlyList<SupersededRun> runs, Budget budget, CancellationToken ct)
    {
        var total = 0;
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
                where.Append(" AND ").Append(EligibleRowWhere);

                total += (int)await DeleteKeysetAsync(
                        table, group.Key.PeriodKey, where.ToString(), parameters, budget, betweenBatches: null, ct)
                    .ConfigureAwait(false);
            }
        }

        return total;
    }

    /// <summary>
    /// Крок нічного проходу в запасній формі (індексів прогону немає) для однієї таблиці й партиції: рядки прогонів, що
    /// підлягають прибиранню (<see cref="EligibleSql"/>).
    /// </summary>
    internal Task<long> DeleteNonCurrentAsync(
        string table, int periodKey, Budget budget, Func<CancellationToken, Task<bool>>? betweenBatches,
        CancellationToken ct)
        => DeleteKeysetAsync(
            table, periodKey, EligibleRowWhere, parameters: [], budget, betweenBatches, ct);

    /// <summary>Умова на рядок таблиці (<c>t</c>): його прогін підлягає прибиранню.</summary>
    internal static string EligibleRowWhere =>
        $"EXISTS (SELECT 1 FROM calc.CalculationRun AS ru WHERE ru.Id = t.CalculationRunId AND {EligibleSql("ru")})";

    /// <summary>Скільки рядків таблиці в партиції належить прогонам, що підлягають прибиранню (запасний dry-run).</summary>
    internal async Task<long> CountNonCurrentAsync(string table, int periodKey, CancellationToken ct)
    {
        await using var scope = await ConnectionScope.OpenAsync(db, ct).ConfigureAwait(false);
        await using var command = CreateCommand(
            $"SELECT COUNT_BIG(*) FROM {table} AS t WHERE t.PeriodKey = @p AND {EligibleRowWhere};",
            [new SqlParameter("@p", SqlDbType.Int) { Value = periodKey }, FailedBefore()]);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>Партиції, де є прогони, що підлягають прибиранню (запасна форма; за таблицею прогонів).</summary>
    internal async Task<List<int>> ListEligiblePeriodsAsync(CancellationToken ct)
        => (await ListEligibleRunsAsync(ct).ConfigureAwait(false))
            .Select(r => r.PeriodKey)
            .Distinct()
            .Order()
            .ToList();

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

        while (budget.CanContinue)
        {
            long? hi;
            int batchDeleted;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var all = new List<SqlParameter>(parameters.Count + 4)
                    {
                        new("@batch", SqlDbType.Int) { Value = options.BatchSize },
                        new("@p", SqlDbType.Int) { Value = periodKey },
                        new("@last", SqlDbType.BigInt) { Value = last },
                        FailedBefore(),
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
            LogKeysetBatch(logger, table, periodKey, batchDeleted);

            if (!await AfterBatchAsync(budget, betweenBatches, ct).ConfigureAwait(false))
            {
                return deleted;
            }
        }

        return deleted;
    }

    /// <summary>Після пакета: перевірка викликача (журнал, CHECKPOINT) і пауза. <c>false</c> — зупинитися.</summary>
    private async Task<bool> AfterBatchAsync(
        Budget budget, Func<CancellationToken, Task<bool>>? betweenBatches, CancellationToken ct)
    {
        if (betweenBatches is not null && !await betweenBatches(ct).ConfigureAwait(false))
        {
            budget.Stopped = true;
            return false;
        }

        if (options.PauseBetweenBatches > TimeSpan.Zero)
        {
            await Task.Delay(options.PauseBetweenBatches, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Оператор, що повертає одне число (<c>SELECT @@ROWCOUNT</c>), з повтором жертви блокування/таймауту.</summary>
    private async Task<int> ExecuteCountAsync(string sql, Func<List<SqlParameter>> parameters, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var command = CreateCommand(sql, parameters());
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
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
    }

    /// <summary>Значення <see cref="FailedBeforeParameter"/>: зараз мінус <see cref="SupersededPurgeOptions.FailedRunGrace"/>.</summary>
    private SqlParameter FailedBefore()
        => new(FailedBeforeParameter, SqlDbType.DateTime2)
        {
            Value = clock.UtcNow - SupersededPurgeOptions.FailedRunGrace,
        };

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
        Level = LogLevel.Debug,
        Message = "Прибирання неактуальних прогонів: {Table}, прогін {RunId} - пакет видалив {Deleted}.")]
    private static partial void LogBatch(ILogger logger, string table, long runId, int deleted);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прибирання неактуальних прогонів (запасна форма): {Table}, період {PeriodKey} - пакет видалив {Deleted}.")]
    private static partial void LogKeysetBatch(ILogger logger, string table, int periodKey, int deleted);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прибирання неактуальних прогонів: прогін {RunId} прибрано - рядків {Rows}, рядок прогону видалено: {RunDeleted}.")]
    internal static partial void LogRunPurged(ILogger logger, long runId, long rows, bool runDeleted);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання неактуальних прогонів: рядок прогону {RunId} лишився - на нього ще посилаються рядки (FK); наступний прохід повторить.")]
    private static partial void LogRunRowKept(ILogger logger, long runId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання неактуальних прогонів: індексів за CalculationRunId (07-partition-tables.sql) немає - прибираються лише рядки (keyset), рядки calc.CalculationRun лишаються. Повторіть 07-partition-tables.sql.")]
    internal static partial void LogRunIndexesMissing(ILogger logger);
}
