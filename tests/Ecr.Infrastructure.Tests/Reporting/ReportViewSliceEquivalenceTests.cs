// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewSliceEquivalenceTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// Q-17 (регресія p61, 178 розбіжностей): вʼюха MonthsInColumns з прапором ON мусить віддавати КОЖНУ комірку так само,
/// як зріз API (<see cref="MonthRoutedCellStore"/>): місячні/немісячні колонки, введені/обчислені комірки,
/// числові/рядкові/логічні/дати. Правило на рівні КОМІРКИ: власна обчислена комірка перегляду, якщо вона є; інакше
/// введене значення власника; інакше власна введена копія (запасне значення до консолідації - єдина свідома
/// відмінність від зрізу, який такі копії ховає).
/// </summary>
[Collection("SqlServer")]
public sealed class ReportViewSliceEquivalenceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
    private const int ViewPeriod = 202610;

    /// <summary>Стан власної комірки екземпляра перегляду.</summary>
    private enum Own { None, Input, Calc, CalcEmpty, CalcOtherSlot }

    /// <summary>Стан комірки власника (введеної).</summary>
    private enum Owner { None, Input, Empty }

    private sealed record Col(int Id, string Code, CellDataType Type, bool Month, int? MonthNo);

    private static readonly Own[] Owns = Enum.GetValues<Own>();
    private static readonly Owner[] Owners = Enum.GetValues<Owner>();

    private async Task<(TestDocument Doc, string View, List<Col> Columns)> BuildAsync(int columnCount = 3)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ViewPeriod, columnCount: columnCount, rowCount: Owns.Length * Owners.Length,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        await using var db = sql.CreateContext();
        var extra = new (string Prefix, CellDataType Type)[]
        {
            ("INT", CellDataType.Int), ("FLG", CellDataType.Bool), ("DAY", CellDataType.Date),
            ("STR", CellDataType.String), ("FRM", CellDataType.Formula), ("CLC", CellDataType.Calculated),
        };
        var ordinal = 9000;
        foreach (var (prefix, type) in extra)
        {
            db.ColumnDefs.Add(new ColumnDef(doc.TableDefId, EcrCode.Create(prefix + Guid.NewGuid().ToString("N")[..8]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = prefix }), ordinal++, type));
        }

        await db.SaveChangesAsync();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        version.Publish(publishedByUserId: 1, Now);
        await db.SaveChangesAsync();

        var template = await db.Templates.SingleAsync(t => t.Id == version.TemplateId);
        var table = await db.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        var view = System.Text.RegularExpressions.Regex.Replace(
            $"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{version.Version}", "[^A-Za-z0-9_]", "_");
        var cols = (await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId).OrderBy(c => c.Id).ToListAsync())
            .Select(c => new Col(c.Id, c.Code, c.DataType, c.IsMonthColumn, c.MonthNumber)).ToList();
        return (doc, view, cols);
    }

    private async Task GenerateAsync(int templateVersionId, bool routing)
    {
        await using var db = sql.CreateContext();
        var router = routing
            ? new MonthColumnRouter(
                db,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
                    .Build())
            : null;
        await new ReportViewGenerator(db, monthRouter: router).GenerateAsync(templateVersionId, CancellationToken.None);
    }

    private static string RowKeyOf(TestDocument doc, int index) => $"R{index + 1}_{doc.SheetCode["SHEET".Length..]}";

    private static string SlotOf(CellDataType type) => type switch
    {
        CellDataType.String => "ValueString",
        CellDataType.Bool => "ValueBool",
        CellDataType.Date => "ValueDate",
        _ => "ValueNumeric",
    };

    private static object Typed(CellDataType type, int n) => type switch
    {
        CellDataType.String => $"s{n}",
        CellDataType.Bool => n % 2 == 1,
        CellDataType.Date => new DateTime(2026, 1, 1).AddDays(n),
        _ => (decimal)n + 0.5m,
    };

    private async Task InsertAsync(
        SqlConnection connection, int period, long rowId, Col col, int tableDefId, string slot, object? value,
        bool calculated, bool empty)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, {slot}, IsCalculated, IsEmpty)
            VALUES ({period}, {rowId}, {col.Id}, {tableDefId}, @v, @c, @e);
            """;
        command.Parameters.AddWithValue("@v", value ?? DBNull.Value);
        command.Parameters.AddWithValue("@c", calculated);
        command.Parameters.AddWithValue("@e", empty);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Заповнює таблицю: рядок r = (власна, власник) за переліками; колонки - усі типи.</summary>
    private async Task<(TestDocument Doc, string View, List<Col> Cols, Dictionary<string, (Own Own, Owner Owner)> States)>
        SeedAsync(bool routing)
    {
        var (doc, view, cols) = await BuildAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var owners = new Dictionary<int, TestPeriodInstance>();
        foreach (var p in new[] { 202601, 202603, 202611 })
        {
            owners[p] = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(p));
        }

        var states = new Dictionary<string, (Own, Owner)>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        var rowNo = 0;
        foreach (var own in Owns)
        {
            foreach (var owner in Owners)
            {
                var rowKey = RowKeyOf(doc, rowNo);
                states[rowKey] = (own, owner);
                foreach (var col in cols)
                {
                    var ownerPeriod = col.Month ? 202600 + col.MonthNo!.Value : 202601;
                    if (col.Month && col.MonthNo is not (3 or 10 or 11))
                    {
                        continue;   // решта місяців - лише для ширини вʼюхи
                    }

                    var slot = SlotOf(col.Type);
                    if (own != Own.None)
                    {
                        var calc = own != Own.Input;
                        var value = own switch
                        {
                            Own.CalcEmpty => null,
                            Own.CalcOtherSlot => (object)"#DIV/0",
                            _ => Typed(col.Type, 1),
                        };
                        var s = own == Own.CalcOtherSlot ? (col.Type == CellDataType.String ? "ValueNumeric" : "ValueString") : slot;
                        if (own == Own.CalcOtherSlot && col.Type == CellDataType.String)
                        {
                            value = 7m;
                        }

                        await InsertAsync(connection, ViewPeriod, doc.RowIds[rowNo], col, doc.TableDefId, s, value, calc,
                            own == Own.CalcEmpty);
                    }

                    if (owner != Owner.None && ownerPeriod != ViewPeriod && owners.TryGetValue(ownerPeriod, out var ownerInstance))
                    {
                        await InsertAsync(connection, ownerPeriod, ownerInstance.RowIds[rowNo], col, doc.TableDefId, slot,
                            owner == Owner.Input ? Typed(col.Type, 2) : null, calculated: false, empty: owner == Owner.Empty);
                    }
                }

                rowNo++;
            }
        }

        await GenerateAsync(doc.TemplateVersionId, routing);
        return (doc, view, cols, states);
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> ReadViewAsync(TestDocument doc, string view)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM rpt.[{view}] WHERE _DocumentId = @d AND _PeriodKey = {ViewPeriod}";
        command.Parameters.AddWithValue("@d", doc.DocumentId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            result[(string)row["_RowKey"]!] = row;
        }

        return result;
    }

    private async Task<List<CellRecord>> SliceAsync(TestDocument doc)
    {
        await using var db = sql.CreateContext();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
            .Build();
        using var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var store = new MonthRoutedCellStore(
            new NormalizedCellStore(db), new MonthColumnRouter(db, config),
            new Ecr.Infrastructure.Caching.MetadataCache(memory, db), db);
        return [.. await store.ReadSliceAsync(doc.TableInstanceId, new PeriodKey(ViewPeriod), CancellationToken.None)];
    }

    private static object? SlotValue(Col col, CellValueData v) => col.Type switch
    {
        CellDataType.String => v.ValueString,
        CellDataType.Bool => v.ValueBool,
        CellDataType.Date => v.ValueDate,
        _ => v.ValueNumeric,
    };

    private static bool Same(object? a, object? b)
        => a is null || b is null ? a is null && b is null
            : a is decimal da && b is decimal db ? da == db
            : Equals(a, b);

    /// <summary>
    /// Для КОЖНОЇ колонки й кожної комбінації (власна комірка x комірка власника) вʼюха ON == зріз API. Зріз з обома
    /// комірками на адресі (обчислена перегляду + введена власника) зводиться до обчисленої - правило "обчислена
    /// комірка перегляду приховує введену власника". Єдина допустима відмінність - власна ВВЕДЕНА копія як запасне
    /// значення вʼюхи (зріз для чужого власника її ховає).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Вʼюха_з_прапором_дорівнює_зрізу_для_всіх_колонок_і_станів_комірок()
    {
        var (doc, view, cols, states) = await SeedAsync(routing: true);
        var viewRows = await ReadViewAsync(doc, view);
        var slice = await SliceAsync(doc);
        var rowKeyById = doc.RowIds.Select((id, i) => (id, key: RowKeyOf(doc, i))).ToDictionary(x => x.id, x => x.key);

        var diffs = new List<string>();
        foreach (var rowKey in states.Keys)
        {
            var rowId = rowKeyById.Single(kv => kv.Value == rowKey).Key;
            var (own, owner) = states[rowKey];
            foreach (var col in cols)
            {
                var cells = slice.Where(c => c.Address.TableRowId == rowId && c.Address.ColumnDefId == col.Id).ToList();
                // Зріз віддає РІВНО одну комірку на адресу (обчислена зі значенням, інакше власник, інакше порожня обчислена).
                if (cells.Count > 1)
                {
                    diffs.Add($"{col.Type}{(col.Month ? "/M" + col.MonthNo : "")} own={own} owner={owner}: у зрізі {cells.Count} комірок на адресі");
                    continue;
                }

                var cell = cells.SingleOrDefault();
                var expected = cell is null ? null : SlotValue(col, cell.Value);
                var actual = viewRows[rowKey][col.Code];

                // Незалежний еталон для порожньої обчисленої + введеного власника (лише колонки з чужим власником):
                // віддається значення власника, а не порожнеча.
                var foreignOwner = (!col.Month || col.MonthNo is 3 or 11)   // місяці 3 і 11 мають екземпляр-власник (10 = сам перегляд)
                                   && col.Type is not (CellDataType.Formula or CellDataType.Calculated);
                if (own is Own.CalcEmpty or Own.CalcOtherSlot && owner == Owner.Input && foreignOwner
                    && !Same(expected, Typed(col.Type, 2)))
                {
                    diffs.Add($"{col.Type}{(col.Month ? "/M" + col.MonthNo : "")} own={own} owner={owner}: зріз={expected ?? "NULL"}, очікувалось значення власника");
                }

                var ownCopyFallback = own == Own.Input && Same(actual, Typed(col.Type, 1))
                                      ;
                if (!Same(expected, actual) && !ownCopyFallback)
                {
                    diffs.Add($"{col.Type}{(col.Month ? "/M" + col.MonthNo : "")} own={own} owner={owner}: зріз={expected ?? "NULL"} вʼюха={actual ?? "NULL"}");
                }
            }
        }

        Assert.True(diffs.Count == 0, $"{diffs.Count} розбіжностей:\n" + string.Join('\n', diffs.Take(40)));
    }

    // ───────────────────────── Q-17.recalc: результат методології (колонка Calculated) ─────────────────────────

    private const string RunStatusCompleted = "Completed";

    private async Task<(int MethodologyId, int VersionId)> AddMethodologyAsync(string tag)
    {
        await using var db = sql.CreateContext();
        var methodology = new Methodology(
            EcrCode.Create($"RCV_{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = tag }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();
        return (methodology.Id, version.Id);
    }

    private async Task<long> AddRunAsync(TestDocument doc, bool current, int period = ViewPeriod, bool projectLevel = false)
    {
        await using var db = sql.CreateContext();
        var run = new CalculationRun(doc.ProjectId, period, triggeredByUserId: 1, Now, projectLevel ? null : doc.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();
        run.Complete(RunStatusCompleted, Now, profileJson: null, errorMessage: null);
        if (current)
        {
            run.MakeCurrent();
        }
        else
        {
            run.Supersede();
        }

        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task AddResultAsync(
        long runId, int versionId, TestDocument doc, int rowIndex, string output, decimal value, int unitId,
        long? substance = null, long? instance = null, int period = ViewPeriod)
    {
        await using var db = sql.CreateContext();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var seq = connection.CreateCommand();
        seq.CommandText = "SELECT NEXT VALUE FOR calc.CalculationResultSeq";
        var id = (long)(await seq.ExecuteScalarAsync())!;

        var result = new CalculationResult(runId, versionId, period, doc.DocumentId, RowKeyOf(doc, rowIndex), output, value, unitId);
        typeof(Ecr.Domain.Abstractions.Entity<long>).GetProperty("Id")!.SetValue(result, id);
        result.SetSubstance(substance);
        result.SetTable(instance);
        db.CalculationResults.Add(result);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Q-17.recalc: колонка Calculated у вʼюхі == колонці у справжньому зрізі (слайс + <see cref="CalculatedCellOverlay"/>, тобто
    /// те, що віддає GetTableSliceHandler) - за ОБОХ значень прапора. Покрито: проста прив'язка; кілька прив'язок на колонку
    /// (виграє найбільший Id, що має значення); предикат MatchJson; різні речовини сумуються, розбіжні значення однієї
    /// речовини - NULL; результат цього екземпляра точніший за результат без екземпляра, чужий екземпляр ігнорується;
    /// результат неактуального прогону ігнорується; незвʼязана Calculated-колонка лишає матеріалізоване значення.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Колонка_Calculated_у_вʼюсі_дорівнює_результату_методології_у_зрізі(bool routing)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ViewPeriod, columnCount: 2, rowCount: 4,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        var codes = new[] { "STRP", "CLA", "CLB", "CLC", "CLD", "CLE", "CLF", "CLG" };
        await using (var db = sql.CreateContext())
        {
            var ordinal = 9000;
            foreach (var code in codes)
            {
                db.ColumnDefs.Add(new ColumnDef(doc.TableDefId, EcrCode.Create(code + Guid.NewGuid().ToString("N")[..6]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal++,
                    code == "STRP" ? CellDataType.String : CellDataType.Calculated));
            }

            await db.SaveChangesAsync();
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(publishedByUserId: 1, Now);
            await db.SaveChangesAsync();
        }

        await using var read = sql.CreateContext();
        var table = await read.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        var template = await read.Templates.SingleAsync(t => t.Id == doc.TemplateId);
        var tv = await read.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        var view = System.Text.RegularExpressions.Regex.Replace(
            $"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{tv.Version}", "[^A-Za-z0-9_]", "_");
        var extra = (await read.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId && c.Ordinal >= 9000)
            .OrderBy(c => c.Id).ToListAsync()).ToDictionary(c => codes[(int)(c.Ordinal - 9000)], c => c);
        var unitId = await read.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        // Прив'язки: A - проста; B - дві (друга має більший Id); C - предикат; D - речовини; E - екземпляри; F - лише неактуальний прогін.
        var mA = await AddMethodologyAsync("A" + Guid.NewGuid().ToString("N")[..6]);
        var mB1 = await AddMethodologyAsync("B1" + Guid.NewGuid().ToString("N")[..6]);
        var mB2 = await AddMethodologyAsync("B2" + Guid.NewGuid().ToString("N")[..6]);
        var mC = await AddMethodologyAsync("C" + Guid.NewGuid().ToString("N")[..6]);
        var mD = await AddMethodologyAsync("D" + Guid.NewGuid().ToString("N")[..6]);
        var mE = await AddMethodologyAsync("E" + Guid.NewGuid().ToString("N")[..6]);
        var mF = await AddMethodologyAsync("F" + Guid.NewGuid().ToString("N")[..6]);
        await using (var db = sql.CreateContext())
        {
            var strId = extra["STRP"].Id;
            db.CalculationBindings.AddRange(
                new CalculationBinding(doc.TableDefId, extra["CLA"].Id, mA.MethodologyId, "OA", "{}"),
                new CalculationBinding(doc.TableDefId, extra["CLB"].Id, mB1.MethodologyId, "OB", "{}"),
                new CalculationBinding(doc.TableDefId, extra["CLB"].Id, mB2.MethodologyId, "OB", "{ }"),
                new CalculationBinding(doc.TableDefId, extra["CLC"].Id, mC.MethodologyId, "OC", $"{{\"{strId}\": \"s1\"}}"),
                new CalculationBinding(doc.TableDefId, extra["CLD"].Id, mD.MethodologyId, "OD", "{}"),
                new CalculationBinding(doc.TableDefId, extra["CLE"].Id, mE.MethodologyId, "OE", "{}"),
                new CalculationBinding(doc.TableDefId, extra["CLF"].Id, mF.MethodologyId, "OF", "{}"));
            await db.SaveChangesAsync();
        }

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            var strCol = new Col(extra["STRP"].Id, "STRP", CellDataType.String, false, null);
            string[] strings = ["s1", "other", "s1", "s1"];
            for (var i = 0; i < 4; i++)
            {
                await InsertAsync(connection, ViewPeriod, doc.RowIds[i], strCol, doc.TableDefId, "ValueString", strings[i], false, false);
            }

            // CLG без прив'язки: матеріалізоване значення слайс і вʼюха віддають однаково.
            await InsertAsync(connection, ViewPeriod, doc.RowIds[0],
                new Col(extra["CLG"].Id, "CLG", CellDataType.Calculated, false, null), doc.TableDefId, "ValueNumeric", 9.5m, true, false);
        }

        var current = await AddRunAsync(doc, current: true);
        var old = await AddRunAsync(doc, current: false);
        long foreign = 987_654_321;
        await AddResultAsync(current, mA.VersionId, doc, 0, "OA", 5.5m, unitId);
        await AddResultAsync(current, mA.VersionId, doc, 2, "OA", 1.25m, unitId);
        await AddResultAsync(old, mA.VersionId, doc, 3, "OA", 99m, unitId);
        await AddResultAsync(current, mB1.VersionId, doc, 0, "OB", 3m, unitId);
        await AddResultAsync(current, mB2.VersionId, doc, 0, "OB", 7m, unitId);
        await AddResultAsync(current, mB1.VersionId, doc, 2, "OB", 3m, unitId);
        await AddResultAsync(current, mC.VersionId, doc, 0, "OC", 2m, unitId);
        await AddResultAsync(current, mC.VersionId, doc, 1, "OC", 2m, unitId);
        await AddResultAsync(current, mC.VersionId, doc, 2, "OC", 4m, unitId);
        await AddResultAsync(current, mD.VersionId, doc, 0, "OD", 1.5m, unitId, substance: null);
        await AddResultAsync(current, mD.VersionId, doc, 0, "OD", 2m, unitId, substance: 1);
        await AddResultAsync(current, mD.VersionId, doc, 1, "OD", 1.5m, unitId, substance: 1);
        await AddResultAsync(current, mD.VersionId, doc, 1, "OD", 1.6m, unitId, substance: 1);
        await AddResultAsync(current, mE.VersionId, doc, 0, "OE", 4m, unitId, instance: doc.TableInstanceId);
        await AddResultAsync(current, mE.VersionId, doc, 0, "OE", 9m, unitId);
        await AddResultAsync(current, mE.VersionId, doc, 1, "OE", 5m, unitId, instance: foreign);
        await AddResultAsync(current, mE.VersionId, doc, 2, "OE", 9m, unitId);
        await AddResultAsync(old, mF.VersionId, doc, 0, "OF", 8m, unitId);

        await GenerateAsync(doc.TemplateVersionId, routing);
        var viewRows = await ReadViewAsync(doc, view);

        // Еталон: справжній слайс + накладення результатів (рівно те, що робить GetTableSliceHandler).
        await using var db2 = sql.CreateContext();
        var physical = routing
            ? await SliceAsync(doc)
            : [.. await new NormalizedCellStore(db2).ReadSliceAsync(doc.TableInstanceId, new PeriodKey(ViewPeriod), CancellationToken.None)];
        var rowIds = doc.RowIds.Select((id, i) => (key: RowKeyOf(doc, i), id)).ToDictionary(x => x.key, x => x.id);
        var calcIds = extra.Where(kv => kv.Key != "STRP").Select(kv => kv.Value.Id).ToHashSet();
        var overlaid = await new CalculatedCellOverlay(new MethodologyStore(db2), new CalculationResultStore(db2, new TestClock(Now)))
            .ApplyAsync(doc.DocumentId, ViewPeriod,
                [new OverlayTable(doc.TableInstanceId, doc.TableDefId, rowIds, physical, calcIds)], CancellationToken.None);
        var slice = overlaid[doc.TableInstanceId];

        var diffs = new List<string>();
        var nonNull = 0;
        for (var i = 0; i < 4; i++)
        {
            var rowKey = RowKeyOf(doc, i);
            foreach (var (code, column) in extra.Where(kv => kv.Key != "STRP"))
            {
                var cell = slice.SingleOrDefault(c => c.Address.TableRowId == doc.RowIds[i] && c.Address.ColumnDefId == column.Id);
                var expected = cell?.Value.ValueNumeric;
                var actual = viewRows[rowKey][column.Code];
                nonNull += expected is null ? 0 : 1;
                if (!Same(expected, actual))
                {
                    diffs.Add($"{code} {rowKey}: зріз={(expected is { } e ? e.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NULL")} вʼюха={actual ?? "NULL"}");
                }
            }
        }

        Assert.True(nonNull >= 9, $"тест порожній: у зрізі лише {nonNull} значень Calculated");
        Assert.True(diffs.Count == 0, $"routing={routing}: {diffs.Count} розбіжностей:\n" + string.Join('\n', diffs));
    }

    private async Task<List<CellRecord>> SliceOfAsync(long instanceId, int period, bool routing)
    {
        await using var db = sql.CreateContext();
        if (!routing)
        {
            return [.. await new NormalizedCellStore(db).ReadSliceAsync(instanceId, new PeriodKey(period), CancellationToken.None)];
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
            .Build();
        using var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var store = new MonthRoutedCellStore(
            new NormalizedCellStore(db), new MonthColumnRouter(db, config),
            new Ecr.Infrastructure.Caching.MetadataCache(memory, db), db);
        return [.. await store.ReadSliceAsync(instanceId, new PeriodKey(period), CancellationToken.None)];
    }

    private async Task<string> DefinitionAsync(string view)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(@n))";
        command.Parameters.AddWithValue("@n", $"rpt.[{view}]");
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> ExecScalarAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = text;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Q-17.rvseek: читання rv "за прогонами" (<c>ru.PeriodKey = ti.PeriodKey</c> + <c>INNER LOOP JOIN</c> до результатів) змінює ПЛАН, а не
    /// семантику. Для обох гілок rv - наборової (прив'язки без предиката) і гілки з предикатом MatchJson (є активна прив'язка з
    /// предикатом на момент генерації), за обох значень прапора маршрутизації місяців: (1) текст вʼюхи містить саме очікувану гілку
    /// (наявність фільтра періоду прогону і підказки LOOP порахована); (2) колонки Calculated вʼюхи == справжній зріз + накладення
    /// (<see cref="CalculatedCellOverlay"/>) для двох екземплярів: період із прогоном документа (неактуальний прогін і проєктний
    /// прогін того ж періоду ігноруються) і період без прогону документа (діє проєктний прогін, запасний варіант); (3) СТАРИЙ вигляд
    /// (той самий текст без фільтра періоду й підказки) і НОВИЙ дають однакові набори (EXCEPT в обидва боки = 0).
    /// Мутація: прибрати <c>AND ru.PeriodKey = ti.PeriodKey</c> з обох гілок - частина (1) червона; змінити період прогону у
    /// проєктного прогону на ViewPeriod - (2) червона.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Читання_rv_за_прогонами_не_змінює_результат_для_обох_гілок_і_обох_значень_прапора(bool routing, bool predicate)
    {
        const int Other = 202611;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ViewPeriod, columnCount: 2, rowCount: 4,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        var codes = new[] { "STRP", "CLA", "CLB", "CLC" };
        await using (var db = sql.CreateContext())
        {
            var ordinal = 9000;
            foreach (var code in codes)
            {
                db.ColumnDefs.Add(new ColumnDef(doc.TableDefId, EcrCode.Create(code + Guid.NewGuid().ToString("N")[..6]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal++,
                    code == "STRP" ? CellDataType.String : CellDataType.Calculated));
            }

            await db.SaveChangesAsync();
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(publishedByUserId: 1, Now);
            await db.SaveChangesAsync();
        }

        var other = await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Other));

        await using var read = sql.CreateContext();
        var table = await read.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        var template = await read.Templates.SingleAsync(t => t.Id == doc.TemplateId);
        var tv = await read.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        var view = System.Text.RegularExpressions.Regex.Replace(
            $"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{tv.Version}", "[^A-Za-z0-9_]", "_");
        var extra = (await read.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId && c.Ordinal >= 9000)
            .OrderBy(c => c.Id).ToListAsync()).ToDictionary(c => codes[(int)(c.Ordinal - 9000)], c => c);
        var unitId = await read.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        var mA = await AddMethodologyAsync("A" + Guid.NewGuid().ToString("N")[..6]);
        var mB1 = await AddMethodologyAsync("B1" + Guid.NewGuid().ToString("N")[..6]);
        var mB2 = await AddMethodologyAsync("B2" + Guid.NewGuid().ToString("N")[..6]);
        var mC = await AddMethodologyAsync("C" + Guid.NewGuid().ToString("N")[..6]);
        await using (var db = sql.CreateContext())
        {
            var strId = extra["STRP"].Id;
            db.CalculationBindings.AddRange(
                new CalculationBinding(doc.TableDefId, extra["CLA"].Id, mA.MethodologyId, "OA", "{}"),
                new CalculationBinding(doc.TableDefId, extra["CLB"].Id, mB1.MethodologyId, "OB", "{}"),
                new CalculationBinding(doc.TableDefId, extra["CLB"].Id, mB2.MethodologyId, "OB", "{ }"),
                new CalculationBinding(doc.TableDefId, extra["CLC"].Id, mC.MethodologyId, "OC",
                    predicate ? $"{{\"{strId}\": \"s1\"}}" : "{}"));
            await db.SaveChangesAsync();
        }

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            var strCol = new Col(extra["STRP"].Id, "STRP", CellDataType.String, false, null);
            string[] strings = ["s1", "other", "s1", "s1"];
            for (var i = 0; i < 4; i++)
            {
                await InsertAsync(connection, ViewPeriod, doc.RowIds[i], strCol, doc.TableDefId, "ValueString", strings[i], false, false);
                await InsertAsync(connection, Other, other.RowIds[i], strCol, doc.TableDefId, "ValueString", strings[i], false, false);
            }
        }

        // 202610: актуальний прогін документа; неактуальний (ігнор); проєктний актуальний того ж періоду (ігнор: є прогін документа).
        var cur = await AddRunAsync(doc, current: true);
        var old = await AddRunAsync(doc, current: false);
        var proj10 = await AddRunAsync(doc, current: true, projectLevel: true);
        // 202611: прогону документа немає - діє проєктний актуальний; неактуальний прогін документа (ігнор). 202612: проєктний (без екземпляра).
        var proj11 = await AddRunAsync(doc, current: true, period: Other, projectLevel: true);
        var old11 = await AddRunAsync(doc, current: false, period: Other);
        var proj12 = await AddRunAsync(doc, current: true, period: 202612, projectLevel: true);

        await AddResultAsync(cur, mA.VersionId, doc, 0, "OA", 5.5m, unitId);
        await AddResultAsync(cur, mA.VersionId, doc, 2, "OA", 1.25m, unitId);
        await AddResultAsync(old, mA.VersionId, doc, 3, "OA", 99m, unitId);
        await AddResultAsync(proj10, mA.VersionId, doc, 1, "OA", 55m, unitId);
        await AddResultAsync(cur, mB1.VersionId, doc, 0, "OB", 3m, unitId);
        await AddResultAsync(cur, mB2.VersionId, doc, 0, "OB", 7m, unitId);
        await AddResultAsync(cur, mB1.VersionId, doc, 2, "OB", 3m, unitId);
        await AddResultAsync(cur, mC.VersionId, doc, 0, "OC", 2m, unitId);
        await AddResultAsync(cur, mC.VersionId, doc, 1, "OC", 2m, unitId);
        await AddResultAsync(cur, mC.VersionId, doc, 2, "OC", 4m, unitId);
        await AddResultAsync(proj11, mA.VersionId, doc, 0, "OA", 3m, unitId, period: Other);
        await AddResultAsync(proj11, mB1.VersionId, doc, 1, "OB", 4m, unitId, period: Other);
        await AddResultAsync(proj11, mC.VersionId, doc, 0, "OC", 6m, unitId, period: Other);
        await AddResultAsync(proj11, mC.VersionId, doc, 1, "OC", 8m, unitId, period: Other);
        await AddResultAsync(old11, mA.VersionId, doc, 2, "OA", 77m, unitId, period: Other);
        await AddResultAsync(proj12, mA.VersionId, doc, 0, "OA", 1m, unitId, period: 202612);

        var oldView = view + "_rvold";
        try
        {
            await GenerateAsync(doc.TemplateVersionId, routing);
            var text = await DefinitionAsync(view);

            // (1) гілка справді згенерована: наборова має підказку LOOP і фільтр періоду прогону; предикатна - лише фільтр.
            Assert.Contains("ru.PeriodKey = ti.PeriodKey", text, StringComparison.Ordinal);
            Assert.Equal(predicate ? 0 : 1, System.Text.RegularExpressions.Regex.Count(text, "INNER LOOP JOIN calc.CalculationResult"));
            Assert.Equal(predicate, text.Contains("ISJSON(b.MatchJson)", StringComparison.Ordinal));
            Assert.Equal(predicate ? 0 : 1, System.Text.RegularExpressions.Regex.Count(text, "LEFT HASH JOIN \\(SELECT q.SourceRowKey"));

            // (3) старий вигляд = той самий текст без фільтра періоду прогону й без підказки.
            var oldText = text
                .Replace(" AND ru.PeriodKey = ti.PeriodKey", "", StringComparison.Ordinal)
                .Replace("INNER LOOP JOIN calc.CalculationResult", "JOIN calc.CalculationResult", StringComparison.Ordinal);
            Assert.NotEqual(text, oldText);
            oldText = System.Text.RegularExpressions.Regex.Replace(
                oldText, @"^\s*(CREATE|ALTER)\s+VIEW\s+\S+", $"CREATE VIEW rpt.[{oldView}]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            await ExecScalarAsync($"IF OBJECT_ID(N'rpt.[{oldView}]') IS NOT NULL DROP VIEW rpt.[{oldView}]; SELECT 0");
            await using (var connection = new SqlConnection(sql.ConnectionString))
            {
                await connection.OpenAsync();
                await using var create = connection.CreateCommand();
                create.CommandText = oldText;
                await create.ExecuteNonQueryAsync();
            }

            var rows = await ExecScalarAsync($"SELECT COUNT(*) FROM rpt.[{view}] WHERE _DocumentId = {doc.DocumentId}");
            Assert.Equal(8, rows);
            Assert.Equal(rows, await ExecScalarAsync($"SELECT COUNT(*) FROM rpt.[{oldView}] WHERE _DocumentId = {doc.DocumentId}"));
            Assert.Equal(0, await ExecScalarAsync(
                $"SELECT COUNT(*) FROM (SELECT * FROM rpt.[{view}] EXCEPT SELECT * FROM rpt.[{oldView}]) AS x"));
            Assert.Equal(0, await ExecScalarAsync(
                $"SELECT COUNT(*) FROM (SELECT * FROM rpt.[{oldView}] EXCEPT SELECT * FROM rpt.[{view}]) AS x"));

            // (2) еталон: справжній зріз + накладення результатів для обох екземплярів.
            var calcIds = extra.Where(kv => kv.Key != "STRP").Select(kv => kv.Value.Id).ToHashSet();
            var diffs = new List<string>();
            var nonNull = 0;
            foreach (var (period, instanceId, rowIds) in new[]
                     {
                         (ViewPeriod, doc.TableInstanceId, (IReadOnlyList<long>)doc.RowIds),
                         (Other, other.TableInstanceId, other.RowIds),
                     })
            {
                var viewRows = await ReadViewPeriodAsync(doc, view, period);
                var physical = await SliceOfAsync(instanceId, period, routing);
                await using var db2 = sql.CreateContext();
                var overlaid = await new CalculatedCellOverlay(new MethodologyStore(db2), new CalculationResultStore(db2, new TestClock(Now)))
                    .ApplyAsync(doc.DocumentId, period,
                        [new OverlayTable(instanceId, doc.TableDefId, rowIds.Select((id, i) => (key: RowKeyOf(doc, i), id)).ToDictionary(x => x.key, x => x.id),
                            physical, calcIds)], CancellationToken.None);
                var slice = overlaid[instanceId];
                for (var i = 0; i < 4; i++)
                {
                    foreach (var (code, column) in extra.Where(kv => kv.Key != "STRP"))
                    {
                        var cell = slice.SingleOrDefault(c => c.Address.TableRowId == rowIds[i] && c.Address.ColumnDefId == column.Id);
                        var expected = cell?.Value.ValueNumeric;
                        var actual = viewRows[RowKeyOf(doc, i)][column.Code];
                        nonNull += expected is null ? 0 : 1;
                        if (!Same(expected, actual))
                        {
                            diffs.Add($"{period} {code} {RowKeyOf(doc, i)}: зріз={expected?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"} вʼюха={actual ?? "NULL"}");
                        }
                    }
                }
            }

            Assert.True(nonNull >= 8, $"тест порожній: у зрізі лише {nonNull} значень Calculated");
            Assert.True(diffs.Count == 0, $"routing={routing} predicate={predicate}: {diffs.Count} розбіжностей:\n" + string.Join('\n', diffs));
        }
        finally
        {
            await ExecScalarAsync($"IF OBJECT_ID(N'rpt.[{oldView}]') IS NOT NULL DROP VIEW rpt.[{oldView}]; SELECT 0");
        }
    }

    private async Task<Dictionary<string, Dictionary<string, object?>>> ReadViewPeriodAsync(TestDocument doc, string view, int period)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM rpt.[{view}] WHERE _DocumentId = @d AND _PeriodKey = {period}";
        command.Parameters.AddWithValue("@d", doc.DocumentId);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            result[(string)row["_RowKey"]!] = row;
        }

        return result;
    }

    /// <summary>
    /// AN-147 (семантика супутника): <c>&lt;Код&gt;_Text</c> колонки Calculated = ValueString ЛИШЕ коли ПІДСУМКОВЕ число колонки
    /// (результат методології, інакше матеріалізоване) порожнє. Методологія дала число, а в doc.CellValue лежить рядок -
    /// супутник NULL (число лишається числом); немає ні результату, ні числа - рядок у супутнику. Прапор вимкнений і увімкнений.
    /// Мутація: у <c>@selectTextRv</c> замінити COALESCE(rv, ValueNumeric) на ValueNumeric - тест червоний.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.2")]
    public async Task Супутник_Text_Calculated_порожній_коли_методологія_дала_число(bool routing)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ViewPeriod, columnCount: 2, rowCount: 3,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);

        string calcCode;
        await using (var db = sql.CreateContext())
        {
            calcCode = "CLT" + Guid.NewGuid().ToString("N")[..6];
            db.ColumnDefs.Add(new ColumnDef(doc.TableDefId, EcrCode.Create(calcCode),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "CLT" }), 9000, CellDataType.Calculated));
            await db.SaveChangesAsync();
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(publishedByUserId: 1, Now);
            await db.SaveChangesAsync();
        }

        await using var read = sql.CreateContext();
        var table = await read.TableDefs.SingleAsync(t => t.Id == doc.TableDefId);
        var template = await read.Templates.SingleAsync(t => t.Id == doc.TemplateId);
        var tv = await read.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
        var view = System.Text.RegularExpressions.Regex.Replace(
            $"v_{template.Code}_{doc.SheetCode}_{table.Code}_v{tv.Version}", "[^A-Za-z0-9_]", "_");
        var column = await read.ColumnDefs.AsNoTracking().SingleAsync(c => c.TableDefId == doc.TableDefId && c.Ordinal == 9000);
        var unitId = await read.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        var m = await AddMethodologyAsync("T" + Guid.NewGuid().ToString("N")[..6]);
        await using (var db = sql.CreateContext())
        {
            db.CalculationBindings.Add(new CalculationBinding(doc.TableDefId, column.Id, m.MethodologyId, "OT", "{}"));
            await db.SaveChangesAsync();
        }

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            var col = new Col(column.Id, calcCode, CellDataType.Calculated, false, null);
            // Рядки 0 і 1: рядок у ValueString; рядок 0 має результат методології, рядок 1 - ні. Рядок 2: результат без рядка.
            await InsertAsync(connection, ViewPeriod, doc.RowIds[0], col, doc.TableDefId, "ValueString", "Sample J-0", true, false);
            await InsertAsync(connection, ViewPeriod, doc.RowIds[1], col, doc.TableDefId, "ValueString", "Sample J-1", true, false);
        }

        var run = await AddRunAsync(doc, current: true);
        await AddResultAsync(run, m.VersionId, doc, 0, "OT", 5.5m, unitId);
        await AddResultAsync(run, m.VersionId, doc, 2, "OT", 2m, unitId);

        await GenerateAsync(doc.TemplateVersionId, routing);
        var rows = await ReadViewAsync(doc, view);
        var text = calcCode + "_Text";

        Assert.Equal(5.5m, Convert.ToDecimal(rows[RowKeyOf(doc, 0)][calcCode], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(rows[RowKeyOf(doc, 0)][text]);
        Assert.Null(rows[RowKeyOf(doc, 1)][calcCode]);
        Assert.Equal("Sample J-1", rows[RowKeyOf(doc, 1)][text]);
        Assert.Equal(2m, Convert.ToDecimal(rows[RowKeyOf(doc, 2)][calcCode], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(rows[RowKeyOf(doc, 2)][text]);
    }
}
