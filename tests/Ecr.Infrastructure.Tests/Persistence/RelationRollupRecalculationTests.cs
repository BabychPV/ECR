// tests/Ecr.Infrastructure.Tests/Persistence/RelationRollupRecalculationTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D-230: хук <c>IRelationRecalculator</c> у <c>RecalculationService.RunAsync</c> на реальному SQL Server.
/// Джерело й приймач — дві таблиці однієї «версії» (дві згенеровані будівником таблиці під одним знімком).
/// </summary>
/// <remarks>
/// Мутація-доказ: прибрати виклик <c>ComputeForDocumentAsync</c> у <c>RecalculationService</c> — тести запису
/// червоніють; прибрати перевірку <c>HasActiveRollupOrCheck</c> в <c>RelationRecalculator</c> — тест «без
/// Rollup» червоніє (зв'язки читаються).
/// </remarks>
[Collection("SqlServer")]
public sealed class RelationRollupRecalculationTests(SqlServerFixture sql)
{
    private const int StringCol = 0;
    private const int DecimalCol = 1;
    private const int FormulaCol = 4;
    private const int TotalCol = 3;
    private const int ColumnFormulaId = 930_001;
    private const int TargetFormulaId = 930_002;

    private sealed record Pair(TestDocument Source, TestDocument Target);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Rollup_пишеться_в_приймач_після_формул_ідемпотентно_і_слідує_за_джерелом()
    {
        var pair = await ArrangeAsync();

        await using (var db = CreateContext([]))
        {
            var written = await Build(db, pair, flag: true, withRollup: true, withHook: true, withColumnFormula: false)
                .RecalculateAllAsync(pair.Target.DocumentId, pair.Target.PeriodKey, CancellationToken.None);
            Assert.Equal(1, written);
        }

        // Рядок приймача з ключем 'A' отримав суму 5 + 10; решта рядків (без ключа) не торкнулась.
        Assert.Equal(15m, await NumericAsync(pair.Target, pair.Target.RowIds[0], FormulaCol));
        Assert.Null(await NumericAsync(pair.Target, pair.Target.RowIds[1], FormulaCol));
        Assert.True(await IsCalculatedAsync(pair.Target, pair.Target.RowIds[0], FormulaCol));

        // Ідемпотентність: той самий прогін нічого не змінює.
        await using (var db = CreateContext([]))
        {
            var again = await Build(db, pair, flag: true, withRollup: true, withHook: true, withColumnFormula: false)
                .RecalculateAllAsync(pair.Target.DocumentId, pair.Target.PeriodKey, CancellationToken.None);
            Assert.Equal(0, again);
        }

        // Джерело змінилось — приймач слідує.
        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 7 WHERE TableRowId = {pair.Source.RowIds[0]} " +
            $"AND ColumnDefId = {pair.Source.ColumnDefIds[DecimalCol]}");

        await using (var db = CreateContext([]))
        {
            var changed = await Build(db, pair, flag: true, withRollup: true, withHook: true, withColumnFormula: false)
                .RecalculateAllAsync(pair.Target.DocumentId, pair.Target.PeriodKey, CancellationToken.None);
            Assert.Equal(1, changed);
        }

        Assert.Equal(17m, await NumericAsync(pair.Target, pair.Target.RowIds[0], FormulaCol));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_без_активного_Rollup_не_читає_зв_язки_і_не_додає_запитів()
    {
        var pair = await ArrangeAsync();

        // База: формула колонки приймача, хука немає. З хуком, але без прапора, — ті самі звернення до бази.
        var baseline = new List<string>();
        int baselineWritten;
        await using (var db = CreateContext(baseline))
        {
            baselineWritten = await Build(db, pair, flag: false, withRollup: true, withHook: false, withColumnFormula: true)
                .RecalculateAllAsync(pair.Target.DocumentId, pair.Target.PeriodKey, CancellationToken.None);
        }

        Assert.True(baselineWritten > 0, "Базовий прогін мусить щось писати, інакше порівняння порожнє.");
        await ExecuteAsync(
            $"DELETE FROM doc.CellValue WHERE ColumnDefId = {pair.Target.ColumnDefIds[FormulaCol]}");

        var withHook = new List<string>();
        var versionsSpy = Substitute.For<ITemplateVersionStore>();
        await using (var db = CreateContext(withHook))
        {
            var written = await Build(
                    db, pair, flag: false, withRollup: true, withHook: true, withColumnFormula: true, versionsSpy)
                .RecalculateAllAsync(pair.Target.DocumentId, pair.Target.PeriodKey, CancellationToken.None);
            Assert.Equal(baselineWritten, written);
        }

        Assert.Equal(baseline.Count, withHook.Count);
        await versionsSpy.DidNotReceive().ListTableRelationsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Формула_над_ціллю_Rollup_перераховується_у_повному_прогоні_після_запису_цілі()
    {
        var pair = await ArrangeAsync();

        await using (var db = CreateContext([]))
        {
            await Build(db, pair, flag: true, withRollup: true, withHook: true, withColumnFormula: false, withTargetFormula: true)
                .RecalculateAllAsync(pair.Target.DocumentId, pair.Target.PeriodKey, CancellationToken.None);
        }

        // Ціль стала 5 + 10; CTOT = CFRM * 2 мусить прочитати НОВУ ціль, а не порожню/стару.
        Assert.Equal(15m, await NumericAsync(pair.Target, pair.Target.RowIds[0], FormulaCol));
        Assert.Equal(30m, await NumericAsync(pair.Target, pair.Target.RowIds[0], TotalCol));

        // Джерело змінилось — і ціль, і формула над нею слідують за ним.
        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 7 WHERE TableRowId = {pair.Source.RowIds[0]} " +
            $"AND ColumnDefId = {pair.Source.ColumnDefIds[DecimalCol]}");

        await using (var db = CreateContext([]))
        {
            await Build(db, pair, flag: true, withRollup: true, withHook: true, withColumnFormula: false, withTargetFormula: true)
                .RecalculateAllAsync(pair.Target.DocumentId, pair.Target.PeriodKey, CancellationToken.None);
        }

        Assert.Equal(17m, await NumericAsync(pair.Target, pair.Target.RowIds[0], FormulaCol));
        Assert.Equal(34m, await NumericAsync(pair.Target, pair.Target.RowIds[0], TotalCol));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Формула_над_ціллю_Rollup_перераховується_після_правки_джерела_інкрементним_прогоном()
    {
        var pair = await ArrangeAsync();

        // Правка комірки джерела (як PatchCells): значення вже в базі, брудне насіння — ця комірка.
        await ExecuteAsync(
            $"UPDATE doc.CellValue SET ValueNumeric = 7 WHERE TableRowId = {pair.Source.RowIds[0]} " +
            $"AND ColumnDefId = {pair.Source.ColumnDefIds[DecimalCol]}");
        var dirty = new DirtySet();
        dirty.Add(new CellAddress(pair.Source.PeriodKey, pair.Source.RowIds[0], pair.Source.ColumnDefIds[DecimalCol]));

        await using (var db = CreateContext([]))
        {
            await Build(db, pair, flag: true, withRollup: true, withHook: true, withColumnFormula: false, withTargetFormula: true)
                .RecalculateAsync(pair.Source.TableInstanceId, dirty, CancellationToken.None);
        }

        Assert.Equal(17m, await NumericAsync(pair.Target, pair.Target.RowIds[0], FormulaCol));
        Assert.Equal(34m, await NumericAsync(pair.Target, pair.Target.RowIds[0], TotalCol));
    }
    private async Task<Pair> ArrangeAsync()
    {
        var source = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 5, rowCount: 3, ct: CancellationToken.None);
        var target = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 5, rowCount: 3, ct: CancellationToken.None);

        Assert.Equal(source.PeriodKey, target.PeriodKey);

        var key = source.PeriodKey.Value;
        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, IsCalculated, IsEmpty) VALUES " +
            $"({key}, {source.RowIds[0]}, {source.ColumnDefIds[StringCol]}, {source.TableDefId}, N'A', 0, 0), " +
            $"({key}, {source.RowIds[1]}, {source.ColumnDefIds[StringCol]}, {source.TableDefId}, N'A', 0, 0), " +
            $"({key}, {target.RowIds[0]}, {target.ColumnDefIds[StringCol]}, {target.TableDefId}, N'A', 0, 0);");
        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({key}, {source.RowIds[0]}, {source.ColumnDefIds[DecimalCol]}, {source.TableDefId}, 5, 0, 0), " +
            $"({key}, {source.RowIds[1]}, {source.ColumnDefIds[DecimalCol]}, {source.TableDefId}, 10, 0, 0), " +
            $"({key}, {target.RowIds[0]}, {target.ColumnDefIds[DecimalCol]}, {target.TableDefId}, 4, 0, 0);");

        return new Pair(source, target);
    }

    private static RecalculationService Build(
        EcrDbContext db, Pair pair, bool flag, bool withRollup, bool withHook, bool withColumnFormula,
        ITemplateVersionStore? versionsOverride = null, bool withTargetFormula = false)
    {
        var target = pair.Target;
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(target.DocumentId, target.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(target.DocumentId, target.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var versions = versionsOverride ?? Substitute.For<ITemplateVersionStore>();
        // Формула над ціллю Rollup (CTOT = CFRM * 2) має в графі ребро на колонку цілі, як після справжньої публікації.
        IReadOnlyList<FormulaDependency> edges = withTargetFormula
            ? [FormulaDependency.ForFormula(TargetFormulaId, 0, target.TableDefId, null, target.ColumnDefIds[FormulaCol], null, null, 0)]
            : [];
        versions.ListFormulaDependenciesAsync(target.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(edges);

        if (withRollup)
        {
            var relation = new TableRelationDef(
                EcrCode.Create("ROLL1"), pair.Source.TableDefId, target.TableDefId, TableRelationKind.Rollup,
                """{"keys":[{"source":"CSTR","target":"CSTR"}]}""");
            relation.Update(
                pair.Source.TableDefId, target.TableDefId, TableRelationKind.Rollup, relation.MatchJson,
                """{"sourceColumn":"CDEC","targetColumn":"CFRM","aggregate":"sum"}""", 0, true);
            versions.ListTableRelationsAsync(target.TemplateVersionId, Arg.Any<CancellationToken>())
                    .Returns([relation]);
        }

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        // Екземпляри обох документів — «один документ»: джерело видається за екземпляр документа-приймача.
        var real = new RowStore(db, bulk, clock);
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var period = call.ArgAt<PeriodKey>(1);
                var both = new List<TableInstanceRef>();
                foreach (var doc in new[] { pair.Source, pair.Target })
                {
                    both.AddRange((await real.GetTableInstancesAsync(doc.DocumentId, period, CancellationToken.None))
                        .Select(i => i with { DocumentId = target.DocumentId, TemplateVersionId = target.TemplateVersionId }));
                }

                return (IReadOnlyList<TableInstanceRef>)both;
            });
        rows.ResolveTableInstanceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(async call => (await real.ResolveTableInstanceAsync(call.ArgAt<long>(0), CancellationToken.None))
                with { DocumentId = target.DocumentId, TemplateVersionId = target.TemplateVersionId });
        rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call => real.GetRowIdsBatchAsync(
                call.ArgAt<IReadOnlyList<long>>(0), call.ArgAt<PeriodKey>(1), CancellationToken.None));

        var cells = new NormalizedCellStore(db);
        IRelationRecalculator? hook = withHook ? new RelationRecalculator(versions, cells) : null;

        return new RecalculationService(
            cells, rows, periods, Metadata(pair, flag, withColumnFormula, withTargetFormula), versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db), hook);
    }

    private static IMetadataCache Metadata(Pair pair, bool flag, bool withColumnFormula, bool withTargetFormula = false)
    {
        var target = pair.Target;
        var sheet = new SheetDef(target.TemplateVersionId, EcrCode.Create(target.SheetCode), Name("Sheet"), 1);
        SetId(sheet, target.SheetDefId);

        var columns = new Dictionary<int, ColumnDef>();
        foreach (var doc in new[] { pair.Source, pair.Target })
        {
            var table = new TableDef(target.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Name("Table"), 1,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            SetId(table, doc.TableDefId);

            foreach (var (index, code, type) in new[]
                     {
                         (StringCol, "CSTR", CellDataType.String), (DecimalCol, "CDEC", CellDataType.Decimal),
                         (TotalCol, "CTOT", CellDataType.Decimal), (FormulaCol, "CFRM", CellDataType.Formula),
                     })
            {
                var def = new ColumnDef(doc.TableDefId, EcrCode.Create(code), Name(code), index + 1, type);
                SetId(def, doc.ColumnDefIds[index]);
                table.AddColumn(def);
                columns[def.Id] = def;
            }

            if (withColumnFormula && doc == pair.Target)
            {
                var formula = new FormulaDef(table.Id, FormulaScope.Column, "[CDEC] * 2", ExpressionDialect.Template);
                SetId(formula, ColumnFormulaId);
                formula.AssignColumn(doc.ColumnDefIds[FormulaCol]);
                formula.SetEvaluationOrder(1);
                table.AddFormula(formula);
            }

            if (withTargetFormula && doc == pair.Target)
            {
                var overTarget = new FormulaDef(table.Id, FormulaScope.Column, "[CFRM] * 2", ExpressionDialect.Template);
                SetId(overTarget, TargetFormulaId);
                overTarget.AssignColumn(doc.ColumnDefIds[TotalCol]);
                overTarget.SetEvaluationOrder(2);
                table.AddFormula(overTarget);
            }

            sheet.AddTable(table);
        }

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: target.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columns, RowsByKey: new Dictionary<(int, string), RowDef>())
        {
            HasActiveRollupOrCheck = flag,
        };

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(target.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<decimal?> NumericAsync(TestDocument doc, long rowId, int column)
    {
        var result = await ScalarAsync(
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {rowId} AND ColumnDefId = {doc.ColumnDefIds[column]}");
        return result is null or DBNull ? null : (decimal)result;
    }

    private async Task<bool> IsCalculatedAsync(TestDocument doc, long rowId, int column)
        => (bool?)await ScalarAsync(
            $"SELECT IsCalculated FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {rowId} AND ColumnDefId = {doc.ColumnDefIds[column]}") == true;

    private async Task<object?> ScalarAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        return await command.ExecuteScalarAsync();
    }

    private EcrDbContext CreateContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
