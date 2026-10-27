// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewSliceEquivalenceTests.cs
using Ecr.Application.Ports;
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
                // Одна комірка на адресу: обчислена, що несе значення; інакше введена (власника); інакше порожня обчислена.
                var cell = cells.FirstOrDefault(c => c.Value.IsCalculated && SlotValue(col, c.Value) is not null)
                           ?? cells.FirstOrDefault(c => !c.Value.IsCalculated) ?? cells.FirstOrDefault();
                var expected = cell is null ? null : SlotValue(col, cell.Value);
                var actual = viewRows[rowKey][col.Code];

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
}
