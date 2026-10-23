// tests/Ecr.Infrastructure.Tests/Jobs/CrossPeriodRecalculationSqlTests.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// WW-03, наскрізно на справжньому SQL: 7.0 періоду m читає колонку місяця k з періоду k того ж документа
/// через <c>[Period:#k]</c>; правка періоду k перераховує період-читач; закритий період і поданий аркуш
/// (знімок) не перераховуються.
/// </summary>
/// <remarks>
/// ⛔ Що було: документ-місяць ізольований — 7.0 періоду m бачила лише свій екземпляр Water Report (інші місяці
/// порожні), а правка періоду k не перераховувала читачів (G2). Формула тут — <c>[Period:#3].[IN]</c> у
/// колонці OUT: періоди 3 (власний), 5 і 7 читають IN періоду 3.
/// </remarks>
[Collection("SqlServer")]
public sealed class CrossPeriodRecalculationSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
    private const int FormulaId = 920_301;
    private const int May = 202605;
    private const int July = 202607;
    private const decimal StaleOutput = -1m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WW-03")]
    public async Task Період_5_читає_колонку_періоду_3_а_правка_періоду_3_перераховує_5_і_7()
    {
        var world = await ArrangeAsync();

        // 1. Повний перерахунок періоду 5: OUT = IN періоду 3 (30), а НЕ IN власного (50).
        await using (var db = world.Builder.CreateContext())
        {
            await Formulas(db, world).RecalculateAllAsync(
                world.Doc.DocumentId, new PeriodKey(May), CancellationToken.None);
        }

        Assert.Equal(30m, await OutputAsync(world, May));

        // 2. Правка IN у періоді 3 (PATCH → задача каскаду). Читачі 5 і 7 ставляться coalesced.
        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 33 WHERE PeriodKey = 202603 AND TableRowId = {world.Doc.RowIds[0]} " +
            $"AND ColumnDefId = {world.Doc.ColumnDefIds[1]}");

        var enqueued = new List<(string Target, string Payload)>();
        await RunJobAsync(
            world, enqueued,
            new FormulaRecalculationRequest(
                world.Doc.TableInstanceId, 202603, [new DirtyCell(world.Doc.RowIds[0], world.Doc.ColumnDefIds[1])]));

        // Власний період (3): абсолютне посилання на себе йде через звичайний план.
        Assert.Equal(33m, await OutputAsync(world, 202603));

        // Рівно по одній coalesced-задачі на період-читач — не по задачі на комірку.
        Assert.Equal(
            [
                FormulaRecalculationTarget.Of(world.Doc.DocumentId, May, createdByUserId: null),
                FormulaRecalculationTarget.Of(world.Doc.DocumentId, July, createdByUserId: null),
            ],
            enqueued.Select(e => e.Target).Order(StringComparer.Ordinal).ToList());

        // 3. Виконавець бере задачі періодів-читачів — ті самі, що поставив fan-out.
        foreach (var queued in enqueued)
        {
            var request = JsonSerializer.Deserialize<FormulaRecalculationRequest>(
                queued.Payload, WebOptions)!;
            await RunJobAsync(world, [], request);
        }

        Assert.Equal(33m, await OutputAsync(world, May));
        Assert.Equal(33m, await OutputAsync(world, July));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WW-03")]
    public async Task Закритий_період_читач_не_ставиться_і_не_перераховується()
    {
        var world = await ArrangeAsync();
        await using (var setup = world.Builder.CreateContext())
        {
            var july = await setup.Periods.SingleAsync(
                p => p.ProjectId == world.Doc.ProjectId && p.PeriodKeyValue == July);
            july.AdvanceTo(PeriodState.Closed, DateTime.UtcNow);
            await setup.SaveChangesAsync();
        }

        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 33 WHERE PeriodKey = 202603 AND TableRowId = {world.Doc.RowIds[0]} " +
            $"AND ColumnDefId = {world.Doc.ColumnDefIds[1]}");

        var enqueued = new List<(string Target, string Payload)>();
        await RunJobAsync(
            world, enqueued,
            new FormulaRecalculationRequest(
                world.Doc.TableInstanceId, 202603, [new DirtyCell(world.Doc.RowIds[0], world.Doc.ColumnDefIds[1])]));

        // «Закриті періоди автоматично не перераховуються ніколи»: у чергу лише 5.
        var only = Assert.Single(enqueued);
        Assert.Equal(FormulaRecalculationTarget.Of(world.Doc.DocumentId, May, createdByUserId: null), only.Target);
        Assert.Equal(StaleOutput, await OutputAsync(world, July));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "WW-03")]
    public async Task Поданий_аркуш_періоду_читача_лишається_знімком()
    {
        var world = await ArrangeAsync();
        await using (var setup = world.Builder.CreateContext())
        {
            var state = new ApprovalState(world.Doc.DocumentId, world.Doc.SheetDefId, May);
            state.Submit(userId: 5, DateTime.UtcNow);
            setup.ApprovalStates.Add(state);
            await setup.SaveChangesAsync();
        }

        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 33 WHERE PeriodKey = 202603 AND TableRowId = {world.Doc.RowIds[0]} " +
            $"AND ColumnDefId = {world.Doc.ColumnDefIds[1]}");

        var enqueued = new List<(string Target, string Payload)>();
        await RunJobAsync(
            world, enqueued,
            new FormulaRecalculationRequest(
                world.Doc.TableInstanceId, 202603, [new DirtyCell(world.Doc.RowIds[0], world.Doc.ColumnDefIds[1])]));

        foreach (var queued in enqueued)
        {
            var request = JsonSerializer.Deserialize<FormulaRecalculationRequest>(
                queued.Payload, WebOptions)!;
            await RunJobAsync(world, [], request);
        }

        // ФВ-9.17: подана цифра змінюється лише Reopen. Липень (не поданий) оновився, травень — ні.
        Assert.Equal(StaleOutput, await OutputAsync(world, May));
        Assert.Equal(33m, await OutputAsync(world, July));
    }

    /// <summary>
    /// ✎ D-442 (борг AN-129): правка погоджувача поточного кроку на ПОДАНОМУ аркуші — каскад перераховує формули
    /// саме цього аркуша одразу, а не лише на затвердженні. Справжній <c>SheetEditGate</c> читає <c>Submitted</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: у <c>RecalculationService.EnterSheetsAsync</c> прибрати гілку <c>approverEditSheets</c> — OUT
    /// лишається −1, тест червоний. Насіння — з JSON (<c>approverSheetDefId</c>), тієї самої форми, яку ставить
    /// <c>PatchCellsHandler</c>: розійдеться ім'я поля — позначка загубиться, і тест так само червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-442")]
    public async Task D442_правка_погоджувача_перераховує_формули_поданого_аркуша_до_затвердження()
    {
        var world = await ArrangeAsync();
        await SetSheetStateAsync(world, approved: false);

        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 33 WHERE PeriodKey = 202603 AND TableRowId = {world.Doc.RowIds[0]} " +
            $"AND ColumnDefId = {world.Doc.ColumnDefIds[1]}");

        var request = JsonSerializer.Deserialize<FormulaRecalculationRequest>(
            $$"""
            {"tableInstanceId":{{world.Doc.TableInstanceId}},"periodKey":202603,
             "cells":[{"rowId":{{world.Doc.RowIds[0]}},"columnDefId":{{world.Doc.ColumnDefIds[1]}},"approverSheetDefId":{{world.Doc.SheetDefId}}}]}
            """,
            WebOptions)!;

        await RunJobAsync(world, [], request);

        Assert.Equal(33m, await OutputAsync(world, 202603));
    }

    /// <summary>
    /// D-442: поданий аркуш, який ніхто не правив (насіння без позначки погоджувача), каскад і далі пропускає;
    /// затверджений не пишеться навіть із позначкою (затвердження вже перерахувало його під своїм блокуванням).
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-442")]
    public async Task D442_поданий_без_правки_погоджувача_і_затверджений_каскад_пропускає(bool approved, bool marked)
    {
        var world = await ArrangeAsync();
        await SetSheetStateAsync(world, approved);

        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 33 WHERE PeriodKey = 202603 AND TableRowId = {world.Doc.RowIds[0]} " +
            $"AND ColumnDefId = {world.Doc.ColumnDefIds[1]}");

        await RunJobAsync(
            world, [],
            new FormulaRecalculationRequest(
                world.Doc.TableInstanceId, 202603,
                [new DirtyCell(
                    world.Doc.RowIds[0], world.Doc.ColumnDefIds[1],
                    ApproverSheetDefId: marked ? world.Doc.SheetDefId : null)]));

        Assert.Equal(StaleOutput, await OutputAsync(world, 202603));
    }

    /// <summary>Аркуш документа за березень — поданий (або затверджений).</summary>
    private static async Task SetSheetStateAsync(World world, bool approved)
    {
        await using var setup = world.Builder.CreateContext();
        var state = new ApprovalState(world.Doc.DocumentId, world.Doc.SheetDefId, 202603);
        state.Submit(userId: 5, DateTime.UtcNow);
        if (approved)
        {
            state.Approve(userId: 5, DateTime.UtcNow);
        }

        setup.ApprovalStates.Add(state);
        await setup.SaveChangesAsync();
    }

    private async Task RunJobAsync(World world, List<(string Target, string Payload)> enqueued, FormulaRecalculationRequest request)
    {
        await using var db = world.Builder.CreateContext();

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                enqueued.Add((call.ArgAt<string>(0), JsonSerializer.Serialize(call.ArgAt<object?>(1))));
                return "job";
            });

        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        var periods = Periods(world);
        var versions = Versions(world);
        var fanOut = new CrossPeriodFanOut(
            new RowStore(db, bulk, new TestClock(Now)), versions, periods, jobs, db);

        var job = new FormulaRecalculationJob(Formulas(db, world), db, fanOut, periods);
        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);
    }

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202603, columnCount: 3, rowCount: 1, rowMode: TableRowMode.Dynamic);
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);

        await using (var setup = builder.CreateContext())
        {
            var rowKey = (await setup.TableRows.SingleAsync(
                r => r.Id == doc.RowIds[0] && r.PeriodKeyValue == 202603)).RowKeyValue;

            var march = await setup.Periods.SingleAsync(
                p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == 202603);
            march.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

            foreach (var period in new[] { May, July })
            {
                var key = new PeriodKey(period);
                var entity = new Period(
                    doc.ProjectId, key, (byte)key.Sequence,
                    new DateOnly(key.Year, key.Sequence, 1), new DateOnly(key.Year, key.Sequence, 28));
                entity.AdvanceTo(PeriodState.Open, DateTime.UtcNow);
                setup.Periods.Add(entity);

                var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
                var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
                setup.TableInstances.Add(new TableInstance(key, instanceId, doc.DocumentId, doc.TableDefId, Now));
                setup.TableRows.Add(new TableRow(key, rowId, instanceId, RowKey.Create(rowKey), 1, Now));
                await setup.SaveChangesAsync();

                // Вхід IN = період × 10 / 1000 · 10; число для кожного періоду своє: 30, 50, 70.
                var input = (period % 100) * 10;
                await ExecuteAsync(
                    "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
                    $"({period}, {rowId}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, {input}, 0, 0), " +
                    $"({period}, {rowId}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, {StaleOutput.ToString(CultureInfo.InvariantCulture)}, 1, 0);");
            }

            await setup.SaveChangesAsync();
        }

        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"(202603, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 30, 0, 0), " +
            $"(202603, {doc.RowIds[0]}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, {StaleOutput.ToString(CultureInfo.InvariantCulture)}, 1, 0);");

        return new World(builder, doc);
    }

    private static IPeriodStore Periods(World world)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

        // Стан береться з бази: закритий липень має бути видно й задачі, і fan-out.
        periods.FindPeriodStateAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await using var db = world.Builder.CreateContext();
                var key = call.ArgAt<int>(1);
                var state = await db.Periods
                    .Where(p => p.ProjectId == world.Doc.ProjectId && p.PeriodKeyValue == key)
                    .Select(p => (PeriodState?)p.State)
                    .FirstOrDefaultAsync();

                return state;
            });

        return periods;
    }

    private static ITemplateVersionStore Versions(World world)
    {
        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(world.Doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(
        [
            // [Period:#3].[IN]: вид 5 (абсолютний період), k = 3 у PeriodOffset.
            FormulaDependency.ForFormula(
                FormulaId, dependsOnKind: 5, world.Doc.TableDefId, rowKey: null, world.Doc.ColumnDefIds[1],
                filterJson: null, periodOffset: 3, sortOrder: 0),
        ]);

        return versions;
    }

    private static RecalculationService Formulas(EcrDbContext db, World world)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), Periods(world), Metadata(world.Doc),
            Versions(world), new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

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

        var formula = new FormulaDef(
            table.Id, FormulaScope.Column, "[Period:#3].[IN]", ExpressionDialect.Template);
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

    private async Task<decimal?> OutputAsync(World world, int period)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT cv.ValueNumeric FROM doc.CellValue cv JOIN doc.TableRow r " +
            $"ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId " +
            $"WHERE cv.PeriodKey = {period} AND cv.ColumnDefId = {world.Doc.ColumnDefIds[2]} " +
            $"AND r.TableInstanceId IN (SELECT Id FROM doc.TableInstance WHERE DocumentId = {world.Doc.DocumentId})";
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

    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc);

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
