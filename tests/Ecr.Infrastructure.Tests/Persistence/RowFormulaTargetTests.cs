// tests/Ecr.Infrastructure.Tests/Persistence/RowFormulaTargetTests.cs
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
/// Куди пише формула РЯДКА — на <b>реальному</b> SQL Server (V-04, V-03).
/// </summary>
/// <remarks>
/// ⛔ V-04 (UX-прохід 2026-09-24, третій раунд): формула рядка
/// (<c>RTOT = [R1].[CDEC] + [R2].[CDEC]</c>) писала число в УСІ колонки
/// рядка — String, Date, Bool, Lookup, Unit; <c>doc.CellValue.ValueNumeric = 15</c>
/// лягало в колонку дати. Формула рядка — це «підсумковий рядок» (02b §3.1:
/// <c>[Jan]</c> — колонка того самого рядка), і її результат має сенс лише в
/// колонці, тип якої його приймає.
///
/// ⛔ V-03: рядкова й колонкова формули, що цілять в ОДНУ комірку
/// (<c>RTOT·CFRM</c>), давали два записи з однією адресою в одному пакеті, і
/// <c>NormalizedCellStore.ApplyAsync</c> падав на
/// <c>PRIMARY KEY … dbo.@cells</c> — подання 500, фонова задача ретраїла
/// хвилинами. Публікація таку конфігурацію тепер відхиляє, але вже
/// опубліковані версії лишаються — рушій мусить не падати й вирішувати
/// детерміновано: пише остання в порядку обчислення (саме її значення вже
/// бачать залежні формули в контексті).
/// </remarks>
[Collection("SqlServer")]
public sealed class RowFormulaTargetTests(SqlServerFixture sql)
{
    private const int RowFormulaId = 920_001;
    private const int ColumnFormulaId = 920_002;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Формула_рядка_пише_лише_в_колонки_що_приймають_число()
    {
        var doc = await ArrangeAsync();

        await using var db = CreateContext();
        var written = await Build(db, doc, withColumnFormula: false)
            .RecalculateAllAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);

        var total = doc.RowIds[2];
        Assert.Equal(15m, await NumericAsync(doc, total, Column.Decimal));
        Assert.Equal(15m, await NumericAsync(doc, total, Column.Formula));

        // ⛔ Головне: у String/Date/Bool колонках підсумкового рядка нічого
        // немає. До виправлення тут лежало ValueNumeric = 15.
        foreach (var column in new[] { Column.String, Column.Date, Column.Bool })
        {
            Assert.False(
                await ExistsAsync(doc, total, column),
                $"Формула рядка записала число в колонку {column}: сумісні лише числові.");
        }

        Assert.Equal(2, written);
    }

    /// <summary>Порядкові номери колонок у будівнику (1-based).</summary>
    private enum Column
    {
        String = 1,
        Decimal = 2,
        Date = 3,
        Bool = 4,
        Formula = 5,
    }

    private async Task<TestDocument> ArrangeAsync()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 5, rowCount: 3, ct: CancellationToken.None);

        var key = doc.PeriodKey.Value;
        var dec = doc.ColumnDefIds[(int)Column.Decimal - 1];
        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({key}, {doc.RowIds[0]}, {dec}, {doc.TableDefId}, 5, 0, 0), " +
            $"({key}, {doc.RowIds[1]}, {dec}, {doc.TableDefId}, 10, 0, 0);");

        return doc;
    }

    private static RecalculationService Build(EcrDbContext db, TestDocument doc, bool withColumnFormula)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns([]);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), periods,
            Metadata(doc, withColumnFormula), versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    private static IMetadataCache Metadata(TestDocument doc, bool withColumnFormula)
    {
        // Ключі рядків — ті, що будівник уже записав у doc.TableRow: R{i}_{tag}.
        var tag = doc.SheetCode["SHEET".Length..];
        string RowKeyOf(int i) => $"R{i}_{tag}";

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create(doc.SheetCode), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, doc.TableDefId);

        var columns = new Dictionary<int, ColumnDef>();
        foreach (var (column, type) in new[]
                 {
                     (Column.String, CellDataType.String), (Column.Decimal, CellDataType.Decimal),
                     (Column.Date, CellDataType.Date), (Column.Bool, CellDataType.Bool),
                     (Column.Formula, CellDataType.Decimal),
                 })
        {
            var code = column switch
            {
                Column.String => "CSTR", Column.Decimal => "CDEC", Column.Date => "CDAT",
                Column.Bool => "CBOL", _ => "CFRM",
            };
            var def = new ColumnDef(doc.TableDefId, EcrCode.Create(code), Name(code), (int)column, type);
            SetId(def, doc.ColumnDefIds[(int)column - 1]);
            table.AddColumn(def);
            columns[def.Id] = def;
        }

        var rows = new Dictionary<(int, string), RowDef>();
        for (var i = 1; i <= 3; i++)
        {
            var row = new RowDef(doc.TableDefId, RowKey.Create(RowKeyOf(i)), i, Name($"Row {i}"), RowKind.Item);
            SetId(row, doc.RowDefIds[i - 1]);
            table.AddRow(row);
            rows[(doc.TableDefId, row.RowKeyValue)] = row;
        }

        var rowFormula = new FormulaDef(
            table.Id, FormulaScope.Row, $"[{RowKeyOf(1)}].[CDEC] + [{RowKeyOf(2)}].[CDEC]", ExpressionDialect.Template);
        SetId(rowFormula, RowFormulaId);
        rowFormula.AssignRow(doc.RowDefIds[2]);
        rowFormula.SetEvaluationOrder(1);
        table.AddFormula(rowFormula);

        if (withColumnFormula)
        {
            var columnFormula = new FormulaDef(table.Id, FormulaScope.Column, "[CDEC] * 2", ExpressionDialect.Template);
            SetId(columnFormula, ColumnFormulaId);
            columnFormula.AssignColumn(doc.ColumnDefIds[(int)Column.Formula - 1]);
            columnFormula.SetEvaluationOrder(2);
            table.AddFormula(columnFormula);
        }

        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columns, RowsByKey: rows);

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<decimal?> NumericAsync(TestDocument doc, long rowId, Column column)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {rowId} AND ColumnDefId = {doc.ColumnDefIds[(int)column - 1]}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    private async Task<bool> ExistsAsync(TestDocument doc, long rowId, Column column)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {rowId} AND ColumnDefId = {doc.ColumnDefIds[(int)column - 1]}";
        return (int)(await command.ExecuteScalarAsync())! > 0;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

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
