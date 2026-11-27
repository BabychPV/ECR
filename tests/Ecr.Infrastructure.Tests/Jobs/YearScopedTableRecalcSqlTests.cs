// tests/Ecr.Infrastructure.Tests/Jobs/YearScopedTableRecalcSqlTests.cs
using System.Globalization;
using Ecr.Application.Calculations;
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
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// b14 document-move B5 + N1: документ HSE401 (таблиця «екземпляр на період» версії «рік + місячні колонки») у
/// МІСЯЧНОМУ проєкті LANDUI2026 перераховується в закритому січні-якорі, поки рік відкритий (B24
/// <c>AnnualOnlyAnchorsAsync</c>), а з усім закритим роком — відмова. Формула з довжиною року з <c>[Period].Year</c>
/// (HSE401 v14) дає те саме, що в річному проєкті (365), а <c>[Period].Days</c> (v13) — 31 (N1, ×11.8).
/// </summary>
/// <remarks>
/// МУТАЦІЇ (локально, 26.11): (H-2) у RecalculationJob.AnnualOnlyAnchorsAsync не брати YearEditWindowFacts — червоний «Вікно_року_в_Grace…»; база 4980a1837 (роутер не вважає таблицю «річною») — червоний «Закритий_якір…»: річна
/// ціль якоря не рахується, TOT лишається −1.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "b14-document-move-B5")]
public sealed class YearScopedTableRecalcSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private const int Jan = 202601;
    private const int Nov = 202611;
    private const int TotalFormulaId = 940_100;
    private const int DaysFormulaId = 940_101;
    private const int YearFormulaId = 940_102;
    private const decimal Input = 730m;

    [Fact]
    public async Task Закритий_якір_річної_таблиці_рахується_поки_рік_відкритий_і_довжина_року_як_у_річному_проєкті()
    {
        var w = await ArrangeAsync(novOpen: true);
        try
        {
            await SetCellAsync(w, w.Input, Input, calculated: false);
            await SetCellAsync(w, w.Total, -1m, calculated: true);

            await RunJobAsync(w, Jan);

            Assert.Equal(2 * Input, await PhysicalAsync(w, w.Total));
            Assert.Equal(decimal.Round(Input / 31m, 10), await PhysicalAsync(w, w.Days));     // v13: [Period].Days січня місячного проєкту
            Assert.Equal(decimal.Round(Input / 365m, 10), await PhysicalAsync(w, w.YearLen));  // v14: як у HSE401_2026 (365)
        }
        finally
        {
            await CleanupAsync(w);
        }
    }

    [Fact]
    public async Task Увесь_рік_закритий_перерахунок_якоря_відхиляється()
    {
        var w = await ArrangeAsync(novOpen: false);
        try
        {
            await SetCellAsync(w, w.Input, Input, calculated: false);
            await SetCellAsync(w, w.Total, -1m, calculated: true);

            await Assert.ThrowsAnyAsync<Exception>(() => RunJobAsync(w, Jan));
            Assert.Equal(-1m, await PhysicalAsync(w, w.Total));
        }
        finally
        {
            await CleanupAsync(w);
        }
    }

    [Fact]
    [Trait("Finding", "b14-document-move-H2")]
    public async Task Вікно_року_в_Grace_перераховує_закритий_якір_коли_закриті_всі_місяці()
    {
        // Огляд H-2: правку HSE401 у Grace вікна року прийнято (AccessDecisionService) — перерахунок того самого якоря не
        // відхиляється, хоч агрегат місяців (усі Closed) його б відхилив. Вікно після року + зміщення — відмова.
        var w = await ArrangeAsync(novOpen: false);
        try
        {
            await ExecuteAsync($"UPDATE doc.Project SET Status = {(int)ProjectStatus.Active} WHERE Id = {w.Doc.ProjectId}; UPDATE cfg.Template SET YearEditWindow = 1 WHERE Id = {w.Doc.TemplateId};");
            await SetCellAsync(w, w.Input, Input, calculated: false);
            await SetCellAsync(w, w.Total, -1m, calculated: true);

            await RunJobAsync(w, Jan, new DateTime(2027, 1, 10, 12, 0, 0, DateTimeKind.Utc));
            Assert.Equal(2 * Input, await PhysicalAsync(w, w.Total));

            await SetCellAsync(w, w.Total, -1m, calculated: true);
            await Assert.ThrowsAnyAsync<Exception>(() => RunJobAsync(w, Jan, new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc)));
            Assert.Equal(-1m, await PhysicalAsync(w, w.Total));
        }
        finally
        {
            await ExecuteAsync($"UPDATE doc.Project SET Status = {(int)ProjectStatus.Draft} WHERE Id = {w.Doc.ProjectId}; UPDATE cfg.Template SET YearEditWindow = 0 WHERE Id = {w.Doc.TemplateId};");
            await CleanupAsync(w);
        }
    }

    // ─────────────────────────────── допоміжне ───────────────────────────────
    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc, int Input, int Total, int Days, int YearLen, IMetadataCache Metadata);

    private async Task<World> ArrangeAsync(bool novOpen)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(Jan, columnCount: 2, rowCount: 1, layoutKind: TableLayoutKind.PerPeriodInstance);
        await builder.AddPeriodAsync(doc, new PeriodKey(Nov));

        int total, days, yearLen;
        await using (var setup = builder.CreateContext())
        {
            var columns = new[]
            {
                new ColumnDef(doc.TableDefId, EcrCode.Create($"TOT_{doc.TableDefId}"), Text("TOT"), 10, CellDataType.Formula),
                new ColumnDef(doc.TableDefId, EcrCode.Create($"DAYS_{doc.TableDefId}"), Text("DAYS"), 11, CellDataType.Formula),
                new ColumnDef(doc.TableDefId, EcrCode.Create($"YLEN_{doc.TableDefId}"), Text("YLEN"), 12, CellDataType.Formula),
            };
            setup.ColumnDefs.AddRange(columns);
            await setup.SaveChangesAsync();
            (total, days, yearLen) = (columns[0].Id, columns[1].Id, columns[2].Id);

            // LANDUI2026: січень (якір) закритий; листопад — відкритий або теж закритий (рік закрито).
            foreach (var period in await setup.Periods.Where(p => p.ProjectId == doc.ProjectId).ToListAsync())
            {
                period.AdvanceTo(PeriodState.Open, Now);
                if (period.PeriodKeyValue == Jan || !novOpen)
                {
                    period.AdvanceTo(PeriodState.Grace, Now);
                    period.AdvanceTo(PeriodState.Closed, Now);
                }
            }

            await setup.SaveChangesAsync();
        }

        await ExecuteAsync(
            $"UPDATE cfg.TemplateVersion SET Layout = {(int)TemplateLayout.YearWithMonthColumns} WHERE Id = {doc.TemplateVersionId};");

        var world = new World(builder, doc, doc.ColumnDefIds[1], total, days, yearLen, null!);
        return world with { Metadata = Metadata(world) };
    }

    private async Task RunJobAsync(World w, int periodKey, DateTime? jobNow = null)
    {
        await using var db = w.Builder.CreateContext();
        var registry = Substitute.For<ITemplateLayoutRegistry>();
        registry.HasAny.Returns(true);
        registry.IsYearVersion(w.Doc.TemplateVersionId).Returns(true);
        registry.IsYearSheet(w.Doc.SheetDefId).Returns(true);
        var router = new MonthColumnRouter(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(), registry);
        var store = new MonthRoutedCellStore(new NormalizedCellStore(db), router, w.Metadata, db);

        var job = new RecalculationJob(db, new NoBindingsRunner(), RunHandler(), Formulas(db, w, store, router), new TestClock(jobNow ?? Now));
        await job.ExecuteAsync(
            new RecalculationRequest(w.Doc.ProjectId, w.Doc.DocumentId, periodKey, TriggeredByUserId: null),
            new CapturingJobProgress(),
            CancellationToken.None);
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<Ecr.Application.Common.ICurrentUser>(),
            new TestClock(Now));

    private sealed class NoBindingsRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }

    private static RecalculationService Formulas(EcrDbContext db, World w, ICellStore store, IMonthColumnRouter router)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        // Межі періоду — січень МІСЯЧНОГО проєкту (LANDUI2026): [Period].Days = 31, [Period].Year = 2026.
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(w.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        periods.FindPeriodStateAsync(w.Doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<PeriodState?>(PeriodState.Open));

        var versions = Substitute.For<ITemplateVersionStore>();
        versions.ListFormulaDependenciesAsync(w.Doc.TemplateVersionId, Arg.Any<CancellationToken>())
            .Returns(new List<FormulaDependency>
            {
                FormulaDependency.ForFormula(TotalFormulaId, 0, w.Doc.TableDefId, null, w.Input, null, null, 0),
                FormulaDependency.ForFormula(DaysFormulaId, 0, w.Doc.TableDefId, null, w.Input, null, null, 1),
                FormulaDependency.ForFormula(YearFormulaId, 0, w.Doc.TableDefId, null, w.Input, null, null, 2),
            });

        return new RecalculationService(
            store, new RowStore(db, bulk, clock), periods, w.Metadata, versions, new RealFormulaEngine(), units,
            Substitute.For<IRegistryStore>(), headers, new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db),
            monthRouter: router);
    }

    private static IMetadataCache Metadata(World w)
    {
        var doc = w.Doc;
        var columns = new List<ColumnDef>
        {
            Column(doc, doc.ColumnDefIds[0], "C1", 1, CellDataType.String),
            Column(doc, w.Input, "IN", 2, CellDataType.Decimal),
            Column(doc, w.Total, "TOT", 10, CellDataType.Formula),
            Column(doc, w.Days, "DAYS", 11, CellDataType.Formula),
            Column(doc, w.YearLen, "YLEN", 12, CellDataType.Formula),
        };

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Text("Sheet"), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Text("T775"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        foreach (var column in columns)
        {
            table.AddColumn(column);
        }

        AddFormula(table, TotalFormulaId, w.Total, "[IN] * 2");
        AddFormula(table, DaysFormulaId, w.Days, "[IN] / [Period].Days");
        AddFormula(table, YearFormulaId, w.YearLen, "[IN] / IF([Period].Year % 4 = 0, 366, 365)");
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: columns.ToDictionary(c => c.Id), RowsByKey: new Dictionary<(int, string), RowDef>());
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static void AddFormula(TableDef table, int id, int columnDefId, string expression)
    {
        var formula = new FormulaDef(table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(formula, id);
        formula.AssignColumn(columnDefId);
        table.AddFormula(formula);
    }

    private static ColumnDef Column(TestDocument doc, int id, string code, int ordinal, CellDataType type)
    {
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create(code), Text(code), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private Task SetCellAsync(World w, int columnDefId, decimal value, bool calculated)
        => ExecuteAsync(
            $"DELETE FROM doc.CellValue WHERE PeriodKey = {Jan} AND TableRowId = {w.Doc.RowIds[0]} AND ColumnDefId = {columnDefId}; " +
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({Jan}, {w.Doc.RowIds[0]}, {columnDefId}, {w.Doc.TableDefId}, {value.ToString(CultureInfo.InvariantCulture)}, {(calculated ? 1 : 0)}, 0);");

    private async Task<decimal?> PhysicalAsync(World w, int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {Jan} AND TableRowId = {w.Doc.RowIds[0]} AND ColumnDefId = {columnDefId}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : decimal.Round((decimal)result, 10);
    }

    /// <summary>Прибирання власного стану (як <c>YearLayoutTailSqlTests</c>): прогони, стани, розкладка версії.</summary>
    private async Task CleanupAsync(World w)
    {
        await ExecuteAsync(
            $"UPDATE calc.CalculationRun SET Status = N'Superseded' WHERE ProjectId = {w.Doc.ProjectId} " +
            $"AND Status <> N'{Ecr.Domain.Entities.Calculations.CalculationRun.SupersededStatus}';");
        await ExecuteAsync($"DELETE FROM calc.FormulaError WHERE DocumentId = {w.Doc.DocumentId};");
        await ExecuteAsync(
            $"UPDATE cfg.TemplateVersion SET Layout = {(int)default(TemplateLayout)} WHERE Id = {w.Doc.TemplateVersionId};");
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
