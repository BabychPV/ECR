// tests/Ecr.Infrastructure.Tests/Jobs/FormulaRecalculationDocumentPassSqlTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
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
/// L2 (recalc-latency), наскрізно на справжньому SQL: ОДНА задача формул на документ обходить усі зачеплені
/// періоди за зростанням під ОДНИМ локом; результат збігається з послідовною обробкою по періодах.
/// </summary>
/// <remarks>
/// ⛔ Мутації: (1) у циклі проходу брати не найменший, а найбільший період — червоний «за зростанням»;
/// (2) у <c>FormulaRecalculationJob</c> не зливати читачів у перелік (<c>next.AddSource</c> пропустити) — червоні
/// «еквівалентність» і «жоден період не втрачено»; (3) <c>Hops</c> читача не збільшувати (<c>item.Hops + 1</c> → 0) —
/// «цикл» виходить на <c>MaxPasses</c>, а не на стелю ланцюжка; (4) брати лок у кожному періоді — червоний «лок раз».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class FormulaRecalculationDocumentPassSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private const int ShiftFormulaId = 920_411;
    private const int SumFormulaId = 920_412;
    private const decimal StaleOutput = -1m;
    private static readonly int[] Months = [3, 4, 5, 6, 7];

    /// <summary>Накопичувальна сума: вихід періоду = вихід попереднього + власний вхід.</summary>
    private const string Chain = "SUM([Period:-1].[C])";

    /// <summary>Двосторонній цикл між сусідніми періодами: розбіжний, якщо його не обрізати.</summary>
    private const string Cycle = "SUM([Period:-1].[C]) + SUM([Period:+1].[C])";

    // ── еквівалентність ──────────────────────────────────────────────────────

    /// <summary>
    /// Диференційний доказ: один прохід по насінню з різних періодів дає ті самі значення, що (а) окремі задачі по
    /// періоду в довільному порядку («як було») і (б) повний перерахунок року за зростанням — еталон фіксованої точки.
    /// </summary>
    [Fact]
    public async Task Один_прохід_дає_ті_самі_значення_що_окремі_задачі_по_періодах_і_повний_перерахунок()
    {
        var single = await ArrangeAsync(Chain);
        var perPeriod = await ArrangeAsync(Chain);
        var full = await ArrangeAsync(Chain);
        foreach (var world in new[] { single, perPeriod, full })
        {
            await SetInputAsync(world, 3, 100m);
            await SetInputAsync(world, 5, 500m);
        }

        // Нове: одна задача, насіння трьох періодів у «неправильному» порядку.
        var audit = new RecordingAuditWriter();
        var progress = await RunJobAsync(
            single,
            new FormulaRecalculationRequest(
                single.Instance(7).InstanceId, Period(7),
                [Seed(single, 7), Seed(single, 3), Seed(single, 5)], single.Doc.DocumentId),
            audit);

        // Як було: по задачі на період, у порядку НАДХОДЖЕННЯ (спадному), кожна окремо.
        foreach (var month in new[] { 5, 3 })
        {
            await RunJobAsync(
                perPeriod,
                new FormulaRecalculationRequest(
                    perPeriod.Instance(month).InstanceId, Period(month), [Seed(perPeriod, month)],
                    perPeriod.Doc.DocumentId),
                new RecordingAuditWriter());
        }

        // Еталон: повний перерахунок за зростанням (кожен період читає вже готовий попередній).
        await using (var db = full.Builder.CreateContext())
        {
            foreach (var month in Months)
            {
                await Formulas(db, full, new RecordingAuditWriter())
                    .RecalculateAllAsync(full.Doc.DocumentId, new PeriodKey(Period(month)), CancellationToken.None);
            }
        }

        foreach (var month in Months)
        {
            var expected = await OutputAsync(full, month);
            Assert.NotEqual(StaleOutput, expected);
            Assert.Equal(expected, await OutputAsync(single, month));
            Assert.Equal(expected, await OutputAsync(perPeriod, month));
        }

        // Лічильники: кожен період написано рівно раз, за зростанням; підсумок = кількість змінених комірок.
        Assert.Equal([Period(3), Period(4), Period(5), Period(6), Period(7)], audit.PeriodsInOrder);
        // B і C кожного з п'яти періодів — по одній зміні, ніяких повторних записів.
        Assert.Equal(Months.Length * 2, progress.Written);
        Assert.Equal([Period(3), Period(4), Period(5), Period(6), Period(7)], progress.Periods);
    }

    [Fact]
    public async Task Читачі_дораховуються_тим_самим_проходом_кожен_раз_і_жоден_період_не_втрачено()
    {
        // Без вхідного значення в 7-му: правка лише періоду 6 і 4 (насіння) — 5 і 7 читачі через ланцюжок.
        var world = await ArrangeAsync(Chain);
        await SetInputAsync(world, 4, 45m);
        await SetInputAsync(world, 6, 65m);

        var audit = new RecordingAuditWriter();
        var progress = await RunJobAsync(
            world,
            new FormulaRecalculationRequest(
                world.Instance(6).InstanceId, Period(6), [Seed(world, 6), Seed(world, 4)], world.Doc.DocumentId),
            audit);

        // 4, 5 (читач), 6, 7 (читач) — кожен рівно раз, за зростанням; 3 не зачеплено (читає ніхто з правлених).
        Assert.Equal([Period(4), Period(5), Period(6), Period(7)], audit.PeriodsInOrder);
        Assert.Equal(30m, await OutputAsync(world, 3)); // не зачеплено: лишилось базовим
        Assert.Equal(Period(4), progress.Periods[0]);
        Assert.Equal(Period(7), progress.Periods[^1]);
    }

    // ── лок ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task П_ять_періодів_одне_взяття_лока_документа()
    {
        var world = await ArrangeAsync(Chain);
        await SetInputAsync(world, 3, 100m);

        var acquisitions = new List<long>();
        RecalculationDocumentLock.AcquireObserver.Value = acquisitions.Add;
        try
        {
            var progress = await RunJobAsync(
                world,
                new FormulaRecalculationRequest(
                    world.Instance(3).InstanceId, Period(3), [Seed(world, 3)], world.Doc.DocumentId),
                new RecordingAuditWriter());

            Assert.Equal(Months.Length, progress.Periods.Count);
        }
        finally
        {
            RecalculationDocumentLock.AcquireObserver.Value = null;
        }

        Assert.Equal([world.Doc.DocumentId], acquisitions);
    }

    // ── завершення ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Циклічні_міжперіодні_посилання_обриваються_стелею_ланцюжка_а_не_безкінечністю()
    {
        var world = await ArrangeAsync(Cycle);

        // Старт: «у періоді 4 змінилась колонка OUT» — період-читач 5 (старий формат задачі читача).
        var audit = new RecordingAuditWriter();
        var progress = await RunJobAsync(
            world,
            new FormulaRecalculationRequest(
                0, Period(5),
                [new DirtyCell(0, world.Doc.ColumnDefIds[3], world.Doc.TableDefId, 4, 0)],
                world.Doc.DocumentId),
            audit);

        // Задача завершилась (інакше тест завис би), робила не один прохід, і зупинила її саме стеля ланцюжка:
        // до `MaxPasses` не дійшло.
        Assert.True(audit.Calls > 2, $"Прохід має піти по ланцюжку читачів, а було {audit.Calls}.");
        Assert.True(
            audit.Calls < FormulaRecalculationJob.MaxPasses,
            $"Обрив мав статися стелею ланцюжка (hops), а не страховкою MaxPasses: {audit.Calls} проходів.");
        Assert.True(progress.Written > 0);
    }

    // ── світ ─────────────────────────────────────────────────────────────────

    private static int Period(int month) => 202600 + month;

    private static DirtyCell Seed(World world, int month)
        => new(
            world.Instance(month).RowId, world.Doc.ColumnDefIds[1],
            PeriodKey: Period(month), TableInstanceId: world.Instance(month).InstanceId);

    private async Task<World> ArrangeAsync(string expression, bool baseline = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202603, columnCount: 4, rowCount: 1, rowMode: TableRowMode.Dynamic);
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instances = new Dictionary<int, (long InstanceId, long RowId)>
        {
            [3] = (doc.TableInstanceId, doc.RowIds[0]),
        };

        await using (var setup = builder.CreateContext())
        {
            var rowKey = (await setup.TableRows.SingleAsync(
                r => r.Id == doc.RowIds[0] && r.PeriodKeyValue == 202603)).RowKeyValue;

            var march = await setup.Periods.SingleAsync(
                p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == 202603);
            march.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

            foreach (var month in Months.Where(m => m != 3))
            {
                var key = new PeriodKey(Period(month));
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
                instances[month] = (instanceId, rowId);
            }

            await setup.SaveChangesAsync();
        }

        // Вхід IN = місяць × 10; B і C (обчислювані) — застарілі (-1) у кожному періоді.
        foreach (var (month, (_, rowId)) in instances)
        {
            await ExecuteAsync(
                "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
                $"({Period(month)}, {rowId}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, {month * 10}, 0, 0), " +
                $"({Period(month)}, {rowId}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, {StaleOutput.ToString(CultureInfo.InvariantCulture)}, 1, 0), " +
                $"({Period(month)}, {rowId}, {doc.ColumnDefIds[3]}, {doc.TableDefId}, {StaleOutput.ToString(CultureInfo.InvariantCulture)}, 1, 0);");
        }

        var world = new World(builder, doc, instances, expression);
        if (baseline)
        {
            // Узгоджений початковий стан: повний перерахунок року за зростанням (інакше застарілі B/C різних
            // періодів змішалися б із результатом перевірюваного шляху).
            await using var db = builder.CreateContext();
            foreach (var month in Months)
            {
                await Formulas(db, world, new RecordingAuditWriter())
                    .RecalculateAllAsync(doc.DocumentId, new PeriodKey(Period(month)), CancellationToken.None);
            }
        }

        return world;
    }

    private Task SetInputAsync(World world, int month, decimal value)
        => ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = {value.ToString(CultureInfo.InvariantCulture)} " +
            $"WHERE PeriodKey = {Period(month)} AND TableRowId = {world.Instance(month).RowId} " +
            $"AND ColumnDefId = {world.Doc.ColumnDefIds[1]}");

    private async Task<CapturingJobProgress> RunJobAsync(
        World world, FormulaRecalculationRequest request, RecordingAuditWriter audit)
    {
        await using var db = world.Builder.CreateContext();

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        var periods = Periods(world);
        var fanOut = new CrossPeriodFanOut(
            new RowStore(db, bulk, new TestClock(Now)), Versions(world), periods, jobs, db);

        var job = new FormulaRecalculationJob(Formulas(db, world, audit), db, fanOut, periods);
        var progress = new CapturingJobProgress();
        await job.ExecuteAsync(request, progress, CancellationToken.None);

        // Жодної нової задачі: читачі йдуть у той самий прохід.
        Assert.Empty(jobs.ReceivedCalls());

        return progress;
    }

    private static IPeriodStore Periods(World world)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(world.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(PeriodState.Open);

        return periods;
    }

    private static ITemplateVersionStore Versions(World world)
    {
        var versions = Substitute.For<ITemplateVersionStore>();
        var table = world.Doc.TableDefId;
        var inColumn = world.Doc.ColumnDefIds[1];
        var shiftColumn = world.Doc.ColumnDefIds[2];
        var sumColumn = world.Doc.ColumnDefIds[3];

        // F1 (колонка B) читає C сусіднього періоду; F2 (колонка C) = B + IN у своєму періоді. Власна колонка формули
        // не читається (це цикл у межах періоду), тому ланцюжок йде через пару B -> C.
        List<FormulaDependency> dependencies =
        [
            FormulaDependency.ForFormula(
                ShiftFormulaId, dependsOnKind: 3, table, rowKey: null, sumColumn, filterJson: null, periodOffset: -1, sortOrder: 0),
            FormulaDependency.ForFormula(
                SumFormulaId, dependsOnKind: 0, table, rowKey: null, shiftColumn, filterJson: null, periodOffset: null, sortOrder: 1),
            FormulaDependency.ForFormula(
                SumFormulaId, dependsOnKind: 0, table, rowKey: null, inColumn, filterJson: null, periodOffset: null, sortOrder: 2),
        ];
        if (world.Expression == Cycle)
        {
            dependencies.Add(FormulaDependency.ForFormula(
                ShiftFormulaId, dependsOnKind: 3, table, rowKey: null, sumColumn, filterJson: null, periodOffset: 1, sortOrder: 3));
        }

        versions.ListFormulaDependenciesAsync(world.Doc.TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(dependencies);

        return versions;
    }

    private RecalculationService Formulas(EcrDbContext db, World world, RecordingAuditWriter audit)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), Periods(world), Metadata(world),
            Versions(world), new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            audit.Bind(new AuditWriter(db)), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    private static IMetadataCache Metadata(World world)
    {
        var doc = world.Doc;
        var text = ColumnDefFor(doc, 1, "TXT", CellDataType.String);
        var input = ColumnDefFor(doc, 2, "IN", CellDataType.Decimal);
        var shift = ColumnDefFor(doc, 3, "B", CellDataType.Decimal);
        var sum = ColumnDefFor(doc, 4, "C", CellDataType.Decimal);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(text);
        table.AddColumn(input);
        table.AddColumn(shift);
        table.AddColumn(sum);

        var shiftFormula = new FormulaDef(table.Id, FormulaScope.Column, world.Expression, ExpressionDialect.Template);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(shiftFormula, ShiftFormulaId);
        shiftFormula.AssignColumn(shift.Id);
        table.AddFormula(shiftFormula);
        var sumFormula = new FormulaDef(table.Id, FormulaScope.Column, "[B] + [IN]", ExpressionDialect.Template);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sumFormula, SumFormulaId);
        sumFormula.AssignColumn(sum.Id);
        table.AddFormula(sumFormula);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>
            {
                [text.Id] = text,
                [input.Id] = input,
                [shift.Id] = shift,
                [sum.Id] = sum,
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

    private async Task<decimal?> OutputAsync(World world, int month)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT cv.ValueNumeric FROM doc.CellValue cv JOIN doc.TableRow r " +
            "ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId " +
            $"WHERE cv.PeriodKey = {Period(month)} AND cv.ColumnDefId = {world.Doc.ColumnDefIds[3]} " +
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

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Doc,
        IReadOnlyDictionary<int, (long InstanceId, long RowId)> Instances,
        string Expression)
    {
        public (long InstanceId, long RowId) Instance(int month) => Instances[month];
    }

    /// <summary>Пише в справжній аудит і запам'ятовує, у які періоди (за порядком викликів) ішов запис.</summary>
    private sealed class RecordingAuditWriter : IAuditWriter
    {
        private IAuditWriter? _inner;

        public List<int> PeriodsInOrder { get; } = [];

        public int Calls { get; private set; }

        public RecordingAuditWriter Bind(IAuditWriter inner)
        {
            _inner = inner;

            return this;
        }

        public async Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct)
        {
            Calls++;
            // Прохід по періоду може писати двома викликами (інкремент і «з інших періодів») — це один прохід.
            foreach (var period in changes.Select(c => c.Address.PeriodKey.Value).Distinct())
            {
                if (PeriodsInOrder.Count == 0 || PeriodsInOrder[^1] != period)
                {
                    PeriodsInOrder.Add(period);
                }
            }

            await _inner!.WriteCellChangesAsync(changes, ct).ConfigureAwait(false);
        }

        public Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct)
            => _inner!.WriteStructureChangeAsync(change, ct);

        public Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
            => _inner!.WriteSecurityEventAsync(evt, ct);

        public Task WriteIndependentSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
            => _inner!.WriteIndependentSecurityEventAsync(evt, ct);

        public Task WriteSecurityEventsAsync(IReadOnlyList<SecurityEventRecord> events, CancellationToken ct)
            => _inner!.WriteSecurityEventsAsync(events, ct);

        public Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct)
            => _inner!.WritePublicationEventAsync(evt, ct);
    }
}
