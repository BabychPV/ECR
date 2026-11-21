using System.Data;
using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Persistence;

/// <summary>B85 (хвіст B13): масовий запис результатів прогону через <see cref="SqlBulkCopy"/>.</summary>
/// <remarks>
/// ⛔ Семантика та сама, що в EF-шляху <see cref="WriteResultsAsync"/>: ті самі рядки (той самий конструктор
/// сутності, ті самі Id з <c>calc.CalculationResultSeq</c> у тому самому порядку), та сама транзакція (EF-транзакція
/// контексту — <c>SqlBulkCopy</c> іде в ній, а не поруч), ті самі обмеження (<c>CheckConstraints</c>: FK на прогін,
/// версію й одиницю перевіряються, ключі лишаються довіреними). Тригерів і аудиту на <c>calc.CalculationResult</c>
/// немає; витіснення попереднього прогону (<see cref="SwitchCurrentRunAsync"/>) від способу вставки не залежить.
/// <para>
/// ⚠ Без <c>TableLock</c> (на відміну від <see cref="BulkCellLoader"/>): гілки оркестратора пишуть результати того
/// самого періоду паралельно, а табличне X-блокування на кластерному індексі серіалізувало б їх.
/// </para>
/// <para>
/// ⚠ Лише в транзакції: поза нею рядки лягли б окремо від трейсу, що пишеться наступним <c>SaveChanges</c>.
/// Без транзакції (чи не SQL Server) — EF-шлях, як до B85. <c>CalculationOutputWriter</c> відкриває транзакцію для
/// чанка понад <see cref="ICalculationResultStore.BulkWriteThreshold"/> сам.
/// </para>
/// </remarks>
public sealed partial class CalculationResultStore
{
    /// <summary>
    /// ✎ L1b / D1: рядків на один оператор <c>insert bulk</c> (<see cref="SqlBulkCopy.BatchSize"/>) — у тій самій транзакції.
    /// </summary>
    /// <remarks>
    /// ⛔ Індекси <c>calc.CalculationResult</c> беруть лише рядкові замки (<c>ALLOW_PAGE_LOCKS = OFF</c>,
    /// <c>07-partition-tables.sql</c>, міграція <c>L1bCalcRowLocksOnly</c>): сторінкові X-замки на межах діапазонів гілок
    /// давали взаємні блокування одночасних записів того самого періоду (стенд Land, 1205). Рядковий замок — один на ключ
    /// кожного індексу, і оператор від 5000 замків на об'єкт пробує ескалацію до X на всю таблицю, що серіалізувало б гілки.
    /// Пакет нижче порога тримає кожен оператор поза нею; транзакція й атомарність методології — ті самі.
    /// </remarks>
    internal const int BulkCopyBatchRows = 2_000;

    /// <summary>Id результатів останнього масового запису (рядків немає в трекері) — для прив'язки кроків трейсу.</summary>
    private BulkPendingResults? bulkPending;

    private sealed record BulkPendingResults(
        long RunId, Dictionary<(long, long?, string?, int, string, long?), long> Ids);

    /// <summary>Колонки <c>calc.CalculationResult</c>; мапінг лише за іменами (див. <see cref="BulkCellLoader"/>).</summary>
    private static readonly (string Name, Type Type)[] BulkColumns =
    [
        ("PeriodKey", typeof(int)),
        ("Id", typeof(long)),
        ("CalculationRunId", typeof(long)),
        ("MethodologyVersionId", typeof(int)),
        ("DocumentId", typeof(long)),
        ("SourceRowKey", typeof(string)),
        ("OutputCode", typeof(string)),
        ("Value", typeof(decimal)),
        ("UnitId", typeof(int)),

        // ⛔ Фізично int (EF: HasConversion<int?>), як ValueRegistryEntryId у BulkCellLoader.
        ("SubstanceEntryId", typeof(int)),
        ("Kind", typeof(byte)),
        ("TableInstanceId", typeof(long)),
    ];

    private bool UseBulk(int count, int? threshold)
        => count > (threshold ?? ICalculationResultStore.BulkWriteThreshold)
           && db.Database.IsSqlServer()
           && db.Database.CurrentTransaction is not null;

    private async Task BulkInsertResultsAsync(long calculationRunId, List<CalculationResult> rows, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var table = new DataTable();
        foreach (var (name, type) in BulkColumns)
        {
            table.Columns.Add(name, type);
        }

        var ids = new Dictionary<(long, long?, string?, int, string, long?), long>(rows.Count);
        foreach (var r in rows)
        {
            table.Rows.Add(
                r.PeriodKey,
                r.Id,
                r.CalculationRunId,
                r.MethodologyVersionId,
                r.DocumentId,
                (object?)r.SourceRowKey ?? DBNull.Value,
                r.OutputCode,
                r.Value,
                r.UnitId,
                (object?)(int?)r.SubstanceEntryId ?? DBNull.Value,
                (byte)r.Kind,
                (object?)r.TableInstanceId ?? DBNull.Value);

            ids.TryAdd(
                (r.DocumentId, r.TableInstanceId, r.SourceRowKey, r.MethodologyVersionId,
                 r.OutputCode.ToUpperInvariant(), r.SubstanceEntryId),
                r.Id);
        }

        var connection = (SqlConnection)db.Database.GetDbConnection();
        var transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = "calc.CalculationResult",
            BulkCopyTimeout = db.Database.GetCommandTimeout() ?? 30,
            BatchSize = BulkCopyBatchRows,
        };

        foreach (var (name, _) in BulkColumns)
        {
            copy.ColumnMappings.Add(name, name);
        }

        await copy.WriteToServerAsync(table, ct).ConfigureAwait(false);
        bulkPending = new BulkPendingResults(calculationRunId, ids);

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogBulkWritten(logger ?? NullLogger<CalculationResultStore>.Instance, calculationRunId, rows.Count, elapsedMs);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "B85: прогін {RunId} — {Rows} результатів записано SqlBulkCopy за {ElapsedMs} мс.")]
    private static partial void LogBulkWritten(ILogger logger, long runId, int rows, double elapsedMs);

    /// <summary>
    /// ✎ D-565 (PERF-03): колонки <c>calc.CalculationStep</c> масового запису; мапінг лише за іменами.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>TraceJson</c> (нестиснений текст кроків до D-565) не пишеться — лишається <c>NULL</c>; деталізація йде
    /// стисненою в <c>TraceJsonGz</c> (<see cref="TraceJsonCompression"/>), і читач бере її прозоро
    /// (<see cref="CalculationStep.TraceJson"/>).
    /// </remarks>
    private static readonly (string Name, Type Type)[] StepBulkColumns =
    [
        ("PeriodKey", typeof(int)),
        ("Id", typeof(long)),
        ("CalculationRunId", typeof(long)),
        ("ResultId", typeof(long)),
        ("StepOrder", typeof(int)),
        ("StepCode", typeof(string)),
        ("Expression", typeof(string)),
        ("Value", typeof(decimal)),
        ("TraceJsonGz", typeof(byte[])),
        ("MaskedZero", typeof(byte)),
        ("DocumentId", typeof(long)),
        ("SourceRowKey", typeof(string)),

        // ⛔ Фізично int (EF: HasConversion<int?>), як у результату.
        ("SubstanceEntryId", typeof(int)),
        ("TableInstanceId", typeof(long)),
    ];

    /// <summary>Кроки — масово: SQL Server, транзакція записувача й понад <c>stepBulkThreshold</c> кроків (типово — будь-скільки).</summary>
    private bool UseStepBulk(int count)
        => count > (stepBulkThreshold ?? 0)
           && db.Database.IsSqlServer()
           && db.Database.CurrentTransaction is not null;

    /// <summary>
    /// ✎ D-565 (PERF-03): кроки трейсу одним <see cref="SqlBulkCopy"/> у транзакції записувача — замість EF-пакетів
    /// MERGE (~715 походів до сервера на прогін на стенді Land, query 2511 аудиту).
    /// </summary>
    /// <remarks>
    /// ⛔ Ті самі рядки, що EF-шлях (<see cref="WriteStepsAsync"/>): ті самі Id з діапазону, адреса кроку, прив'язка до
    /// результату (<see cref="ResultIdOf"/>), обрізаний вираз (<see cref="CalculationStep.TrimExpression"/>), перевірка
    /// адреси (рядок/речовина без документа — відмова, як у <see cref="CalculationStep.SetAddress"/>), FK на прогін
    /// (<c>CheckConstraints</c>). Без <c>TableLock</c> — з тієї самої причини, що й у результатів (паралельні гілки).
    /// </remarks>
    private async Task BulkInsertStepsAsync(
        long calculationRunId,
        int periodKey,
        List<(CalculationOutput Output, CalculationTraceStep Step)> steps,
        long firstId,
        Dictionary<(long, long?, string?, int, string, long?), long> results,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var table = new DataTable();
        foreach (var (name, type) in StepBulkColumns)
        {
            table.Columns.Add(name, type);
        }

        var nextId = firstId;
        foreach (var (output, step) in steps)
        {
            // Ті самі перевірки, що конструктор кроку; адреса — документ виходу (є завжди), рядок і речовина.
            ArgumentException.ThrowIfNullOrWhiteSpace(step.StepCode);
            table.Rows.Add(
                periodKey,
                nextId++,
                calculationRunId,
                (object?)ResultIdOf(results, output, step) ?? DBNull.Value,
                step.StepOrder,
                step.StepCode,
                (object?)CalculationStep.TrimExpression(step.Expression) ?? DBNull.Value,
                (object?)step.Value ?? DBNull.Value,
                (object?)TraceJsonCompression.Compress(step.Detail ?? step.TraceJson) ?? DBNull.Value,
                (byte)step.Masked,
                output.DocumentId,
                (object?)output.SourceRowKey ?? DBNull.Value,
                (object?)(int?)step.SubstanceEntryId ?? DBNull.Value,
                (object?)output.TableInstanceId ?? DBNull.Value);
        }

        var connection = (SqlConnection)db.Database.GetDbConnection();
        var transaction = (SqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        using var copy = new SqlBulkCopy(connection, SqlBulkCopyOptions.CheckConstraints, transaction)
        {
            DestinationTableName = SupersededResultsPurger.StepTable,
            BulkCopyTimeout = db.Database.GetCommandTimeout() ?? 30,
            BatchSize = BulkCopyBatchRows,
        };

        foreach (var (name, _) in StepBulkColumns)
        {
            copy.ColumnMappings.Add(name, name);
        }

        await copy.WriteToServerAsync(table, ct).ConfigureAwait(false);

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        LogStepsBulkWritten(logger ?? NullLogger<CalculationResultStore>.Instance, calculationRunId, steps.Count, elapsedMs);
    }

    /// <remarks>
    /// ⚠ Без слова «SqlBulkCopy» у тексті: лічильник масових записів РЕЗУЛЬТАТІВ у тестах B85 шукає саме його.
    /// </remarks>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "D-565: прогін {RunId} — {Rows} кроків трейсу записано масово (bulk copy) за {ElapsedMs} мс.")]
    private static partial void LogStepsBulkWritten(ILogger logger, long runId, int rows, double elapsedMs);
}
