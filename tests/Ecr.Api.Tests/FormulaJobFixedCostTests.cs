// tests/Ecr.Api.Tests/FormulaJobFixedCostTests.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// L2b (recalc-latency): ФІКСОВАНА вартість задачі перерахунку формул з ОДНІЄЮ зміненою коміркою на шаблоні з багатьма
/// таблицями. Вимірюється справжній <see cref="FormulaRecalculationJob"/> з DI справжнього хоста у теплому стані
/// (кеш метаданих прогрітий): скільки звернень до БД і які саме, скільки логічних читань.
/// </summary>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage3)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class FormulaJobFixedCostTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly AsyncLocal<bool> Measuring = new();

    [Fact]
    public async Task Задача_з_однією_коміркою_на_шаблоні_Land_масштабу_має_стелю_звернень_без_графа_і_без_квадрата()
    {
        const int tablesCount = 30;
        const int rowsCount = 100;
        const int columnsCount = 12;

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SiteZone));
        var periodKey = (siteToday.Year * 100) + siteToday.Month;
        var document = await builder.BuildAsync(periodKey, columnCount: 2, rowCount: 3, rowMode: TableRowMode.Fixed)
            .ConfigureAwait(true);
        var extra = await MultiTableDocument
            .AddTablesAsync(builder, document, [.. Enumerable.Repeat(1, tablesCount)], columnsCount, rowsCount)
            .ConfigureAwait(true);

        await using (var db = builder.CreateContext())
        {
            foreach (var sheet in extra.Select(e => e.SheetDefId).Distinct())
            {
                db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, sheet));
            }

            var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(true);
            project.Activate(DateTime.UtcNow);
            var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(true);
            var period = await db.Periods.FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(true);
            period.RecomputeBoundaries(policy, SiteZone);
            period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var tableIds = string.Join(",", extra.Select(e => e.TableDefId));
        await ExecuteAsync($"UPDATE cfg.ColumnDef SET DataType = 6 WHERE TableDefId IN ({tableIds}) AND Ordinal >= 3");
        await ExecuteAsync(
            "INSERT INTO cfg.FormulaDef (TableDefId, Scope, ColumnDefId, RowDefId, Dialect, Expression, EvaluationOrder, IsCrossSheet, IsSnapshot, IsDeleted) " +
            "SELECT r.TableDefId, 2, c.Id, r.Id, 0, " +
            "CASE WHEN c.Ordinal = 3 THEN '[' + c1.Code + '] + [' + c2.Code + ']' ELSE '[' + cp.Code + '] + 1' END, 0, 0, 0, 0 " +
            "FROM cfg.RowDef r JOIN cfg.ColumnDef c ON c.TableDefId = r.TableDefId AND c.Ordinal >= 3 " +
            "JOIN cfg.ColumnDef c1 ON c1.TableDefId = r.TableDefId AND c1.Ordinal = 1 " +
            "JOIN cfg.ColumnDef c2 ON c2.TableDefId = r.TableDefId AND c2.Ordinal = 2 " +
            "JOIN cfg.ColumnDef cp ON cp.TableDefId = r.TableDefId AND cp.Ordinal = CASE WHEN c.Ordinal = 3 THEN 1 ELSE c.Ordinal - 1 END " +
            $"WHERE r.TableDefId IN ({tableIds});");
        await ExecuteAsync(
            "INSERT INTO cfg.FormulaDependency (SourceKind, FormulaDefId, BindingId, DependsOnKind, TableDefId, RowKey, ColumnDefId, FilterJson, PeriodOffset, SortOrder) " +
            "SELECT 0, f.Id, NULL, 0, f.TableDefId, r.RowKey, d.Id, NULL, NULL, 0 " +
            "FROM cfg.FormulaDef f JOIN cfg.RowDef r ON r.Id = f.RowDefId " +
            "JOIN cfg.ColumnDef c ON c.Id = f.ColumnDefId " +
            "JOIN cfg.ColumnDef d ON d.TableDefId = f.TableDefId AND (d.Ordinal = 1 AND c.Ordinal = 3 OR d.Ordinal = 2 AND c.Ordinal = 3 OR c.Ordinal > 3 AND d.Ordinal = c.Ordinal - 1) " +
            $"WHERE f.TableDefId IN ({tableIds});");

        var first = extra[0];
        for (var i = 0; i < 2; i++)
        {
            await ExecuteAsync(
                "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
                $"({periodKey}, {first.RowIds[0]}, {first.ColumnDefIds[i]}, {first.TableDefId}, {i + 1}, 0, 0);");
        }

        using var app = new EcrApiFactory(sql, stampCacheSeconds: 300, revisionWindow: TimeSpan.FromMinutes(5));
        app.Server.PreserveExecutionContext = true;


        var texts = new ConcurrentQueue<string>();
        using var counter = new SqlClientCommandCounter(new CommandTally(), command =>
        {
            if (!Measuring.Value)
            {
                return false;
            }

            texts.Enqueue(command.CommandText ?? string.Empty);
            return true;
        });

        var warm = new List<TimeSpan>();
        for (var round = 0; round < 5; round++)
        {
            await ExecuteAsync(
                $"UPDATE doc.CellValue SET ValueNumeric = {10 + round} WHERE PeriodKey = {periodKey} AND TableRowId = {first.RowIds[0]} AND ColumnDefId = {first.ColumnDefIds[0]}");
            counter.Tally.Reset();
            texts.Clear();

            Measuring.Value = true;
            var watch = Stopwatch.StartNew();
            var progress = new Progress();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var job = scope.ServiceProvider.GetRequiredService<IFormulaRecalculationJob>();
                await job.ExecuteAsync(
                    new FormulaRecalculationRequest(
                        first.TableInstanceId, periodKey,
                        [new DirtyCell(first.RowIds[0], first.ColumnDefIds[0], PeriodKey: periodKey, TableInstanceId: first.TableInstanceId)],
                        document.DocumentId),
                    progress, CancellationToken.None).ConfigureAwait(true);
            }

            watch.Stop();
            Measuring.Value = false;

            var seen = counter.Tally.Snapshot();
            counter.AssertObserved();
            output.WriteLine($"round {round}: {watch.ElapsedMilliseconds} ms, commands {seen.Total}, {progress.Last}");

            // Результат той самий в усіх раундах: 10 комірок ланцюжка формул рядка.
            Assert.Contains("\"written\":\"10\"", progress.Last, StringComparison.Ordinal);

            if (round == 0)
            {
                continue;
            }

            // Теплі раунди: граф залежностей — з кешу версії (0 звернень). Було (до L2b): 21 звернення, з них 1 —
            // читання графа (33 000 рядків у цьому світі).
            Assert.Equal(0, seen["SELECT cfg.FormulaDependency"]);
            Assert.True(seen.Total <= MaxWarmCommands, $"{seen.Total} звернень, стеля {MaxWarmCommands}.\n{seen.Format()}");
            warm.Add(watch.Elapsed);
        }

        // Було ~2,8–4,2 с (квадратичний пошук виробників у плані + читання графа); тепер ~0,1 с. Бюджет щедрий,
        // береться НАЙКРАЩИЙ теплий раунд, щоб шум чужого навантаження на машині не червонив.
        Assert.True(
            warm.Min() < TimeSpan.FromMilliseconds(1500),
            $"Найкращий теплий раунд: {warm.Min().TotalMilliseconds:F0} мс (бюджет 1500 мс).");
    }

    /// <summary>Стеля звернень до БД теплої задачі з однією коміркою (було 21 з читанням графа).</summary>
    private const int MaxWarmCommands = 20;

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Progress : IJobProgress
    {
        public string? Last { get; private set; }

        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            Last = message;
            return Task.CompletedTask;
        }
    }
}