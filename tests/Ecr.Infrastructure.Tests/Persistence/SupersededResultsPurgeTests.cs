// tests/Ecr.Infrastructure.Tests/Persistence/SupersededResultsPurgeTests.cs
using System.Data.Common;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Q-17 (рішення людини 28.10: у базі лишаються результати лише актуального прогону): швидкий шлях прибирання
/// після перемикання, інваріант «рядки лише для Current/Running», вимоги до читачів.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати виклик <c>PurgeSupersededAsync</c> у <c>RunCalculationHandler.CompleteAsync</c> -
/// інваріантні тести червоніють; прибрати перечитування прогонів після рядків у <c>ReadCurrentCoreAsync</c> - тест
/// гонитви читання віддає порожньо; прибрати цикл <c>UPDLOCK</c> у <c>LockCurrentSourceRunsAsync</c> - тест гонитви
/// переносу не блокується; прибрати страховку <c>NOT EXISTS ... Current/Running</c> у <c>SupersededResultsPurger</c> -
/// тест «актуальний не прибирається» червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class SupersededResultsPurgeTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Після_перемикання_рядки_замінених_прогонів_прибрано_а_прогони_й_актуальний_лишилися()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202602);
        await using var db = f.Builder.CreateContext();

        var a = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        await PurgeKit.InsertRowsAsync(db, f, a, results: 3, steps: 2, inputs: 2, "A");
        await CompleteAsync(f, a, PurgeKit.On());

        var b = await PurgeKit.AddRunAsync(db, f, "Running", T0.AddHours(1));
        await PurgeKit.InsertRowsAsync(db, f, b, results: 3, steps: 2, inputs: 2, "B");
        await CompleteAsync(f, b, PurgeKit.On());

        await using var check = f.Builder.CreateContext();
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(check, a));
        Assert.Equal((3, 2, 2), await PurgeKit.CountsAsync(check, b));

        // calc.CalculationRun лишається: хто/коли/статус - це історія, а не результат.
        var runs = await check.CalculationRuns.AsNoTracking()
            .Where(r => r.Id == a || r.Id == b).ToDictionaryAsync(r => r.Id, r => r.Status);
        Assert.Equal(CalculationRun.SupersededStatus, runs[a]);
        Assert.Equal(CalculationRun.CurrentStatus, runs[b]);

        // Читач віддає лише актуальний результат.
        var read = await new CalculationResultStore(check, new TestClock(T0))
            .ReadCurrentAsync(f.Document.DocumentId, f.Period, CancellationToken.None);
        Assert.Equal(3, read.Count);
        Assert.All(read, r => Assert.Equal("B", r.OutputCode));

        Assert.Equal(0, await PurgeKit.NonCurrentRowsAsync(check, f));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Старіший_прогін_відмовлений_як_старіший_теж_прибирається_а_актуальний_ціліє()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202603);
        await using var db = f.Builder.CreateContext();

        var older = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        var newer = await PurgeKit.AddRunAsync(db, f, "Running", T0.AddMinutes(1));
        await PurgeKit.InsertRowsAsync(db, f, older, results: 2, steps: 1, inputs: 1, "OLD");
        await PurgeKit.InsertRowsAsync(db, f, newer, results: 2, steps: 1, inputs: 1, "NEW");

        await CompleteAsync(f, newer, PurgeKit.On());
        await CompleteAsync(f, older, PurgeKit.On());

        await using var check = f.Builder.CreateContext();
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(check, older));
        Assert.Equal((2, 1, 1), await PurgeKit.CountsAsync(check, newer));
        Assert.Equal(0, await PurgeKit.NonCurrentRowsAsync(check, f));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вимкнене_прибирання_нічого_не_видаляє()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202604);
        await using var db = f.Builder.CreateContext();

        var a = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        await PurgeKit.InsertRowsAsync(db, f, a, 3, 2, 2, "A");
        await CompleteAsync(f, a, SupersededPurgeOptions.Off);
        var b = await PurgeKit.AddRunAsync(db, f, "Running", T0.AddHours(1));
        await PurgeKit.InsertRowsAsync(db, f, b, 3, 2, 2, "B");
        await CompleteAsync(f, b, SupersededPurgeOptions.Off);

        await using var check = f.Builder.CreateContext();
        Assert.Equal((3, 2, 2), await PurgeKit.CountsAsync(check, a));
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 7)]
    [InlineData(7, 7)]
    [InlineData(8, 7)]
    [InlineData(500, 1200)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Межі_пакета_видаляють_усе_і_лише_свій_прогін_і_повтор_нічого_не_робить(int batch, int rows)
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202605);
        await using var db = f.Builder.CreateContext();

        var victim = await PurgeKit.AddRunAsync(db, f, "Superseded", T0);
        var keep = await PurgeKit.AddRunAsync(db, f, "Current", T0.AddHours(1));
        await PurgeKit.InsertRowsAsync(db, f, victim, rows, rows, rows, "V");
        await PurgeKit.InsertRowsAsync(db, f, keep, 5, 5, 5, "K");

        var store = new CalculationResultStore(db, new TestClock(T0), purgeOptions: PurgeKit.On(batch));
        var run = new SupersededRun(victim, f.Period, f.Document.DocumentId);

        Assert.Equal(rows * 3, await store.PurgeSupersededRunsAsync([run], CancellationToken.None));
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, victim));
        Assert.Equal((5, 5, 5), await PurgeKit.CountsAsync(db, keep));

        // Ідемпотентність: повтор знаходить нуль.
        Assert.Equal(0, await store.PurgeSupersededRunsAsync([run], CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Актуальний_і_працюючий_прогони_не_прибираються_навіть_якщо_їх_передали()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202606);
        await using var db = f.Builder.CreateContext();

        var current = await PurgeKit.AddRunAsync(db, f, "Current", T0);
        var running = await PurgeKit.AddRunAsync(db, f, "Running", T0.AddHours(1), documentScoped: false);
        await PurgeKit.InsertRowsAsync(db, f, current, 3, 2, 2, "C");
        await PurgeKit.InsertRowsAsync(db, f, running, 3, 2, 2, "R");

        var store = new CalculationResultStore(db, new TestClock(T0), purgeOptions: PurgeKit.On());
        Assert.Equal(
            0,
            await store.PurgeSupersededRunsAsync(
                [new SupersededRun(current, f.Period, f.Document.DocumentId), new SupersededRun(running, f.Period, null)],
                CancellationToken.None));

        Assert.Equal((3, 2, 2), await PurgeKit.CountsAsync(db, current));
        Assert.Equal((3, 2, 2), await PurgeKit.CountsAsync(db, running));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_без_періоду_прибирає_партицію_нуль()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202607);
        await using var db = f.Builder.CreateContext();

        var victim = await PurgeKit.AddRunAsync(db, f, "Superseded", T0, documentScoped: false, noPeriod: true);
        await PurgeKit.InsertRowsAsync(db, f, victim, 4, 3, 3, "Z", period: 0);

        var store = new CalculationResultStore(db, new TestClock(T0), purgeOptions: PurgeKit.On(3));
        Assert.Equal(10, await store.PurgeSupersededRunsAsync([new SupersededRun(victim, 0, null)], CancellationToken.None));
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, victim));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_трейсу_кроки_й_входи_лишаються_коли_IncludeTrace_вимкнено()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202608);
        await using var db = f.Builder.CreateContext();

        var victim = await PurgeKit.AddRunAsync(db, f, "Superseded", T0);
        await PurgeKit.InsertRowsAsync(db, f, victim, 3, 2, 2, "V");

        var store = new CalculationResultStore(
            db, new TestClock(T0), purgeOptions: PurgeKit.On() with { IncludeTrace = false });
        await store.PurgeSupersededRunsAsync([new SupersededRun(victim, f.Period, f.Document.DocumentId)], CancellationToken.None);

        Assert.Equal((0, 2, 2), await PurgeKit.CountsAsync(db, victim));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_прибирання_не_кидається_і_повертає_нуль()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202609);
        await using var db = f.Builder.CreateContext();
        var victim = await PurgeKit.AddRunAsync(db, f, "Superseded", T0);
        await PurgeKit.InsertRowsAsync(db, f, victim, 2, 1, 1, "V");

        // Скасований токен у середині не збій; збій імітуємо знищеним з'єднанням: контекст уже звільнений.
        var disposed = f.Builder.CreateContext();
        await disposed.DisposeAsync();
        var store = new CalculationResultStore(disposed, new TestClock(T0), purgeOptions: PurgeKit.On());

        Assert.Equal(
            0,
            await store.PurgeSupersededRunsAsync(
                [new SupersededRun(victim, f.Period, f.Document.DocumentId)], CancellationToken.None));
        Assert.Equal((2, 1, 1), await PurgeKit.CountsAsync(db, victim));
    }

    /// <remarks>
    /// Гонитва R1: між читанням ідентифікаторів актуальних прогонів і читанням рядків закомічено перемикання +
    /// прибирання. Без перечитування прогонів читач віддав би ПОРОЖНЬО (рядки старого прогону вже прибрано), з ним -
    /// повні рядки нового. 20 ітерацій на тому самому документі (правило таймінгових тестів: N/20).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читання_актуальних_під_час_перемикання_і_прибирання_віддає_повний_новий_результат_20_із_20()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202610);
        await using var db = f.Builder.CreateContext();

        var current = await PurgeKit.AddRunAsync(db, f, "Running", T0);
        await PurgeKit.InsertRowsAsync(db, f, current, 3, 1, 1, "I0");
        await CompleteAsync(f, current, PurgeKit.On());

        for (var i = 1; i <= 20; i++)
        {
            var next = await PurgeKit.AddRunAsync(db, f, "Running", T0.AddMinutes(i));
            await PurgeKit.InsertRowsAsync(db, f, next, 3, 1, 1, "I" + i);

            var interceptor = new BeforeResultsReadInterceptor(() => CompleteAsync(f, next, PurgeKit.On()));
            await using var readDb = sql.CreateContext(interceptor);
            var rows = await new CalculationResultStore(readDb, new TestClock(T0))
                .ReadCurrentAsync(f.Document.DocumentId, f.Period, CancellationToken.None);

            Assert.True(interceptor.Fired, $"ітерація {i}: гонитву не відтворено");
            Assert.True(rows.Count == 3, $"ітерація {i}: прочитано {rows.Count} рядків замість 3 (порожньо/неповно)");
            Assert.All(rows, r => Assert.Equal("I" + i, r.OutputCode));
        }
    }

    /// <remarks>
    /// Гонитва R2: поки перенос тримає транзакцію, паралельне перемикання не може зняти актуальність із джерела (а отже
    /// й прибрати його рядки) - UPDATE статусу блокується до коміту. 20 ітерацій.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перенос_тримає_замок_на_прогонах_джерелах_до_коміту_20_із_20()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202611);
        await using var setup = f.Builder.CreateContext();
        var source = await PurgeKit.AddRunAsync(setup, f, "Current", T0);
        await PurgeKit.InsertRowsAsync(setup, f, source, 3, 0, 0, "S");

        for (var i = 1; i <= 20; i++)
        {
            var target = await PurgeKit.AddRunAsync(setup, f, "Running", T0.AddMinutes(i));

            await using var carryDb = f.Builder.CreateContext();
            await using var tx = await carryDb.Database.BeginTransactionAsync();
            var carried = await new CalculationResultStore(carryDb, new TestClock(T0))
                .CarryOverResultsAsync(target, f.Document.DocumentId, [f.MethodologyId], CancellationToken.None);
            Assert.Equal(3, carried);

            // Друга сесія намагається зняти актуальність із джерела: замок на рядку прогону тримає перенос.
            await using var other = f.Builder.CreateContext();
            var blocked = await Assert.ThrowsAsync<SqlException>(
                () => other.Database.ExecuteSqlAsync(
                    $"SET LOCK_TIMEOUT 200; UPDATE calc.CalculationRun SET Status = N'Superseded' WHERE Id = {source}"));
            Assert.Equal(1222, blocked.Number);

            await tx.RollbackAsync();
            await setup.CalculationRuns.Where(r => r.Id == target).ExecuteUpdateAsync(
                s => s.SetProperty(r => r.Status, "Failed"));
        }

        // Замок знято відкатом: тепер зняття актуальності проходить.
        await using var after = f.Builder.CreateContext();
        Assert.Equal(
            1,
            await after.Database.ExecuteSqlAsync(
                $"UPDATE calc.CalculationRun SET Status = N'Superseded' WHERE Id = {source}"));
    }

    /// <remarks>
    /// Пін зміни поведінки (R12, ADR): гард <c>strictBackdated</c> (<c>AnyCalculationResultsAsync</c>) бачить лише
    /// наявні рядки результатів і архів. Після прибирання версія, чиї єдині рядки належали замінених прогонам, більше
    /// «не використана»; рядки актуального прогону блокують як і раніше. Прогін не фіксує версій, тож гард «за
    /// прогонами» без нового зв'язку неможливий - це рішення людини (див. звіт).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Гард_використання_версії_після_прибирання_бачить_лише_актуальні_рядки()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202612);
        await using var db = f.Builder.CreateContext();

        var superseded = await PurgeKit.AddRunAsync(db, f, "Superseded", T0);
        await PurgeKit.InsertRowsAsync(db, f, superseded, 2, 0, 0, "S");
        var methodologies = new MethodologyStore(db);

        Assert.True(await methodologies.AnyCalculationResultsAsync([f.VersionId], CancellationToken.None));

        await new CalculationResultStore(db, new TestClock(T0), purgeOptions: PurgeKit.On())
            .PurgeSupersededRunsAsync([new SupersededRun(superseded, f.Period, f.Document.DocumentId)], CancellationToken.None);
        Assert.False(await methodologies.AnyCalculationResultsAsync([f.VersionId], CancellationToken.None));

        var current = await PurgeKit.AddRunAsync(db, f, "Current", T0.AddHours(1));
        await PurgeKit.InsertRowsAsync(db, f, current, 1, 0, 0, "C");
        Assert.True(await methodologies.AnyCalculationResultsAsync([f.VersionId], CancellationToken.None));
    }

    private static async Task CompleteAsync(PurgeStand f, long runId, SupersededPurgeOptions options)
    {
        await using var db = f.Builder.CreateContext();
        var clock = new TestClock(T0);

        var handler = new RunCalculationHandler(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock, purgeOptions: options),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

        await handler.CompleteAsync(runId, new ModuleProfile(), CancellationToken.None);
    }

    /// <summary>Перед першим читанням <c>calc.CalculationResult</c> виконує дію (раз) - відтворює гонитву між двома запитами читача.</summary>
    private sealed class BeforeResultsReadInterceptor(Func<Task> action) : DbCommandInterceptor
    {
        private int fired;

        public bool Fired => Volatile.Read(ref fired) == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[calc].[CalculationResult]", StringComparison.Ordinal)
                && Interlocked.Exchange(ref fired, 1) == 0)
            {
                await action().ConfigureAwait(false);
            }

            return result;
        }
    }
}

/// <summary>Ланцюг документа й версії методології для тестів прибирання.</summary>
internal sealed record PurgeStand(
    TestDocumentBuilder Builder, TestDocument Document, int MethodologyId, int VersionId, int UnitId)
{
    public int Period => Document.PeriodKey.Value;
}

/// <summary>Спільні помічники тестів прибирання (швидкий шлях і нічна задача).</summary>
internal static class PurgeKit
{
    /// <summary>Прибирання увімкнено, видалення дозволено; пауз немає (тести).</summary>
    public static SupersededPurgeOptions On(int batch = SupersededPurgeOptions.DefaultBatchSize)
        => SupersededPurgeOptions.Off with
        {
            Enabled = true,
            DeleteEnabled = true,
            BatchSize = batch,
            PauseBetweenBatches = TimeSpan.Zero,
            CheckpointEveryBatches = 1,
        };

    public static async Task<PurgeStand> ArrangeAsync(SqlServerFixture sql, int periodKey)
        => await ArrangeAsync(sql.ConnectionString, periodKey);

    public static async Task<PurgeStand> ArrangeAsync(string connectionString, int periodKey)
    {
        var builder = new TestDocumentBuilder(connectionString);
        var document = await builder.BuildAsync(periodKey: periodKey);
        await using var db = builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(
            EcrCode.Create($"PG_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "purge" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0", Ecr.Domain.Enums.CalculationLevel.Configuration, createdByUserId: 1, T(0));
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        return new PurgeStand(builder, document, methodology.Id, version.Id, unitId);
    }

    private static DateTime T(int minutes) => new DateTime(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);

    /// <summary>Новий прогін документа (або проєкту) у заданому статусі.</summary>
    public static async Task<long> AddRunAsync(
        EcrDbContext db, PurgeStand f, string status, DateTime startedAt, bool documentScoped = true,
        bool noPeriod = false)
    {
        var run = new CalculationRun(
            f.Document.ProjectId,
            noPeriod ? null : f.Period,
            triggeredByUserId: null,
            startedAt,
            documentScoped ? f.Document.DocumentId : null);

        if (status != "Running")
        {
            run.Complete(status == "Current" ? "Succeeded" : status, startedAt.AddMinutes(1), "{}", errorMessage: null);
            if (status == "Current")
            {
                run.MakeCurrent();
            }
        }

        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>Рядки прогону set-based (результати, кроки, входи); <paramref name="code"/> - код виходу, за яким видно «чий».</summary>
    public static async Task InsertRowsAsync(
        EcrDbContext db, PurgeStand f, long runId, int results, int steps, int inputs, string code, int? period = null)
    {
        var p = period ?? f.Period;
        var total = Math.Max(1, results + steps + inputs);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @first sql_variant;
            EXEC sys.sp_sequence_get_range
                @sequence_name = N'calc.CalculationResultSeq',
                @range_size = {total},
                @range_first_value = @first OUTPUT;
            DECLARE @base bigint = CONVERT(bigint, @first);

            INSERT INTO calc.CalculationResult
                (Id, PeriodKey, CalculationRunId, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId, Kind)
            SELECT @base + n.Num - 1, {p}, {runId}, {f.VersionId}, {f.Document.DocumentId},
                   CONCAT(N'R-', n.Num), {code}, 1, {f.UnitId}, 0
              FROM (SELECT TOP ({results}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Num
                      FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n;

            INSERT INTO calc.CalculationStep (Id, PeriodKey, CalculationRunId, StepOrder, StepCode, MaskedZero)
            SELECT @base + {results} + n.Num - 1, {p}, {runId}, n.Num, N'S', 0
              FROM (SELECT TOP ({steps}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Num
                      FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n;

            INSERT INTO calc.CalculationInput (Id, PeriodKey, CalculationRunId, DocumentId, ArgumentCode)
            SELECT @base + {results} + {steps} + n.Num - 1, {p}, {runId}, {f.Document.DocumentId}, N'A'
              FROM (SELECT TOP ({inputs}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS Num
                      FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS n;
            """);
    }

    public static async Task<(int Results, int Steps, int Inputs)> CountsAsync(EcrDbContext db, long runId)
        => (
            await db.CalculationResults.AsNoTracking().CountAsync(r => r.CalculationRunId == runId),
            await db.CalculationSteps.AsNoTracking().CountAsync(r => r.CalculationRunId == runId),
            await db.CalculationInputs.AsNoTracking().CountAsync(r => r.CalculationRunId == runId));

    /// <summary>Рядки трьох таблиць документа/проєкту стенда, чий прогін не Current/Running (інваріант = 0).</summary>
    public static async Task<int> NonCurrentRowsAsync(EcrDbContext db, PurgeStand f)
    {
        var runIds = await db.CalculationRuns.AsNoTracking()
            .Where(r => r.ProjectId == f.Document.ProjectId && r.Status != "Current" && r.Status != "Running")
            .Select(r => r.Id).ToListAsync();

        return await db.CalculationResults.AsNoTracking().CountAsync(r => runIds.Contains(r.CalculationRunId))
               + await db.CalculationSteps.AsNoTracking().CountAsync(r => runIds.Contains(r.CalculationRunId))
               + await db.CalculationInputs.AsNoTracking().CountAsync(r => runIds.Contains(r.CalculationRunId));
    }
}
