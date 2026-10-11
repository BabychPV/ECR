using System.Xml.Linq;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// R5-Q1-03: видалення документа прибирає <c>calc.CalculationResult</c> / <c>calc.CalculationInput</c>
/// за <c>DocumentId</c> З відсічкою партицій (<c>PeriodKey</c>), а не сканом усіх партицій усередині
/// транзакції, що тримає діапазон <c>ext.SourceEventMap</c>.
/// </summary>
/// <remarks>
/// ⚠ Той самий прийом, що в <c>DocumentDeletionStoreTests.Прогони_документа_прибираються_з_відсічкою_партицій_трейсу</c>:
/// DELETE перехоплюється, повторюється під <c>STATISTICS XML</c> з відкатом, і рахується, скільки партицій
/// він торкнувся. Голий <c>DocumentId</c> торкається всіх (<c>fanout</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentDeletionCalcPartitionTests(SqlServerFixture sql)
{
    private static readonly DateTime At = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "R5-Q1-03")]
    public async Task Результати_й_входи_документа_прибираються_з_відсічкою_партицій()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        long periodInputId;
        long yearInputId;
        await using (var db = builder.CreateContext())
        {
            // Прогін проєкту (не документа): його входи документа прибирає САМЕ предикат за DocumentId,
            // а не гілка «прогони документа». Річний прогін пише в PeriodKey 0 (`run.PeriodKey ?? 0`).
            var periodRun = new CalculationRun(doc.ProjectId, doc.PeriodKey.Value, null, At);
            var yearRun = new CalculationRun(doc.ProjectId, null, null, At);
            db.CalculationRuns.AddRange(periodRun, yearRun);
            await db.SaveChangesAsync();

            var periodInput = new CalculationInputRow(periodRun.Id, doc.PeriodKey.Value, doc.DocumentId, "R1", "ARG");
            var yearInput = new CalculationInputRow(yearRun.Id, 0, doc.DocumentId, "R1", "ARG");
            foreach (var input in new[] { periodInput, yearInput })
            {
                var id = (await db.Database
                    .SqlQuery<long>($"SELECT NEXT VALUE FOR calc.CalculationResultSeq AS Value")
                    .ToListAsync())[0];
                typeof(Ecr.Domain.Abstractions.Entity<long>).GetProperty("Id")!.SetValue(input, id);
                db.CalculationInputs.Add(input);
            }

            await db.SaveChangesAsync();
            periodInputId = periodInput.Id;
            yearInputId = yearInput.Id;
        }

        var recorder = new DocumentCalcDeleteRecorder();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(recorder)
            .Options))
        {
            await new DocumentDeletionStore(db).DeleteAsync(doc.DocumentId, CancellationToken.None);
        }

        // Повнота набору ключів: і вхід періоду, і вхід річного прогону (PeriodKey 0) прибрано.
        await using (var check = builder.CreateContext())
        {
            Assert.False(await check.CalculationInputs.AnyAsync(i => i.Id == periodInputId || i.Id == yearInputId));
        }

        // По DELETE на кожен ключ періоду для кожної з двох таблиць (рівність, а не IN-список —
        // див. DocumentDeletionStore): ключі {0, період документа} → щонайменше по два на таблицю.
        Assert.True(recorder.Seen.Count >= 4, $"DELETE результатів/входів: {recorder.Seen.Count}");

        int fanout;
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT fanout FROM sys.partition_functions WHERE name = N'pf_ByPeriodKey';";
            fanout = (int)(await count.ExecuteScalarAsync())!;
        }

        foreach (var table in new[] { "[CalculationResult]", "[CalculationInput]" })
        {
            var deletes = recorder.Seen.Where(s => s.Text.Contains(table, StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(deletes);

            foreach (var delete in deletes)
            {
                var accessed = await PartitionsAccessedAsync(table, delete);

                Assert.NotEmpty(accessed);
                Assert.All(accessed, n => Assert.True(n < fanout, $"прочитано {n} партицій {table} із {fanout}:\n{delete.Text}"));
            }
        }
    }

    /// <summary>
    /// X1-03 (аудит R11): прогони САМЕ цього документа прибираються з рівністю за <c>PeriodKey</c> — по
    /// DELETE на ключ, а не <c>PeriodKey IN (…)</c>: список параметрів оптимізатор згортає в залишковий
    /// OR-предикат і вільний обрати скан без відсічки партицій <c>calc.CalculationResult</c>.
    /// </summary>
    /// <remarks>Мутація: повернути `keys.Contains(r.PeriodKey)` — у тексті DELETE зʼявляється `IN (`, червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "X1-03")]
    public async Task Прогони_документа_прибираються_рівністю_за_ключем_періоду_а_не_IN_списком()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        long runId;
        await using (var db = builder.CreateContext())
        {
            // Другий період проєкту й РІЧНИЙ прогін документа: набір ключів — щонайменше два, тож
            // `Contains` по списку не вироджується в одиночну рівність.
            db.Periods.Add(new Period(
                doc.ProjectId, new PeriodKey(doc.PeriodKey.Value + 1), 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));

            // Прогін САМЕ документа: його кроки/входи/результати йдуть гілкою «прогони документа».
            var run = new CalculationRun(doc.ProjectId, null, null, At, doc.DocumentId);
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        var recorder = new RunScopedCalcDeleteRecorder();
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(recorder)
            .Options))
        {
            await new DocumentDeletionStore(db).DeleteAsync(doc.DocumentId, CancellationToken.None);
        }

        await using (var check = builder.CreateContext())
        {
            Assert.False(await check.CalculationRuns.AnyAsync(r => r.Id == runId));
        }

        foreach (var table in new[] { "[CalculationStep]", "[CalculationInput]", "[CalculationResult]" })
        {
            var deletes = recorder.Seen.Where(t => t.Contains(table, StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(deletes);
            Assert.All(deletes, text => Assert.DoesNotMatch(@"\[PeriodKey\]\s+IN\s*\(", text));
        }
    }

    /// <summary>Повторює DELETE під <c>STATISTICS XML</c> з відкатом: партиції, яких він торкнувся.</summary>
    private async Task<List<int>> PartitionsAccessedAsync(
        string table, (string Text, List<(string Name, System.Data.SqlDbType Type, object Value)> Parameters) delete)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SET STATISTICS XML ON;\n" + delete.Text + "\nSET STATISTICS XML OFF;";
        foreach (var (name, type, value) in delete.Parameters)
        {
            command.Parameters.Add(new SqlParameter(name, type) { Value = value });
        }

        var plans = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal))
                    {
                        plans.Add(reader.GetString(0));
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        await transaction.RollbackAsync();

        return plans
            .SelectMany(p => XDocument.Parse(p).Descendants(Showplan + "RelOp"))
            .Where(op => op.Element(Showplan + "RunTimePartitionSummary") is not null
                         && op.Descendants(Showplan + "Object").Any(o => (string?)o.Attribute("Table") == table))
            .Select(op => (int)op.Element(Showplan + "RunTimePartitionSummary")!.Element(Showplan + "PartitionsAccessed")!.Attribute("PartitionCount")!)
            .ToList();
    }
}

/// <summary>Запамʼятовує DELETE кроків/входів/результатів за прогоном (<c>CalculationRunId</c>) — текст команди.</summary>
internal sealed class RunScopedCalcDeleteRecorder : DbCommandInterceptor
{
    public List<string> Seen { get; } = [];

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.CommandText.Contains("DELETE", StringComparison.Ordinal)
            && command.CommandText.Contains("[CalculationRunId]", StringComparison.Ordinal))
        {
            Seen.Add(command.CommandText);
        }

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>Запам'ятовує DELETE результатів/входів документа (предикат за <c>DocumentId</c>) — для повтору під планом.</summary>
internal sealed class DocumentCalcDeleteRecorder : DbCommandInterceptor
{
    public List<(string Text, List<(string Name, System.Data.SqlDbType Type, object Value)> Parameters)> Seen { get; } = [];

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.CommandText.Contains("DELETE", StringComparison.Ordinal)
            && command.CommandText.Contains("[DocumentId]", StringComparison.Ordinal)
            && (command.CommandText.Contains("[CalculationResult]", StringComparison.Ordinal)
                || command.CommandText.Contains("[CalculationInput]", StringComparison.Ordinal)))
        {
            Seen.Add((command.CommandText, [.. command.Parameters.Cast<SqlParameter>()
                .Select(p => (p.ParameterName, p.SqlDbType, p.Value))]));
        }

        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }
}
