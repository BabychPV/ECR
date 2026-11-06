// tests/Ecr.Infrastructure.Tests/Persistence/CellSliceReadFidelityTests.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Читання зрізу (<see cref="NormalizedCellStore"/>: усі чотири форми <c>ReadSliceAsync</c>/<c>ReadSlicesAsync</c>)
/// віддає ТІ САМІ значення й у ТОМУ САМОМУ порядку, що й матеріалізація EF тієї самої вибірки.
/// </summary>
/// <remarks>
/// ⚡ Перф зрізу: рядки читаються з <c>DbDataReader</c> напряму в <see cref="CellRecord"/>, а не через проміжний
/// об'єкт EF. Ціна помилки тут — тихо інше значення в сітці (масштаб числа, порожнеча, переплутаний слот), тож еталон
/// — НЕЗАЛЕЖНЕ читання сутностей <c>doc.CellValue</c> через EF, а порівняння — за текстом, що несе масштаб
/// <c>decimal</c> (<c>1.5000000000000000</c> ≠ <c>1.5</c>), тики й <see cref="DateTimeKind"/> дати і різницю
/// <c>null</c> / порожній рядок / явна порожнеча.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellSliceReadFidelityTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Усі_типи_значень_доживають_до_зрізу_без_змін_у_кожній_формі_читання()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202605, columnCount: 8, rowCount: 3, ct: ct);
        var other = await builder.BuildAsync(periodKey: 202605, columnCount: 3, rowCount: 2, ct: ct);
        var (entryId, unitId) = await RegistryEntryAndUnitAsync(builder, ct);

        await using var db = builder.CreateContext();
        var store = new NormalizedCellStore(db, new ArchiveAwareCellReader(db));

        var c = doc.ColumnDefIds;
        var r = doc.RowIds;
        var at = (int row, int column) => new CellAddress(doc.PeriodKey, r[row], c[column]);
        CellRecord Cell(int row, int column, CellValueData value) => new(at(row, column), doc.TableDefId, value);

        await store.ApplyAsync(
            new CellChangeSet(
                doc.TableInstanceId,
                [
                    Cell(0, 1, new CellValueData { ValueString = "Київ" }),
                    Cell(0, 2, new CellValueData { ValueNumeric = Number("0.1234567890123456") }),
                    Cell(0, 3, new CellValueData { ValueNumeric = Number("1000800000000.1234567890123456") }),
                    Cell(0, 4, new CellValueData { ValueNumeric = 1.5m }),
                    Cell(0, 5, new CellValueData { ValueNumeric = Number("-0.0000000000000001") }),
                    Cell(0, 6, new CellValueData { ValueDate = new DateTime(2026, 3, 14, 15, 9, 26, 535, DateTimeKind.Utc) }),
                    Cell(0, 7, new CellValueData { ValueBool = true }),
                    Cell(1, 1, new CellValueData { ValueBool = false }),
                    Cell(1, 2, new CellValueData { ValueRegistryEntryId = entryId }),
                    Cell(1, 3, new CellValueData { ValueUnitId = unitId }),
                    Cell(1, 4, CellValueData.Empty),
                    Cell(1, 5, new CellValueData { ValueNumeric = 0m }),
                    Cell(1, 6, new CellValueData { ValueString = "a  " }),
                    Cell(1, 7, new CellValueData { ValueNumeric = 42m, IsCalculated = true }),
                    Cell(2, 1, new CellValueData { ValueString = new string('я', 1000) }),
                    Cell(2, 2, new CellValueData { ValueNumeric = 12.25m, ValueUnitId = unitId }),
                    Cell(2, 3, new CellValueData { ValueString = string.Empty }),
                ],
                [],
                r,
                ChangedByUserId: 1,
                IsLateEdit: false),
            ct);

        await store.ApplyAsync(
            new CellChangeSet(
                other.TableInstanceId,
                [new CellRecord(new CellAddress(other.PeriodKey, other.RowIds[1], other.ColumnDefIds[1]), other.TableDefId, new CellValueData { ValueNumeric = 7m })],
                [],
                other.RowIds,
                ChangedByUserId: 1,
                IsLateEdit: false),
            ct);

        var expected = await ExpectedAsync(db, doc.PeriodKey, r, ct);
        Assert.Equal(17, expected.Count);

        // Передумови: еталон справді несе те, що треба відрізнити (інакше рівність нічого не доводить).
        Assert.Contains(expected, e => e.Contains("|N1.5000000000000000|", StringComparison.Ordinal));
        Assert.Contains(expected, e => e.Contains("|N1000800000000.1234567890123456|", StringComparison.Ordinal));
        Assert.Contains(expected, e => e.EndsWith("|calc0|empty1", StringComparison.Ordinal));
        Assert.Contains(expected, e => e.Contains("|S'a  '|", StringComparison.Ordinal));
        Assert.Contains(expected, e => e.Contains("|S''|", StringComparison.Ordinal));
        Assert.Contains(expected, e => e.Contains($"|R{entryId}|", StringComparison.Ordinal));

        Assert.Equal(expected, Keys(await store.ReadSliceAsync(doc.TableInstanceId, ct)));
        Assert.Equal(expected, Keys(await store.ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, ct)));

        var batch = await store.ReadSlicesAsync([doc.TableInstanceId, other.TableInstanceId], ct);
        Assert.Equal(expected, Keys(batch[doc.TableInstanceId]));
        Assert.Single(batch[other.TableInstanceId]);

        var inPeriod = await store.ReadSlicesAsync([other.TableInstanceId, doc.TableInstanceId], doc.PeriodKey, ct);
        Assert.Equal(expected, Keys(inPeriod[doc.TableInstanceId]));
        Assert.Equal(
            Keys(batch[other.TableInstanceId]),
            Keys(inPeriod[other.TableInstanceId]));

        // Порожня таблиця — порожній зріз, не виняток і не чужі комірки.
        var empty = await builder.BuildAsync(periodKey: 202605, columnCount: 2, rowCount: 1, ct: ct);
        Assert.Empty(await store.ReadSliceAsync(empty.TableInstanceId, empty.PeriodKey, ct));
        Assert.False((await store.ReadSlicesAsync([empty.TableInstanceId], empty.PeriodKey, ct)).ContainsKey(empty.TableInstanceId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порядок_комірок_зрізу_той_самий_що_в_матеріалізації_EF()
    {
        var ct = CancellationToken.None;
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202606, columnCount: 5, rowCount: 6, ct: ct);

        await using var db = builder.CreateContext();
        var store = new NormalizedCellStore(db);

        // Запис у «розкиданому» порядку: порядок результату визначає SQL, а не порядок запису.
        var records = doc.RowIds
            .SelectMany(row => doc.ColumnDefIds.Skip(1).Select(column => (row, column)))
            .OrderByDescending(p => (p.row * 31 + p.column) % 7)
            .Select(p => new CellRecord(
                new CellAddress(doc.PeriodKey, p.row, p.column), doc.TableDefId,
                new CellValueData { ValueNumeric = p.row + (p.column / 100m) }))
            .ToList();
        await store.ApplyAsync(new CellChangeSet(doc.TableInstanceId, records, [], doc.RowIds, 1, false), ct);

        // Еталон — колишнє читання: той самий LINQ, матеріалізований EF.
        var rowIds = doc.RowIds.ToList();
        var reference = await (
                from instance in db.TableInstances.AsNoTracking()
                where instance.PeriodKeyValue == doc.PeriodKey.Value && new[] { doc.TableInstanceId }.Contains(instance.Id)
                join row in db.TableRows.AsNoTracking()
                    on new { P = instance.PeriodKeyValue, I = instance.Id } equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
                join cell in db.CellValues.AsNoTracking()
                    on new { P = row.PeriodKeyValue, R = row.Id } equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
                where !row.IsDeleted
                select new { cell.TableRowId, cell.ColumnDefId })
            .ToListAsync(ct);

        var slice = await store.ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, ct);

        Assert.Equal(records.Count, slice.Count);
        Assert.Equal(
            reference.Select(x => (x.TableRowId, x.ColumnDefId)),
            slice.Select(x => (x.Address.TableRowId, x.Address.ColumnDefId)));
    }

    /// <summary>Незалежний еталон: сутності <c>doc.CellValue</c> через EF, у сталому порядку.</summary>
    private static async Task<List<string>> ExpectedAsync(
        EcrDbContext db, PeriodKey period, IReadOnlyList<long> rowIds, CancellationToken ct)
        => [.. (await db.CellValues.AsNoTracking()
                .Where(v => v.PeriodKeyValue == period.Value && rowIds.Contains(v.TableRowId))
                .ToListAsync(ct))
            .Select(v => Key(
                v.PeriodKeyValue, v.TableRowId, v.ColumnDefId, v.TableDefId, v.ValueString, v.ValueNumeric, v.ValueDate,
                v.ValueBool, v.ValueRegistryEntryId, v.ValueUnitId, v.IsCalculated, v.IsEmpty))
            .Order(StringComparer.Ordinal)];

    private static List<string> Keys(IEnumerable<CellRecord> records)
        => [.. records
            .Select(x => Key(
                x.Address.PeriodKey.Value, x.Address.TableRowId, x.Address.ColumnDefId, x.TableDefId,
                x.Value.ValueString, x.Value.ValueNumeric, x.Value.ValueDate, x.Value.ValueBool,
                x.Value.ValueRegistryEntryId, x.Value.ValueUnitId, x.Value.IsCalculated, x.Value.IsEmpty))
            .Order(StringComparer.Ordinal)];

    /// <summary>Текст комірки: масштаб числа, тики й вид дати, <c>null</c> окремо від порожнього рядка.</summary>
    private static string Key(
        int period, long rowId, int columnId, int tableDefId, string? text, decimal? number, DateTime? date,
        bool? flag, long? entry, int? unit, bool calculated, bool empty)
        => string.Join(
            '|',
            period.ToString(CultureInfo.InvariantCulture),
            rowId.ToString(CultureInfo.InvariantCulture),
            columnId.ToString(CultureInfo.InvariantCulture),
            tableDefId.ToString(CultureInfo.InvariantCulture),
            text is null ? "S∅" : $"S'{text}'",
            number is null ? "N∅" : "N" + number.Value.ToString(CultureInfo.InvariantCulture),
            date is null ? "D∅" : FormattableString.Invariant($"D{date.Value.Ticks}:{date.Value.Kind}"),
            flag is null ? "B∅" : flag.Value ? "B1" : "B0",
            entry is null ? "R∅" : "R" + entry.Value.ToString(CultureInfo.InvariantCulture),
            unit is null ? "U∅" : "U" + unit.Value.ToString(CultureInfo.InvariantCulture),
            calculated ? "calc1" : "calc0",
            empty ? "empty1" : "empty0");

    private static decimal Number(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>Запис довідника (FK <c>FK_CellValue_Entry</c>) і наявна одиниця (FK <c>FK_CellValue_Unit</c>).</summary>
    private async Task<(long EntryId, int UnitId)> RegistryEntryAndUnitAsync(TestDocumentBuilder builder, CancellationToken ct)
    {
        await using var db = builder.CreateContext();
        var registry = new RegistryDef(
            EcrCode.Create($"FID_{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Slice fidelity" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync(ct);

        var entry = new RegistryEntry(
            registry.Id, EcrCode.Create("E1"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Entry" }));
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("SELECT TOP (1) Id FROM uom.Unit ORDER BY Id;", connection);
        var unit = await command.ExecuteScalarAsync(ct);
        Assert.NotNull(unit);

        return (entry.Id, Convert.ToInt32(unit, CultureInfo.InvariantCulture));
    }
}
