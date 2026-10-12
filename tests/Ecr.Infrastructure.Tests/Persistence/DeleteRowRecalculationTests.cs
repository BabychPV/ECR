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
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D3-A: каскад перерахунку після м'якого видалення рядка (<c>DeleteRowHandler</c> ставить ту саму
/// <c>IFormulaRecalculationJob</c>, що й PATCH, із насінням «усі колонки видаленого рядка»).
/// </summary>
/// <remarks>
/// ⛔ Доводить на справжньому <c>RecalculationService</c> і SQL Server: після видалення агрегат по
/// колонці рахується БЕЗ видаленого рядка, а сам видалений рядок прогін не читає й не пише — його
/// комірки лишаються як були (їх ніхто не бачить).
/// </remarks>
[Collection("SqlServer")]
public sealed class DeleteRowRecalculationTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);

    private const int FormulaId = 920_101;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Агрегат_після_видалення_рядка_рахується_без_нього_а_видалений_рядок_не_пишеться()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 3, rowCount: 3, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);
        var input = doc.ColumnDefIds[1];
        var output = doc.ColumnDefIds[2];

        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({doc.PeriodKey.Value}, {doc.RowIds[0]}, {input}, {doc.TableDefId}, 10, 0, 0), " +
            $"({doc.PeriodKey.Value}, {doc.RowIds[1]}, {input}, {doc.TableDefId}, 20, 0, 0), " +
            $"({doc.PeriodKey.Value}, {doc.RowIds[2]}, {input}, {doc.TableDefId}, 30, 0, 0);");

        // До видалення: усі три рядки несуть суму 60.
        await using (var db = CreateContext())
        {
            var dirty = new DirtySet();
            dirty.Add(new CellAddress(doc.PeriodKey, doc.RowIds[0], input));
            await Service(db, doc).RecalculateAsync(doc.TableInstanceId, dirty, CancellationToken.None);
        }

        foreach (var rowId in doc.RowIds)
        {
            Assert.Equal(60m, await OutputAsync(doc, rowId, output));
        }

        // Видалення середнього рядка (те, що робить DeleteRowHandler) і насіння каскаду від НЬОГО.
        await using (var db = CreateContext())
        {
            var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
            Assert.True(await rows.SoftDeleteRowAsync(doc.RowIds[1], doc.PeriodKey, CancellationToken.None));
        }

        await using (var db = CreateContext())
        {
            var dirty = new DirtySet();
            dirty.Add(new CellAddress(doc.PeriodKey, doc.RowIds[1], input));
            await Service(db, doc).RecalculateAsync(doc.TableInstanceId, dirty, CancellationToken.None);
        }

        // Живі рядки — без видаленого (10 + 30), а видалений прогін не чіпав.
        Assert.Equal(40m, await OutputAsync(doc, doc.RowIds[0], output));
        Assert.Equal(40m, await OutputAsync(doc, doc.RowIds[2], output));
        Assert.Equal(60m, await OutputAsync(doc, doc.RowIds[1], output));
    }

    private static RecalculationService Service(EcrDbContext db, TestDocument doc)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        // Ребро графа: OUT залежить від колонки IN усіх рядків.
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

    /// <summary>Знімок структури з реальними Id і колонковою формулою <c>OUT = SUM([Table].[WHERE [IN] >= 0].[IN])</c>.</summary>
    private static IMetadataCache Metadata(TestDocument doc)
    {
        var text = Column(doc, 1, "TXT", CellDataType.String);
        var input = Column(doc, 2, "IN", CellDataType.Decimal);
        var output = Column(doc, 3, "OUT", CellDataType.Decimal);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, doc.TableDefId);
        table.AddColumn(text);
        table.AddColumn(input);
        table.AddColumn(output);

        var formula = new FormulaDef(table.Id, FormulaScope.Column, $"SUM([TB{doc.TableDefId}].[WHERE [IN] >= 0].[IN])", ExpressionDialect.Template);
        SetId(formula, FormulaId);
        formula.AssignColumn(output.Id);
        table.AddFormula(formula);

        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [text.Id] = text, [input.Id] = input, [output.Id] = output },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        return metadata;
    }

    private static ColumnDef Column(TestDocument doc, int ordinal, string code, CellDataType type)
    {
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create(code), Name(code), ordinal, type);
        SetId(column, doc.ColumnDefIds[ordinal - 1]);

        return column;
    }

    private static void SetId(object entity, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext CreateContext()
        => new TestDocumentBuilder(sql.ConnectionString).CreateContext();

    private async Task<decimal?> OutputAsync(TestDocument doc, long rowId, int column)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {rowId} AND ColumnDefId = {column}";
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

}
