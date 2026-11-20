using System.Globalization;
using Ecr.Application;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Scripting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// MS: НАСКРІЗНИЙ доказ прохідного перерахунку читачів колонки <c>Calculated</c> місяця з чужим власником
/// (<c>RecalculationJob.EnqueueOwnerPeriodReadersAsync</c>, версія YearWithMonthColumns, маршрутизоване зберігання).
/// Справжні <c>RecalculationJob</c> → оркестратор → <c>ScriptCalculationModule</c> → роутер → сховище результатів на
/// реальному SQL Server; у тесті підмінено лише планувальник задач: він записує поставлені coalesced-проходи, а тест
/// виконує їх тим самим справжнім завданням.
/// </summary>
/// <remarks>
/// <para>
/// Світ: документ місячного проєкту, таблиця TA «місяці в колонках» з колонками <c>IN_M01..12</c> (введення місяця),
/// <c>X_M01..12</c> (<c>Calculated</c>) і <c>ZRES</c> (<c>Calculated</c>, не місячна). Методологія-джерело S (скрипт
/// <c>output M02: kg = row.IN[2] * 2</c>) прив'язана до <c>X_M02</c>: власник місяця 2 — період 209902 (P). Методологія-читач
/// R (скрипт <c>output Z: kg = row.X[2]</c>) прив'язана до <c>ZRES</c> і читає <c>X_M02</c> у кожному періоді. Читач у
/// періоді Q (209903) бере число S з екземпляра періоду-власника P, тож прогін самого P без проходу лишив би Z(Q) старим.
/// </para>
/// <para>
/// Мутації: прибрати виклик <c>EnqueueOwnerPeriodReadersAsync</c> у <c>RecalculationJob.ExecuteAsync</c> — червоніє
/// перший тест; у <c>ReaderPassSkipAsync</c> завжди повертати <c>None</c> — див. примітку до другого тесту.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ScriptOwnerPeriodReadersRunSqlTests(SqlServerFixture sql)
{
    private const int Year = 2099;
    private static readonly DateTime Now = new(Year, 1, 15, 9, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Doc,
        IReadOnlyDictionary<int, long> Instances,
        IReadOnlyDictionary<int, long> Rows,
        IReadOnlyDictionary<int, int> InputColumns,
        string TableCode);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_лише_періоду_власника_ставить_прохід_і_читач_іншого_періоду_бачить_свіже_число()
    {
        var world = await ArrangeAsync();
        await using var harness = NewHarness();
        await harness.RefreshRegistryAsync();

        // Базовий прогін усього документа: S(2) = 10 * 2 = 20, читач скрізь бачить 20.
        await SetInputAsync(world, month: 2, 10m);
        // Читачі періодів 1 і 3 у цьому прогоні ще не бачать числа власника (2) — їм ставиться прохід (L2b).
        await harness.RunAsync(world, periodKey: null);
        await harness.RunPassesAsync(world);
        Assert.Equal("Z|20", await ReaderValueAsync(world, 3));
        Assert.Equal("Z|20", await ReaderValueAsync(world, 2));
        Assert.Empty(harness.Passes);

        // Правка входу ПЕРІОДУ-ВЛАСНИКА (P = 2) і прогін ЛИШЕ P.
        await SetInputAsync(world, month: 2, 30m);
        await harness.RunAsync(world, periodKey: Period(2));

        // Сам P уже свіжий; читач у Q (3) — ще ні, але прохід для нього (і для якоря 1) поставлено.
        Assert.Equal("Z|60", await ReaderValueAsync(world, 2));
        Assert.Equal("Z|20", await ReaderValueAsync(world, 3));
        Assert.Equal([Period(1), Period(3)], harness.Passes.Select(p => p.PeriodKey!.Value).Order().ToArray());
        Assert.All(harness.Passes, p => Assert.True(p.ForeignOwnerPass));

        // Виконуємо поставлені проходи справжнім завданням: число читача в Q свіже.
        await harness.RunPassesAsync(world);
        Assert.Equal("Z|60", await ReaderValueAsync(world, 3));
        Assert.Equal("Z|60", await ReaderValueAsync(world, 1));
        Assert.Empty(harness.Passes);   // прохід нових не ставить
    }

    /// <summary>
    /// Затверджений аркуш читача в Q: прохід його не перераховує (число лишається 20), аркуш отримує запис
    /// <c>SkippedApproved</c>; після Reopen і перерахунку Q число свіже. Якір (1), де затвердження немає, оновлюється.
    /// </summary>
    /// <remarks>
    /// Мутація «затверджене не пропускати»: у <c>ReaderPassSkipAsync</c> повертати <c>None</c> — прохід Q ставиться, а
    /// запис <c>SkippedApproved</c> тоді пише лише сам прохід (його зупиняє загальний гейт запису); докази — у звіті до коміту.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Затверджений_аркуш_читача_в_іншому_періоді_не_перераховується_а_після_Reopen_свіжий()
    {
        var world = await ArrangeAsync();
        await using var harness = NewHarness();
        await harness.RefreshRegistryAsync();

        await SetInputAsync(world, month: 2, 10m);
        await harness.RunAsync(world, periodKey: null);
        await harness.RunPassesAsync(world);
        Assert.Equal("Z|20", await ReaderValueAsync(world, 3));

        // Аркуш TA у Q (3) затверджено.
        await using (var db = world.Builder.CreateContext())
        {
            var state = new ApprovalState(world.Doc.DocumentId, world.Doc.SheetDefId, Period(3));
            state.Submit(userId: 5, Now);
            state.Approve(userId: 6, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        await SetInputAsync(world, month: 2, 30m);
        await harness.RunAsync(world, periodKey: Period(2));
        await harness.RunPassesAsync(world);

        // Затверджений Q — старе число й позначка «пропущено»; незатверджений якір — свіже.
        Assert.Equal("Z|20", await ReaderValueAsync(world, 3));
        Assert.Equal("Z|60", await ReaderValueAsync(world, 1));
        Assert.Equal(1, await SkippedMarksAsync(world, 3));
        Assert.Equal(0, await SkippedMarksAsync(world, 1));

        // Reopen → перерахунок Q: число свіже.
        await using (var db = world.Builder.CreateContext())
        {
            var state = await db.ApprovalStates.SingleAsync(s =>
                s.DocumentId == world.Doc.DocumentId && s.SheetDefId == world.Doc.SheetDefId && s.PeriodKey == Period(3));
            state.Reopen(userId: 6, "правка", Now);
            await db.SaveChangesAsync();
        }

        await harness.RunAsync(world, periodKey: Period(3));
        Assert.Equal("Z|60", await ReaderValueAsync(world, 3));
    }

    // ── читання ──────────────────────────────────────────────────────────

    /// <summary>Результат читача (вихід Z) у періоді: <c>Z|число</c>.</summary>
    private static async Task<string> ReaderValueAsync(World world, int month)
    {
        await using var db = world.Builder.CreateContext();
        var rows = await new CalculationResultStore(db, new TestClock(Now))
            .ReadCurrentAsync(world.Doc.DocumentId, Period(month), default);
        var z = rows.Where(r => r.OutputCode == "Z" && r.TableInstanceId == world.Instances[month]).ToList();
        if (z.Count != 1)
        {
            return $"Z×{z.Count}";
        }

        return $"Z|{z[0].Value:0.####}";
    }

    private static async Task<int> SkippedMarksAsync(World world, int month)
    {
        await using var db = world.Builder.CreateContext();
        var correlation = RecalculationSkips.AuditCorrelation(world.Doc.DocumentId, world.Doc.SheetDefId, Period(month));
        var entityType = RecalculationSkips.AuditEntityType;
        var operation = RecalculationSkips.AuditSkippedApproved;
        return await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM aud.StructureChange
                 WHERE EntityType = {entityType} AND Operation = {operation} AND CorrelationId = {correlation}
                """)
            .SingleAsync();
    }

    private static async Task SetInputAsync(World world, int month, decimal value)
    {
        await using var db = world.Builder.CreateContext();
        var column = world.InputColumns[month];
        var rowId = world.Rows[month];
        var cell = await db.CellValues.SingleOrDefaultAsync(c =>
            c.PeriodKeyValue == Period(month) && c.TableRowId == rowId && c.ColumnDefId == column);
        if (cell is null)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(Key(month), rowId, column), world.Doc.TableDefId, new CellValueData { ValueNumeric = value }));
        }
        else
        {
            cell.Apply(new CellValueData { ValueNumeric = value });
        }

        await db.SaveChangesAsync();
    }

    // ── стенд ────────────────────────────────────────────────────────────

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            periodKey: Period(1), columnCount: 1, rowCount: 1, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 0);

        var instances = new Dictionary<int, long> { [1] = doc.TableInstanceId };
        var rows = new Dictionary<int, long> { [1] = doc.RowIds[0] };
        for (var p = 2; p <= 3; p++)
        {
            var other = await builder.AddPeriodInstanceAsync(doc, Key(p));
            instances[p] = other.TableInstanceId;
            rows[p] = other.RowIds[0];
        }

        var inputs = new Dictionary<int, int>();
        int sourceColumn;
        int resultColumn;
        string tableCode;
        int unitId;
        await using (var db = builder.CreateContext())
        {
            foreach (var period in await db.Periods.Where(p => p.ProjectId == doc.ProjectId).ToListAsync())
            {
                period.AdvanceTo(PeriodState.Open, Now);
            }

            var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
            tableCode = table.Code;
            var columns = new List<ColumnDef>();
            for (var m = 1; m <= 12; m++)
            {
                var input = new ColumnDef(table.Id, EcrCode.Create(Code("IN", m)), Name("IN"), 10 + m, CellDataType.Decimal);
                input.SetMonth(true, (byte)m);
                var calculated = new ColumnDef(table.Id, EcrCode.Create(Code("X", m)), Name("X"), 30 + m, CellDataType.Calculated);
                calculated.SetMonth(true, (byte)m);
                columns.Add(input);
                columns.Add(calculated);
            }

            var result = new ColumnDef(table.Id, EcrCode.Create("ZRES"), Name("ZRES"), 99, CellDataType.Calculated);
            columns.Add(result);
            db.ColumnDefs.AddRange(columns);
            await db.SaveChangesAsync();

            for (var m = 1; m <= 12; m++)
            {
                inputs[m] = columns.Single(c => c.Code == Code("IN", m)).Id;
            }

            sourceColumn = columns.Single(c => c.Code == Code("X", 2)).Id;
            resultColumn = result.Id;
            unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE cfg.TemplateVersion SET Layout = {(int)TemplateLayout.YearWithMonthColumns} WHERE Id = {doc.TemplateVersionId}");
        }

        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var source = await PublishAsync(
            builder, doc, unitId, "S" + tag, "rows from " + tableCode + ";\noutput M02: kg = row.IN[2] * 2;",
            "M02", sourceColumn);
        var reader = await PublishAsync(
            builder, doc, unitId, "R" + tag, "rows from " + tableCode + ";\noutput Z: kg = row.X[2];", "Z", resultColumn);

        await using (var db = builder.CreateContext())
        {
            // Залежність читача від джерела — те, що пише матеріалізація публікації (ScriptMaterializer).
            db.MethodologyDependencies.Add(new MethodologyDependency(reader.MethodologyId, source.MethodologyId));
            await db.SaveChangesAsync();
        }

        return new World(builder, doc, instances, rows, inputs, tableCode);
    }

    /// <summary>
    /// Публікує версію-скрипт: текст через домен, вихід і прив'язка напряму, ТАБЛИЦІ ЧИТАННЯ — ті, що дає справжній
    /// компілятор (<c>CompiledScript.ReadTables</c>), як і матеріалізація публікації.
    /// </summary>
    private static async Task<(int MethodologyId, int VersionId)> PublishAsync(
        TestDocumentBuilder builder, TestDocument doc, int unitId, string key, string text, string output, int columnId)
    {
        var compiled = ScriptCompiler.Compile(text, new ScriptCompileOptions(NumericMode.Strict, IsLibrary: false));
        Assert.True(compiled.IsSuccess, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));

        await using var db = builder.CreateContext();
        var methodology = new Methodology(EcrCode.Create("SC" + key), Name("script"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.Full);
        version.SetScript(text, ScriptCompiler.Hash(text));
        version.Publish(2, "test", new DateOnly(Year, 1, 1), testsPassed: true, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var outputRow = new MethodologyOutput(version.Id, EcrCode.Create(output), unitId, 1);
        outputRow.SetPerSubstance(false);
        db.MethodologyOutputs.Add(outputRow);
        db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, columnId, methodology.Id, output, "{}"));
        foreach (var code in compiled.Script!.ReadTables)
        {
            db.MethodologyReadTables.Add(new MethodologyReadTable(version.Id, code));
        }

        await db.SaveChangesAsync();
        return (methodology.Id, version.Id);
    }

    private Harness NewHarness() => new(sql.ConnectionString, Now);

    /// <summary>Справжній контейнер; планувальник записує coalesced-проходи замість Quartz.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness(string connectionString, DateTime now)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ecr"] = connectionString })
                .Build();

            var scheduler = Substitute.For<IBackgroundJobScheduler>();
            scheduler.EnqueueCoalescedAsync<IRecalculationJob>(
                    Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
                .Returns(call =>
                {
                    // Coalesced: одна задача на ціль; нова постановка заміняє ту, що чекає.
                    var target = call.ArgAt<string>(0);
                    var request = (RecalculationRequest)call.ArgAt<object?>(1)!;
                    Queue[target] = request;
                    return Task.FromResult("job-" + target);
                });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEcrInfrastructure(configuration);
            services.AddEcrCalculations();
            services.AddEcrApplication();
            services.AddSingleton<ICurrentUser>(Substitute.For<ICurrentUser>());
            services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(now));
            services.AddSingleton(scheduler);
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
        }

        public Dictionary<string, RecalculationRequest> Queue { get; } = [];

        /// <summary>Поставлені проходи (ціль → завдання).</summary>
        public ICollection<RecalculationRequest> Passes => Queue.Values;

        public Task RefreshRegistryAsync()
            => _provider.GetRequiredService<ITemplateLayoutRegistry>().RefreshAsync(CancellationToken.None);

        public async Task RunAsync(World world, int? periodKey)
        {
            await using var scope = _provider.CreateAsyncScope();
            var job = scope.ServiceProvider.GetRequiredService<IRecalculationJob>();
            await job.ExecuteAsync(
                new RecalculationRequest(
                    ProjectId: world.Doc.ProjectId,
                    DocumentId: world.Doc.DocumentId,
                    PeriodKey: periodKey,
                    TriggeredByUserId: 7),
                new NoOpProgress(),
                CancellationToken.None);
        }

        /// <summary>Виконує поставлені проходи справжнім завданням (кожен — у своєму scope), поки черга не спорожніє.</summary>
        public async Task RunPassesAsync(World world)
        {
            for (var round = 0; round < 3 && Queue.Count > 0; round++)
            {
                var batch = Queue.Values.OrderBy(r => r.PeriodKey).ToList();
                Queue.Clear();
                foreach (var request in batch)
                {
                    await using var scope = _provider.CreateAsyncScope();
                    var job = scope.ServiceProvider.GetRequiredService<IRecalculationJob>();
                    await job.ExecuteAsync(request, new NoOpProgress(), CancellationToken.None);
                }
            }
        }

        public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }

    private static string Code(string prefix, int month) => prefix + "_M" + month.ToString("00", CultureInfo.InvariantCulture);

    private static int Period(int month) => (Year * 100) + month;

    private static PeriodKey Key(int month) => new(Period(month));

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
