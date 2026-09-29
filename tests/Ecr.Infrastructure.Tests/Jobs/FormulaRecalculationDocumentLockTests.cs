// tests/Ecr.Infrastructure.Tests/Jobs/FormulaRecalculationDocumentLockTests.cs
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Інкрементний перерахунок формул після PATCH (<c>FormulaRecalculationJob</c>)
/// проти повного перерахунку того самого документа (<c>RecalculationJob</c>).
/// </summary>
/// <remarks>
/// ⛔ Дефект, який доводить тест. Задачі в різних лейнах (<c>Default</c> і
/// <c>Recalc</c>) і з різними цілями, тож черга пускає їх одночасно. Повний
/// перерахунок читає входи, рахує і лише потім пише (<c>RecalculationService.RunAsync</c>:
/// обчислення поза транзакцією запису). Якщо між читанням і записом людина
/// змінила вхід (PATCH зафіксовано) і інкрементна задача вже записала свіже
/// похідне число, повний перерахунок перезаписує його СТАРІШИМ — і тихо: вхід
/// 30, похідне 42 замість 60, жодної ознаки на екрані.
/// <para>
/// ⚠ Детерміновано: повний перерахунок тримається точкою перемикання між
/// обчисленням і записом (<c>IPeriodStore.FindPeriodStateAsync</c>, як у
/// <c>SubmitRecalculationRaceTests</c>); інкрементна задача або завершується
/// (одночасне виконання — дефект), або стає в очікування лока документа, яке
/// видно в <c>sys.dm_tran_locks</c>.
/// </para>
/// <para>
/// Мутація: прибрати взяття лока з <c>FormulaRecalculationJob.ExecuteAsync</c> —
/// тест червоніє: жива обчислена комірка 42 при вході 30.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class FormulaRecalculationDocumentLockTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>Вхід на момент старту повного перерахунку.</summary>
    private const decimal InputBefore = 21m;

    /// <summary>Вхід після PATCH, зафіксованого, поки повний перерахунок рахував.</summary>
    private const decimal InputAfter = 30m;

    /// <summary>Обчислене значення від давнього перерахунку.</summary>
    private const decimal StaleOutput = 20m;

    /// <summary>Ідентифікатор формули в підставному знімку.</summary>
    private const int FormulaId = 920_001;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Інкрементний_перерахунок_не_виконується_поки_повний_тримає_документ()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 1, rowMode: TableRowMode.Dynamic);
        var inColumn = doc.ColumnDefIds[1];

        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({doc.PeriodKey.Value}, {doc.RowIds[0]}, {inColumn}, {doc.TableDefId}, {Num(InputBefore)}, 0, 0), " +
            $"({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, {Num(StaleOutput)}, 1, 0);");

        await using var fullDb = builder.CreateContext();
        await using var incrementalDb = builder.CreateContext();

        var fullComputed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFull = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var clock = new TestClock(Now);
        var fullJob = new RecalculationJob(
            fullDb,
            new NoOpRunner(),
            RunHandler(fullDb, clock),
            Formulas(fullDb, doc, async () =>
            {
                fullComputed.TrySetResult();
                await releaseFull.Task.WaitAsync(Patience).ConfigureAwait(false);
            }),
            clock);

        // Кнопка «Перерахувати» документа за період: повний прогін формул.
        var fullTask = Task.Run(() => fullJob.ExecuteAsync(
            new RecalculationRequest(doc.ProjectId, doc.DocumentId, doc.PeriodKey.Value, TriggeredByUserId: 7),
            NoOpProgress.Instance,
            CancellationToken.None));

        await fullComputed.Task.WaitAsync(Patience);

        // Повний перерахунок порахував OUT = 42 з IN = 21 і ще не записав. Людина
        // зберігає IN = 30 (PATCH зафіксовано), його каскадна задача стартує.
        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = {Num(InputAfter)} " +
            $"WHERE PeriodKey = {doc.PeriodKey.Value} AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {inColumn}");

        var incrementalJob = IncrementalJob(incrementalDb, doc);
        var incrementalTask = Task.Run(() => incrementalJob.ExecuteAsync(
            new FormulaRecalculationRequest(doc.TableInstanceId, doc.PeriodKey.Value, [new DirtyCell(doc.RowIds[0], inColumn)]),
            NoOpProgress.Instance,
            CancellationToken.None));

        using var stopWatch = new CancellationTokenSource();
        var waiting = WaitUntilSomeoneWaitsOnDocumentLockAsync(doc.DocumentId, stopWatch.Token);

        var first = await Task.WhenAny(incrementalTask, waiting).WaitAsync(Patience);
        var ranConcurrently = first == incrementalTask;
        await stopWatch.CancelAsync();

        if (ranConcurrently)
        {
            // Виняток інкрементної — одразу, а не «щось зависло».
            await incrementalTask;
        }

        releaseFull.TrySetResult();
        await fullTask.WaitAsync(Patience);
        await incrementalTask.WaitAsync(Patience);

        var output = await LiveOutputAsync(doc);

        // ⛔ Наслідок: похідне число мусить відповідати ОСТАННЬОМУ входу.
        Assert.True(
            output == InputAfter * 2,
            $"Вхід {InputAfter}, обчислене {output}: повний перерахунок записав число, пораховане зі старого входу, поверх свіжого.");

        // ⛔ Причина: інкрементна задача не мала права писати, поки повна тримає документ.
        Assert.False(ranConcurrently, "Інкрементний перерахунок виконувався одночасно з повним перерахунком документа.");
    }

    /// <summary>
    /// Задача через DI-активатор: тест не залежить від форми конструктора й
    /// компілюється і до, і після фіксу.
    /// </summary>
    private static FormulaRecalculationJob IncrementalJob(EcrDbContext db, TestDocument doc)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(Formulas(db, doc, beforeWrite: null));

        var provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<FormulaRecalculationJob>(provider);
    }

    /// <summary>Чекає, доки якась сесія стане в чергу за локом документа.</summary>
    private async Task WaitUntilSomeoneWaitsOnDocumentLockAsync(long documentId, CancellationToken ct)
    {
        var resource = FormattableString.Invariant($"ecr:recalc:doc:{documentId}");

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);

        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = 'APPLICATION'
                  AND request_status = 'WAIT'
                  AND resource_description LIKE '%' + @resource + '%'
                """;
            command.Parameters.AddWithValue("@resource", resource);

            if ((int)(await command.ExecuteScalarAsync(ct))! > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    /// <summary>Перерахунок формул на реальних сховищах; <paramref name="beforeWrite"/> — точка перемикання.</summary>
    private static RecalculationService Formulas(EcrDbContext db, TestDocument doc, Func<Task>? beforeWrite)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        // ⚠ `RunAsync` кличе це ПІСЛЯ обчислення і ДО транзакції запису.
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(async _ =>
               {
                   if (beforeWrite is not null)
                   {
                       await beforeWrite().ConfigureAwait(false);
                   }

                   return (PeriodState?)PeriodState.Open;
               });

        // Ребро графа: OUT залежить від колонки IN усіх рядків — без нього
        // інкрементний прогін від брудної комірки IN не знайшов би формули.
        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
        [
            FormulaDependency.ForFormula(
                FormulaId, dependsOnKind: 0, doc.TableDefId, rowKey: null, doc.ColumnDefIds[1],
                filterJson: null, periodOffset: null, sortOrder: 0),
        ]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), periods, Metadata(doc), versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    /// <summary>Знімок структури з реальними Id і формулою <c>OUT = [IN] * 2</c>.</summary>
    private static IMetadataCache Metadata(TestDocument doc)
    {
        var text = ColumnDefFor(doc, 1, "TXT", CellDataType.String);
        var input = ColumnDefFor(doc, 2, "IN", CellDataType.Decimal);
        var output = ColumnDefFor(doc, 3, "OUT", CellDataType.Decimal);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(text);
        table.AddColumn(input);
        table.AddColumn(output);

        var formula = new FormulaDef(table.Id, FormulaScope.Column, "[IN] * 2", ExpressionDialect.Template);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(formula, FormulaId);
        formula.AssignColumn(output.Id);
        table.AddFormula(formula);

        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>
            {
                [text.Id] = text,
                [input.Id] = input,
                [output.Id] = output,
            },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int ordinal, string code, CellDataType type)
    {
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, doc.ColumnDefIds[ordinal - 1]);
        return column;
    }

    private static RunCalculationHandler RunHandler(EcrDbContext db, TestClock clock)
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

    private async Task<decimal?> LiveOutputAsync(TestDocument doc)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[2]}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Методологій у тесті немає — предмет тесту формули шаблону.</summary>
    private sealed class NoOpRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
