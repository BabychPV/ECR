// tests/Ecr.Infrastructure.Tests/Persistence/LandHse400CompositionRegistryTests.Document.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// HSE400 (крок 3a): склад газу для Land читається з ДОКУМЕНТА HSE400 замість імпортованих рядків довідника —
/// <see cref="Hse400CompositionSnapshotLoader"/> на реальному SQL Server і тими самими мостами моделі Land.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише ці тести): у <c>Hse400CompositionMapping.Overlay</c> не відкидати рядки довідника за рік
/// документа — червоніє <see cref="Документ_витісняє_довідник_за_свій_рік_цілком"/> (лютий і LPG_HMB знову дають
/// числа імпорту), а з ним і два інші тести цього файлу.
/// </remarks>
public sealed partial class LandHse400CompositionRegistryTests
{
    private const int Year2026 = 202601;
    private static readonly DateTime Stamp = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE400")]
    public async Task Документ_витісняє_довідник_за_свій_рік_цілком()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(
            ("FG_Internal", "Density_20", 1, 0.81m),
            ("FG_Internal", "Vddg", 1, 2.5m),
            ("FG_Internal", "F_FuelLowerHeating_20", 1, 40.5m));

        var snapshot = await LoadFromDocumentAsync(f, template);

        // Числа документа, а не імпорту (там 0.7 і 1.234).
        Assert.Equal(0.81m, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(2.5m, Eval(f, snapshot, ModelExpression("T9_04", "Vddg"), FuelGasInternal, month: 1).AsNumber());

        // Розрахунковий рядок F_<X>: число актуального прогону, колонка місяця — код виходу.
        Assert.Equal(40.5m, Eval(f, snapshot, ModelExpression("T9_08", "FuelLowerHeating"), FuelGasInternal, month: 1).AsNumber());

        // Лютий у документі не заповнено, LPG_HMB у ньому немає: довідник за цей рік уже не діє (там 0.75 і 1.9).
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 2).ErrorCode);
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, ModelDensity20(), LpgHmb, month: 1).ErrorCode);
        Assert.Equal(
            ExpressionErrors.NotAvailable,
            Eval(f, snapshot, ModelExpression("T9_08", "H2Smass"), FuelGasInternal, month: 1).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE400")]
    public async Task Вимкнено_або_рік_без_документа_читається_довідник()
    {
        var f = await ArrangeAsync();
        await AddCompositionAsync(f, 2025, 1, "FG_Internal", "Density20", 0.66m);
        var template = await BuildDocumentAsync(("FG_Internal", "Density_20", 1, 0.81m));

        var off = await LoadFromDocumentAsync(f, template, enabled: false);
        Assert.Equal(0.7m, Eval(f, off, ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(1.9m, Eval(f, off, ModelDensity20(), LpgHmb, month: 1).AsNumber());

        var on = await LoadFromDocumentAsync(f, template);
        Assert.Equal(0.81m, Eval(f, on, ModelDensity20(), FuelGasInternal, month: 1).AsNumber());

        // 2025 документа не має — рядок імпорту лишається.
        Assert.Equal(0.66m, Eval(f, on, ModelDensity20(), FuelGasInternal, month: 1, year: 2025).AsNumber());

        // Шаблону з таким кодом немає — довідник цілий.
        var foreign = await LoadFromDocumentAsync(f, "NO_SUCH_TEMPLATE");
        Assert.Equal(0.7m, Eval(f, foreign, ModelDensity20(), FuelGasInternal, month: 1).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE400")]
    public async Task Ті_самі_числа_в_документі_й_довіднику_дають_той_самий_результат_мостів_Land()
    {
        var f = await ArrangeAsync();

        // Ті самі шість значень, що в довіднику (ArrangeAsync), але іменами рядків документа.
        var template = await BuildDocumentAsync(
            ("FG_Internal", "Density_20", 1, 0.7m),
            ("LPG_HMB", "Density_20", 1, 1.9m),
            ("FG_Internal", "Density_20", 2, 0.75m),
            ("FG_Internal", "Vddg", 1, 1.234m),
            ("FG_Internal", "F_FuelLowerHeating_20", 1, 34.5m),
            ("FG_Internal", "H2S_mass", 1, 0.02m));

        var registry = await LoadAsync(f);
        var document = await LoadFromDocumentAsync(f, template);

        (string Table, string Column)[] bridges =
        [
            ("T9_04", "Density20"), ("T9_04", "Vddg"), ("T9_08", "Density"), ("T9_08", "FuelLowerHeating"),
            ("T9_08", "H2Smass"), ("T9_08", "MercaptanSulphurContent"),
        ];

        foreach (var (table, column) in bridges)
        {
            var expression = ModelExpression(table, column);
            foreach (var fuel in new[] { FuelGasInternal, LpgHmb, "Diesel - Дизель" })
            {
                foreach (var month in new[] { 1, 2, 3 })
                {
                    var expected = Eval(f, registry, expression, fuel, month);
                    var actual = Eval(f, document, expression, fuel, month);
                    Assert.True(
                        expected.ErrorCode == actual.ErrorCode
                        && expected.IsNull == actual.IsNull
                        && (expected.ErrorCode is not null || expected.IsNull || expected.AsNumber() == actual.AsNumber()),
                        $"{table}.{column}, {fuel}, місяць {month}: довідник {expected}, документ {actual}");
                }
            }
        }
    }

    private async Task<IRegistrySnapshot> LoadFromDocumentAsync(Fixture f, string templateCode, bool enabled = true)
    {
        await using var db = sql.CreateContext();
        var loader = new Hse400CompositionSnapshotLoader(
            new RegistrySnapshotLoader(db),
            db,
            new CalculationResultStore(db, new TestClock(Stamp)),
            enabled,
            f.CompositionCode,
            templateCode);
        return await loader.LoadAsync([f.StreamId, f.CompositionId, f.FuelId], EndOfJanuary, null, CancellationToken.None);
    }

    private async Task AddCompositionAsync(Fixture f, int year, int month, string stream, string indicator, decimal value)
    {
        await using var db = sql.CreateContext();
        var fields = await db.RegistryFieldDefs.AsNoTracking()
            .Where(x => x.RegistryDefId == f.CompositionId)
            .ToDictionaryAsync(x => x.Code, x => x.Id);
        var entry = await AddEntryAsync(db, f.CompositionId, $"H{year}{month:D2}_{stream}_{indicator}", ordinal: 100);
        await SetAsync(db, entry, fields["YEAR"], CellDataType.Int, year);
        await SetAsync(db, entry, fields["MONTH"], CellDataType.Int, month);
        await SetAsync(db, entry, fields["STREAM"], CellDataType.String, stream);
        await SetAsync(db, entry, fields["INDICATOR"], CellDataType.String, indicator);
        await SetAsync(db, entry, fields["VALUE"], CellDataType.Decimal, value);
    }

    /// <summary>
    /// Документ за 2026 рік із таблицями-потоками у формі шаблону HSE400: 12 місячних колонок <c>M01…M12</c>, рядок
    /// на показник; значення рядка вводу — у <c>doc.CellValue</c>, розрахункового <c>F_&lt;X&gt;</c> — в актуальному прогоні.
    /// </summary>
    /// <returns>Код шаблону документа.</returns>
    private async Task<string> BuildDocumentAsync(params (string Stream, string RowKey, int Month, decimal Value)[] cells)
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: Year2026, ct: CancellationToken.None);
        var key = new PeriodKey(Year2026);
        await using var db = sql.CreateContext();
        var templateCode = await db.Templates.Where(t => t.Id == doc.TemplateId).Select(t => t.Code).SingleAsync();
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        long? runId = null;
        int? versionId = null;
        var ordinal = 10;

        foreach (var stream in cells.GroupBy(c => c.Stream))
        {
            var table = new TableDef(
                doc.SheetDefId, EcrCode.Create(stream.Key), Text(stream.Key), ++ordinal,
                TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync();

            var columns = new Dictionary<int, ColumnDef>();
            for (var m = 1; m <= 12; m++)
            {
                var column = new ColumnDef(table.Id, EcrCode.Create($"M{m:D2}"), Text($"M{m:D2}"), m, CellDataType.Decimal);
                column.SetMonth(true, (byte)m);
                columns[m] = column;
                db.ColumnDefs.Add(column);
            }

            var rowKeys = stream.Select(c => c.RowKey).Distinct(StringComparer.Ordinal).ToList();
            for (var i = 0; i < rowKeys.Count; i++)
            {
                db.RowDefs.Add(new RowDef(table.Id, RowKey.Create(rowKeys[i]), i + 1, Text(rowKeys[i]), RowKind.Item));
            }

            await db.SaveChangesAsync();

            var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            var firstRowId = await loader.ReserveIdsAsync("doc.TableRowSeq", rowKeys.Count, CancellationToken.None);
            db.TableInstances.Add(new TableInstance(key, instanceId, doc.DocumentId, table.Id, Stamp));
            var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var i = 0; i < rowKeys.Count; i++)
            {
                rowIds[rowKeys[i]] = firstRowId + i;
                db.TableRows.Add(new TableRow(key, firstRowId + i, instanceId, RowKey.Create(rowKeys[i]), i + 1, Stamp));
            }

            await db.SaveChangesAsync();

            foreach (var cell in stream)
            {
                if (!cell.RowKey.StartsWith("F_", StringComparison.Ordinal))
                {
                    db.CellValues.Add(new CellValue(
                        new CellAddress(key, rowIds[cell.RowKey], columns[cell.Month].Id),
                        table.Id,
                        new CellValueData { ValueNumeric = cell.Value }));
                    continue;
                }

                if (runId is null)
                {
                    (runId, versionId) = await AddCurrentRunAsync(doc);
                }

                await AddResultAsync(runId.Value, versionId!.Value, doc, cell.RowKey, $"M{cell.Month:D2}", cell.Value, instanceId);
            }

            await db.SaveChangesAsync();
        }

        return templateCode;
    }

    private async Task<(long RunId, int VersionId)> AddCurrentRunAsync(TestDocument doc)
    {
        await using var db = sql.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var methodology = new Methodology(EcrCode.Create($"HSE400_{tag}"), Text(tag));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Module, createdByUserId: 1, Stamp);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        var run = new CalculationRun(doc.ProjectId, Year2026, triggeredByUserId: 1, Stamp, doc.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        run.Complete("Completed", Stamp, profileJson: null, errorMessage: null);
        run.MakeCurrent();
        await db.SaveChangesAsync();
        return (run.Id, version.Id);
    }

    private async Task AddResultAsync(
        long runId, int versionId, TestDocument doc, string rowKey, string output, decimal value, long instanceId)
    {
        await using var db = sql.CreateContext();
        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var seq = connection.CreateCommand();
        seq.CommandText = "SELECT NEXT VALUE FOR calc.CalculationResultSeq";
        var id = (long)(await seq.ExecuteScalarAsync())!;

        var result = new CalculationResult(runId, versionId, Year2026, doc.DocumentId, rowKey, output, value, unitId);
        typeof(Ecr.Domain.Abstractions.Entity<long>).GetProperty("Id")!.SetValue(result, id);
        result.SetTable(instanceId);
        db.CalculationResults.Add(result);
        await db.SaveChangesAsync();
    }
}
