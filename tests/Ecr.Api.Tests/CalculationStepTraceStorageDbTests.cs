// tests/Ecr.Api.Tests/CalculationStepTraceStorageDbTests.cs
using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ D-565 (PERF-03) на справжньому SQL Server: кроки трейсу — одним масовим записом (а не EF-пакетами MERGE), стиснені
/// (<c>TraceJsonGz</c>, формат <c>COMPRESS(N'…')</c>) і читаються назад тими самими; старі нестиснені рядки читаються як
/// і раніше; без прапора методології прогін не пише жодного кроку, а результати й входи — ті самі.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЇ (локально, не закомічені): у <c>CalculationResultStore.WriteStepsAsync</c> прибрати гілку
/// <c>UseStepBulk</c> — червоний <see cref="Кроки_йдуть_одним_масовим_записом_без_EF_команд"/> (EF-команди INSERT/MERGE у
/// <c>calc.CalculationStep</c>); писати в масовому записі <c>TraceJson</c> текстом замість <c>TraceJsonGz</c> — червоний
/// той самий тест (колонка <c>TraceJson</c> не NULL); не обрізати вираз у масовому записі — червоний
/// <see cref="Масовий_запис_кроків_дає_ті_самі_рядки_що_й_EF"/> (2628 на <c>nvarchar(2000)</c>); не брати
/// <c>ResultIdOf</c> — червоний той самий (кроки без результату); у <see cref="CalculationStep.TraceJson"/> читати лише
/// стиснену колонку — червоний <see cref="Старі_нестиснені_кроки_читаються_поруч_із_новими"/>; ігнорувати
/// <c>recordSteps</c> у записувачі — червоний <see cref="Без_прапора_жодного_кроку_а_результати_й_входи_ті_самі"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationStepTraceStorageDbTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);

    private const int Rows = 20;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task Кроки_йдуть_одним_масовим_записом_без_EF_команд()
    {
        var f = await ArrangeAsync();
        var outputs = Outputs(f);
        var run = await StartRunAsync(f);

        var recorder = new SqlTextRecorder();
        var log = new StepBulkLog();
        await using (var db = Context(recorder))
        {
            var writer = new CalculationOutputWriter(
                new CalculationResultStore(db, new TestClock(Now), logger: log), new UnitOfWork(db, new TestClock(Now)));
            await writer.WriteAsync(run, outputs, TraceLevel.Full, CancellationToken.None);
        }

        // Один масовий запис кроків — і жодної EF-команди вставки в calc.CalculationStep (доти: пакети MERGE по 42).
        Assert.Equal(1, log.StepBulkWrites);
        Assert.Equal(Rows * 3, log.StepRows);
        Assert.DoesNotContain(
            recorder.Commands,
            c => c.Contains("[calc].[CalculationStep]", StringComparison.Ordinal)
                 && (c.Contains("INSERT", StringComparison.OrdinalIgnoreCase) || c.Contains("MERGE", StringComparison.OrdinalIgnoreCase)));

        // Стиснене — у TraceJsonGz, текстова колонка порожня; SQL DECOMPRESS дає рівно той самий текст, що й код.
        await using var check = f.Builder.CreateContext();
        var expected = outputs
            .SelectMany(o => o.Trace.Select(s => (o.SourceRowKey, s.StepCode, s.SubstanceEntryId, Text: s.Detail ?? s.TraceJson)))
            .ToDictionary(x => $"{x.SourceRowKey}|{x.StepCode}|{x.SubstanceEntryId}", x => x.Text, StringComparer.Ordinal);

        var stored = await check.Database
            .SqlQuery<StoredStep>($"""
                SELECT SourceRowKey, StepCode, CAST(SubstanceEntryId AS bigint) AS SubstanceEntryId,
                       CASE WHEN TraceJson IS NULL THEN 0 ELSE 1 END AS HasText,
                       CAST(DECOMPRESS(TraceJsonGz) AS nvarchar(max)) AS Decompressed
                  FROM calc.CalculationStep
                 WHERE PeriodKey = {f.PeriodKey} AND CalculationRunId = {run}
                """)
            .ToListAsync();

        Assert.Equal(Rows * 3, stored.Count);
        Assert.All(stored, s =>
        {
            Assert.Equal(0, s.HasText);
            Assert.Equal(expected[$"{s.SourceRowKey}|{s.StepCode}|{s.SubstanceEntryId}"], s.Decompressed);
        });

        // І через EF — та сама властивість TraceJson, якою користуються читачі.
        var steps = await check.CalculationSteps.AsNoTracking().Where(s => s.CalculationRunId == run).ToListAsync();
        Assert.All(steps, s => Assert.Equal(expected[$"{s.SourceRowKey}|{s.StepCode}|{s.SubstanceEntryId}"], s.TraceJson));
    }

    /// <remarks>
    /// Той самий набір (з виразом, довшим за колонку, речовиною, маскуванням і кроком без результату) — масовим записом
    /// і EF-шляхом (<c>stepBulkThreshold: int.MaxValue</c>): усі колонки кроку, крім Id, і прив'язка до результату однакові.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task Масовий_запис_кроків_дає_ті_самі_рядки_що_й_EF()
    {
        var f = await ArrangeAsync();
        var outputs = Outputs(f);

        var bulkRun = await StartRunAsync(f);
        var bulkLog = await WriteAsync(f, bulkRun, outputs, stepBulkThreshold: null);

        var efRun = await StartRunAsync(f);
        var efLog = await WriteAsync(f, efRun, outputs, stepBulkThreshold: int.MaxValue);

        Assert.Equal(1, bulkLog.StepBulkWrites);
        Assert.Equal(0, efLog.StepBulkWrites);

        await using var db = f.Builder.CreateContext();
        var bulk = await ShapeAsync(db, bulkRun);
        var ef = await ShapeAsync(db, efRun);

        Assert.Equal(Rows * 3, bulk.Count);
        Assert.Equal(ef, bulk);

        // Вираз понад 2000 обрізано з «…»; крок C без виходу — без результату; A і B — з результатом.
        Assert.All(bulk.Where(s => s.Contains("|LONG|", StringComparison.Ordinal)), s => Assert.Contains("…|", s, StringComparison.Ordinal));
        Assert.Equal(Rows, bulk.Count(s => s.EndsWith("|noresult", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task Без_прапора_жодного_кроку_а_результати_й_входи_ті_самі()
    {
        var f = await ArrangeAsync();
        var outputs = Outputs(f);

        var onRun = await StartRunAsync(f);
        await WriteAsync(f, onRun, outputs, stepBulkThreshold: null, recordSteps: true);

        var offRun = await StartRunAsync(f);
        await WriteAsync(f, offRun, outputs, stepBulkThreshold: null, recordSteps: false);

        await using var db = f.Builder.CreateContext();
        Assert.Equal(Rows * 3, await db.CalculationSteps.CountAsync(s => s.CalculationRunId == onRun));
        Assert.Equal(0, await db.CalculationSteps.CountAsync(s => s.CalculationRunId == offRun));

        Assert.Equal(await ResultsAsync(db, onRun), await ResultsAsync(db, offRun));
        var inputs = await InputsAsync(db, onRun);
        Assert.Equal(Rows * 2, inputs.Count);
        Assert.Equal(inputs, await InputsAsync(db, offRun));
    }

    /// <remarks>
    /// Рядок до D-565 — текст у <c>TraceJson</c>, <c>TraceJsonGz</c> = NULL (вставлено сирим SQL, як його лишила стара
    /// версія) — читається поруч із новим стисненим кроком того самого прогону; великої міграції немає.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task Старі_нестиснені_кроки_читаються_поруч_із_новими()
    {
        var f = await ArrangeAsync();
        var run = await StartRunAsync(f);
        const string legacy = """{"v":1,"expr":"@A / @B","result":null,"error":"#DIV/0","inputs":[]}""";

        await using (var db = f.Builder.CreateContext())
        {
            var id = await NextIdAsync(db);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO calc.CalculationStep (PeriodKey, Id, CalculationRunId, StepOrder, StepCode, Expression, Value, TraceJson, MaskedZero, DocumentId, SourceRowKey)
                VALUES ({f.PeriodKey}, {id}, {run}, 1, N'OLD', N'@A / @B', NULL, {legacy}, 0, {f.DocumentId}, N'R-OLD');
                """);
        }

        await WriteAsync(f, run, [Outputs(f)[0]], stepBulkThreshold: null);

        await using var check = f.Builder.CreateContext();
        var steps = await check.CalculationSteps.AsNoTracking().Where(s => s.CalculationRunId == run).ToListAsync();

        var old = Assert.Single(steps, s => s.StepCode == "OLD");
        Assert.Equal(legacy, old.TraceJson);
        Assert.Null(old.TraceJsonGz);

        var fresh = steps.Where(s => s.StepCode != "OLD").ToList();
        Assert.Equal(3, fresh.Count);
        Assert.All(fresh, s =>
        {
            Assert.Null(s.TraceJsonText);
            Assert.NotNull(s.TraceJson);
        });

        // Той самий вираз, яким інструменти (tools/land-test-doc, PostCheck) читають обидва формати.
        var errors = await check.Database
            .SqlQuery<string>($"""
                SELECT JSON_VALUE(COALESCE(TraceJson, CAST(DECOMPRESS(TraceJsonGz) AS nvarchar(max))), '$.error') AS Value
                  FROM calc.CalculationStep WHERE PeriodKey = {f.PeriodKey} AND CalculationRunId = {run} AND StepCode = N'OLD'
                """)
            .ToListAsync();
        Assert.Equal(["#DIV/0"], errors);
    }

    /// <remarks>
    /// Масовий запис кроків іде в транзакції записувача ДО <c>SaveChanges</c> входів: падіння збереження (вхід із кодом,
    /// довшим за колонку) відкочує й кроки — методологія записана цілком або ніяк. ⛔ Мутація: писати кроки поза
    /// транзакцією записувача (власне з'єднання / без <c>transaction</c>) — червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task Масово_записані_кроки_відкочуються_коли_падає_наступний_SaveChanges()
    {
        var f = await ArrangeAsync();
        var outputs = Outputs(f);
        outputs[^1] = outputs[^1] with { Inputs = [new CalculationArgument(new string('A', 200), 1m, null, null)] };
        var run = await StartRunAsync(f);

        var log = new StepBulkLog();
        await using (var db = f.Builder.CreateContext())
        {
            var writer = new CalculationOutputWriter(
                new CalculationResultStore(db, new TestClock(Now), logger: log), new UnitOfWork(db, new TestClock(Now)));
            await Assert.ThrowsAnyAsync<Exception>(() => writer.WriteAsync(run, outputs, TraceLevel.Full, CancellationToken.None));
        }

        Assert.Equal(1, log.StepBulkWrites);
        await using var check = f.Builder.CreateContext();
        Assert.Equal(0, await check.CalculationSteps.CountAsync(s => s.CalculationRunId == run));
        Assert.Equal(0, await check.CalculationResults.CountAsync(r => r.CalculationRunId == run));
    }

    /// <remarks>
    /// <c>MethodologyStore.GetTraceStepMethodologyIdsAsync</c> на SQL: лише названі методології з прапором, без повторів;
    /// порожній запит — без звернення. ⛔ Мутація: прибрати умову <c>TraceSteps</c> — червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-565")]
    public async Task Сховище_віддає_лише_названі_методології_з_прапором()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        int on, off, other;
        await using (var db = builder.CreateContext())
        {
            Methodology Make(string key)
            {
                var m = new Methodology(
                    EcrCode.Create($"D565{key}_{Guid.NewGuid():N}"[..20]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
                db.Methodologies.Add(m);
                return m;
            }

            var a = Make("A");
            var b = Make("B");
            var c = Make("C");
            a.SetTraceSteps(true);
            c.SetTraceSteps(true);
            await db.SaveChangesAsync();
            (on, off, other) = (a.Id, b.Id, c.Id);
        }

        await using var read = builder.CreateContext();
        var store = new MethodologyStore(read, 1000);
        Assert.Equal([on], await store.GetTraceStepMethodologyIdsAsync([on, off, on], CancellationToken.None));
        Assert.Equal(
            [on, other],
            (await store.GetTraceStepMethodologyIdsAsync([on, off, other], CancellationToken.None)).Order());
        Assert.Empty(await store.GetTraceStepMethodologyIdsAsync([], CancellationToken.None));
    }

    private static async Task<long> NextIdAsync(EcrDbContext db)
        => (await db.Database.SqlQuery<long>($"""
                DECLARE @first sql_variant;
                EXEC sys.sp_sequence_get_range @sequence_name = N'calc.CalculationResultSeq', @range_size = 1,
                     @range_first_value = @first OUTPUT;
                SELECT CONVERT(bigint, @first) AS Value;
                """).ToListAsync())[0];

    private async Task<StepBulkLog> WriteAsync(
        Fixture f, long runId, IReadOnlyList<CalculationOutput> outputs, int? stepBulkThreshold, bool recordSteps = true)
    {
        await using var db = f.Builder.CreateContext();
        var log = new StepBulkLog();
        var writer = new CalculationOutputWriter(
            new CalculationResultStore(db, new TestClock(Now), logger: log, stepBulkThreshold: stepBulkThreshold),
            new UnitOfWork(db, new TestClock(Now)));

        await writer.WriteAsync(runId, outputs, TraceLevel.Full, CancellationToken.None, recordSteps);
        return log;
    }

    private EcrDbContext Context(SqlTextRecorder recorder)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(recorder)
            .Options);

    /// <summary>Усі колонки кроку, крім Id; результат — його вміст, а не Id.</summary>
    private static async Task<List<string>> ShapeAsync(EcrDbContext db, long runId)
    {
        var results = await db.CalculationResults.AsNoTracking().Where(r => r.CalculationRunId == runId).ToListAsync();
        var byId = results.ToDictionary(r => r.Id);
        var steps = await db.CalculationSteps.AsNoTracking().Where(s => s.CalculationRunId == runId).ToListAsync();

        return [.. steps
            .Select(s =>
                $"{s.PeriodKey}|{s.DocumentId}|{s.TableInstanceId}|{s.SourceRowKey}|{s.SubstanceEntryId}|{s.StepOrder}|"
                + $"{s.StepCode}|{s.Expression}|{s.Value?.ToString(CultureInfo.InvariantCulture)}|{s.Masked}|{s.TraceJson}|"
                + (s.ResultId is { } id && byId.TryGetValue(id, out var r)
                    ? $"{r.OutputCode}|{r.SubstanceEntryId}|{r.Value.ToString(CultureInfo.InvariantCulture)}"
                    : "noresult"))
            .Order(StringComparer.Ordinal)];
    }

    private static async Task<List<string>> ResultsAsync(EcrDbContext db, long runId)
        => [.. (await db.CalculationResults.AsNoTracking().Where(r => r.CalculationRunId == runId).ToListAsync())
            .Select(r => $"{r.DocumentId}|{r.TableInstanceId}|{r.SourceRowKey}|{r.MethodologyVersionId}|{r.OutputCode}|"
                         + $"{r.SubstanceEntryId}|{r.Kind}|{r.UnitId}|{r.Value.ToString(CultureInfo.InvariantCulture)}")
            .Order(StringComparer.Ordinal)];

    private static async Task<List<string>> InputsAsync(EcrDbContext db, long runId)
        => [.. (await db.CalculationInputs.AsNoTracking().Where(i => i.CalculationRunId == runId).ToListAsync())
            .Select(i => $"{i.DocumentId}|{i.TableInstanceId}|{i.SourceRowKey}|{i.ArgumentCode}|"
                         + $"{i.Value?.ToString(CultureInfo.InvariantCulture)}|{i.ValueString}|{i.UnitId}")
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// 20 рядків × (2 значення, 3 кроки, 2 входи): A і B — з результатом (B по речовині), C — без виходу; вираз A довший
    /// за колонку (2000), B замасковано; деталізація — <c>TraceJson</c> v1 з кирилицею.
    /// </summary>
    private static List<CalculationOutput> Outputs(Fixture f)
        => [.. Enumerable.Range(1, Rows).Select(i => new CalculationOutput(
            f.DocumentId,
            $"R{i:000}",
            [
                new CalculationOutputValue(f.VersionId, null, "A", i / 3m, f.UnitId),
                new CalculationOutputValue(f.VersionId, 900 + (i % 3), "B", -i * 2.5m, f.UnitId),
            ],
            [
                new CalculationTraceStep(
                    1, "A", "LONG|" + new string('x', 2100), i / 3m, TraceJson: null,
                    Detail: $$"""{"v":1,"expr":"@Об'єм / 3","result":"{{(i / 3m).ToString(CultureInfo.InvariantCulture)}}","inputs":[{"kind":"arg","code":"Об'єм","value":"{{i}}"}]}"""),
                new CalculationTraceStep(
                    2, "B", "@X * 2.5", -i * 2.5m, TraceJson: null, MaskedZeroReason.Infinity,
                    Detail: """{"v":1,"expr":"@X * 2.5","masked":"Infinity","inputs":[]}""", SubstanceEntryId: 900 + (i % 3)),
                new CalculationTraceStep(3, "C", "@X", null, TraceJson: "#DIV/0"),
            ],
            [
                new CalculationArgument("Об'єм", i, null, f.UnitId),
                new CalculationArgument("Category", null, "V8", null),
            ])
        {
            TableInstanceId = 7_000 + (i % 2),
        })];

    private static async Task<long> StartRunAsync(Fixture f)
    {
        await using var db = f.Builder.CreateContext();
        var run = new CalculationRun(f.ProjectId, f.PeriodKey, triggeredByUserId: null, Now);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task<Fixture> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: 209910);
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(
            EcrCode.Create($"D565_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        return new Fixture(builder, document.DocumentId, document.ProjectId, document.PeriodKey.Value, version.Id, unitId);
    }

    private sealed record Fixture(
        TestDocumentBuilder Builder, long DocumentId, int ProjectId, int PeriodKey, int VersionId, int UnitId);

    private sealed record StoredStep(string? SourceRowKey, string StepCode, long? SubstanceEntryId, int HasText, string? Decompressed);

    /// <summary>Тексти EF-команд контексту записувача.</summary>
    private sealed class SqlTextRecorder : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Enqueue(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Рядки журналу «кроків трейсу записано масово» (D-565) — доказ, що масовий шлях спрацював.</summary>
    private sealed class StepBulkLog : ILogger<CalculationResultStore>
    {
        private int writes;
        private int rows;

        public int StepBulkWrites => Volatile.Read(ref writes);

        public int StepRows => Volatile.Read(ref rows);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).StartsWith("D-565:", StringComparison.Ordinal))
            {
                return;
            }

            Interlocked.Increment(ref writes);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> fields
                && fields.FirstOrDefault(p => p.Key == "Rows").Value is int count)
            {
                Interlocked.Add(ref rows, count);
            }
        }
    }
}
