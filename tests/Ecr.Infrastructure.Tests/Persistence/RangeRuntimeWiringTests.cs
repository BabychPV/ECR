// tests/Ecr.Infrastructure.Tests/Persistence/RangeRuntimeWiringTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ФВ-0.2 / D-17 ЧЕРЕЗ <see cref="RecalculationService"/> на реальному SQL Server: діапазон <c>[A:B]</c>
/// формули опублікованої версії читає ключі з <c>cfg.FormulaDependency</c>, а не поточний
/// <c>RowDef.Ordinal</c>, і ця проводка йде з самого сервісу перерахунку.
/// </summary>
/// <remarks>
/// ⛔ Юніт-тести <c>RangeRuntimeReorderTests</c> створюють <c>SliceEvaluationContext</c> руками з
/// <c>formulaDependencies</c> і <c>UseFormula</c>; сервіс, що забув передати граф або викликати
/// <c>UseFormula</c>, ними не ловиться: усі вони лишилися б зеленими, а рантайм мовчки рахував би за
/// поточним порядком (60 → 1039).
/// <para>
/// Мутація (перевірено): у <c>RecalculationService.RunAsync</c> прибрати
/// <c>formulaDependencies: dependencies</c> — перший тест червоний (1039 замість 60).
/// ⚠ Прибраний <c>context.UseFormula(...)</c> окремо не перевірявся: за побудовою контекст без
/// формули не знає, чиї ребра шукати, і діапазон іде запасним шляхом.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RangeRuntimeWiringTests(SqlServerFixture sql)
{
    private const int FormulaId = 930_001;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-0.2")]
    public async Task Перерахунок_читає_діапазон_за_ключами_публікації_а_не_за_поточним_порядком()
    {
        var doc = await ArrangeAsync();

        await using var db = CreateContext();
        await Build(db, doc, withPublishedGraph: true)
            .RecalculateAllAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);

        // Опубліковано: R1:R3 = 10 + 20 + 30. Презентаційна правка поставила R4 (999) на другу позицію,
        // R2 — на четверту: за поточним порядком діапазон R1:R3 охопив би R1, R4, R3.
        Assert.Equal(60m, await NumericAsync(doc, doc.RowIds[4], Column.Decimal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-0.2")]
    public async Task Без_збереженого_графа_чернетка_діапазон_за_поточним_порядком()
    {
        var doc = await ArrangeAsync();

        await using var db = CreateContext();
        await Build(db, doc, withPublishedGraph: false)
            .RecalculateAllAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);

        // Контроль: ті самі дані й порядок, лише без графа — число інше (10 + 999 + 30), тож
        // перший тест справді розрізняє «ключі публікації» і «поточний порядок».
        Assert.Equal(1039m, await NumericAsync(doc, doc.RowIds[4], Column.Decimal));
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
            .BuildAsync(columnCount: 5, rowCount: 5, ct: CancellationToken.None);

        var key = doc.PeriodKey.Value;
        var dec = doc.ColumnDefIds[(int)Column.Decimal - 1];
        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({key}, {doc.RowIds[0]}, {dec}, {doc.TableDefId}, 10, 0, 0), " +
            $"({key}, {doc.RowIds[1]}, {dec}, {doc.TableDefId}, 20, 0, 0), " +
            $"({key}, {doc.RowIds[2]}, {dec}, {doc.TableDefId}, 30, 0, 0), " +
            $"({key}, {doc.RowIds[3]}, {dec}, {doc.TableDefId}, 999, 0, 0);");

        return doc;
    }

    private static RecalculationService Build(EcrDbContext db, TestDocument doc, bool withPublishedGraph)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        // Граф ОПУБЛІКОВАНОЇ версії: діапазон R1:R3, розкритий при публікації в порядку Ordinal 1, 2, 3.
        // Той самий вигляд, що дає `PublishChecks.Dependencies`: ребро Cell на ключ рядка й колонку CDEC,
        // `SortOrder` — позиція в розкритому списку.
        IReadOnlyList<FormulaDependency> graph = withPublishedGraph
            ?
            [
                .. Enumerable.Range(1, 3).Select(i => FormulaDependency.ForFormula(
                    FormulaId, DependencyExtractor.KindCell, doc.TableDefId, RowKeyOf(doc, i),
                    doc.ColumnDefIds[(int)Column.Decimal - 1], filterJson: null, periodOffset: null, sortOrder: i - 1)),
            ]
            : [];

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(graph);

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new RecalculationService(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), periods,
            Metadata(doc), versions,
            new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
            new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db));
    }

    private static string RowKeyOf(TestDocument doc, int i) => $"R{i}_{doc.SheetCode["SHEET".Length..]}";

    private static IMetadataCache Metadata(TestDocument doc)
    {
        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create(doc.SheetCode), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);
        var tableCode = $"TB{doc.TableDefId}";
        var table = new TableDef(doc.SheetDefId, EcrCode.Create(tableCode), Name("Table"), 1,
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

        // ⚠ Поточний порядок — ПІСЛЯ презентаційної правки: R1(1), R4(2), R3(3), R2(4), R5(5).
        // Публікація розкрила R1:R3 як R1, R2, R3.
        var ordinals = new Dictionary<int, int> { [1] = 1, [4] = 2, [3] = 3, [2] = 4, [5] = 5 };
        var rows = new Dictionary<(int, string), RowDef>();
        for (var i = 1; i <= 5; i++)
        {
            var row = new RowDef(doc.TableDefId, RowKey.Create(RowKeyOf(doc, i)), ordinals[i], Name($"Row {i}"), RowKind.Item);
            SetId(row, doc.RowDefIds[i - 1]);
            table.AddRow(row);
            rows[(doc.TableDefId, row.RowKeyValue)] = row;
        }

        var rowFormula = new FormulaDef(
            table.Id, FormulaScope.Row,
            $"SUM([{tableCode}].[{RowKeyOf(doc, 1)}:{RowKeyOf(doc, 3)}].[CDEC])", ExpressionDialect.Template);
        SetId(rowFormula, FormulaId);
        rowFormula.AssignRow(doc.RowDefIds[4]);
        rowFormula.SetEvaluationOrder(1);
        table.AddFormula(rowFormula);

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
