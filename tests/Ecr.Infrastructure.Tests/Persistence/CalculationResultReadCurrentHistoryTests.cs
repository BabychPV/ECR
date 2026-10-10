using System.Xml.Linq;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// R5-Q1-02: <see cref="CalculationResultStore.ReadCurrentAsync"/> читає результати АКТУАЛЬНОГО прогону
/// seek'ом до прогону, а не перебирає результати всіх минулих (<c>Superseded</c>) прогонів документа.
/// </summary>
/// <remarks>
/// ⛔ Доти відбір за прогоном був <c>EXISTS</c> по кожному рядку, а <c>IX_CalculationResult_Lookup</c>
/// не містить <c>CalculationRunId</c>: план діставав КОЖЕН результат документа-періоду за всю історію
/// перерахунків (ретенції немає, ЗБР-1) і лише потім відсіював. Тест перехоплює бойовий SELECT,
/// повторює його під <c>STATISTICS XML</c> і рахує рядки, які оператори над
/// <c>[CalculationResult]</c> фактично віддали.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationResultReadCurrentHistoryTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    /// <summary>Скільки минулих прогонів документа накопичилось.</summary>
    private const int SupersededRuns = 30;

    /// <summary>Результатів на прогін.</summary>
    private const int ResultsPerRun = 200;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "R5-Q1-02")]
    public async Task Читання_актуальних_результатів_не_залежить_від_історії_перерахунків()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 1, rowCount: 1);

        long currentRunId;
        await using (var db = builder.CreateContext())
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var methodology = new Methodology(
                EcrCode.Create($"Q102_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();

            var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

            var runs = new List<CalculationRun>();
            for (var i = 0; i <= SupersededRuns; i++)
            {
                var run = new CalculationRun(doc.ProjectId, doc.PeriodKey.Value, null, Now.AddMinutes(i), doc.DocumentId);
                run.Complete(CalculationRun.SupersededStatus, Now.AddMinutes(i), null, null);
                runs.Add(run);
                db.CalculationRuns.Add(run);
            }

            // Актуальний — останній; решта — історія.
            runs[^1].MakeCurrent();
            await db.SaveChangesAsync();
            currentRunId = runs[^1].Id;

            foreach (var run in runs)
            {
                var marker = run.Id == currentRunId ? 1m : 0m;
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO calc.CalculationResult
                        (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
                    SELECT NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {version.Id}, {doc.PeriodKey.Value},
                           {doc.DocumentId}, CONCAT(N'R', n.n), N'OUT', {marker}, {unitId}
                      FROM (SELECT TOP ({ResultsPerRun}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                              FROM sys.all_objects) AS n;
                    """);
            }
        }

        var recorder = new ResultSelectRecorder();
        IReadOnlyList<CalculationResultRow> rows;
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .AddInterceptors(recorder)
            .Options))
        {
            rows = await new CalculationResultStore(db, new TestClock(Now))
                .ReadCurrentAsync(doc.DocumentId, doc.PeriodKey.Value, CancellationToken.None);
        }

        // Семантика: рівно результати актуального прогону.
        Assert.Equal(ResultsPerRun, rows.Count);
        Assert.All(rows, r => Assert.Equal(1m, r.Value));

        var select = Assert.Single(recorder.Seen);
        var produced = await RowsProducedAsync(select);

        output.WriteLine($"рядків від операторів над [CalculationResult]: {produced}; історія {SupersededRuns * ResultsPerRun}");

        // ⛔ Головне: план не торкається результатів минулих прогонів. Межа — удвічі від актуальних
        // (seek + можливий lookup), тоді як старий план віддавав ≥ (SupersededRuns + 1) × ResultsPerRun.
        Assert.True(
            produced <= 2 * ResultsPerRun,
            $"оператори над [CalculationResult] віддали {produced} рядків при {ResultsPerRun} актуальних:\n{select.Text}");
    }

    /// <summary>Повторює SELECT під <c>STATISTICS XML</c>: скільки рядків віддали оператори над таблицею.</summary>
    private async Task<long> RowsProducedAsync(
        (string Text, List<(string Name, System.Data.SqlDbType Type, object Value)> Parameters) select)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SET STATISTICS XML ON;\n" + select.Text + "\nSET STATISTICS XML OFF;";
        foreach (var (name, type, value) in select.Parameters)
        {
            command.Parameters.Add(new SqlParameter(name, type) { Value = value });
        }

        var plans = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                var isPlan = reader.FieldCount == 1
                             && reader.GetName(0).StartsWith("Microsoft SQL Server", StringComparison.Ordinal);
                while (await reader.ReadAsync())
                {
                    if (isPlan)
                    {
                        plans.Add(reader.GetString(0));
                    }
                }
            }
            while (await reader.NextResultAsync());
        }

        var ops = plans
            .SelectMany(p => XDocument.Parse(p).Descendants(Showplan + "RelOp"))
            .Where(op => op.Elements().Any(e => e.Elements(Showplan + "Object")
                .Any(o => (string?)o.Attribute("Table") == "[CalculationResult]")))
            .ToList();

        Assert.NotEmpty(ops);

        return ops
            .SelectMany(op => op.Elements(Showplan + "RunTimeInformation").Elements(Showplan + "RunTimeCountersPerThread"))
            .Sum(c => (long?)c.Attribute("ActualRows") ?? 0);
    }
}

/// <summary>Запам'ятовує SELECT результатів (<c>calc.CalculationResult</c>) разом із параметрами.</summary>
internal sealed class ResultSelectRecorder : DbCommandInterceptor
{
    public List<(string Text, List<(string Name, System.Data.SqlDbType Type, object Value)> Parameters)> Seen { get; } = [];

    public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command,
        CommandEventData eventData,
        InterceptionResult<System.Data.Common.DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.CommandText.Contains("FROM [calc].[CalculationResult]", StringComparison.Ordinal))
        {
            Seen.Add((command.CommandText, [.. command.Parameters.Cast<SqlParameter>()
                .Select(p => (p.ParameterName, p.SqlDbType, p.Value))]));
        }

        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
