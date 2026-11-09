// tests/Ecr.Infrastructure.Tests/Reporting/DocumentCellsByPermitViewTests.cs
using System.Globalization;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// (j5), human 08.11 «a permit changes mid-month: the user splits the month»: <c>rpt.v_DocumentCellsByPermit</c> credits every
/// numeric cell to its OWNING permit — a split month gives one row per part (the parts replace the cell), any other cell one
/// row credited to the permit in force on the LAST day of its month (base permit cell + <c>doc.PermitSegment</c>); a table
/// without a row permit column uses the header permit field the same way. Checked on the DEPLOYED view (live SQL Server).
/// </summary>
/// <remarks>
/// Mutations (red proofs): (1) in the view force <c>IsSplit</c> to 0 — the split month gives one row of 12 for B instead of
/// 5.5 (A) + 6.5 (B); (2) resolve the permit on the FIRST day of the month instead of the last (<c>AsOf = MonthStart</c>) — the
/// calculated May cell is credited to A instead of B.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentCellsByPermitViewTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);
    private const int Period = 202601;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "ACC-1108-j5")]
    public async Task Комірки_зараховано_дозволу_власнику_поділений_місяць_частинами_решта_дозволом_останнього_дня()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var kit = await BuildAsync(tag);
        long a = kit.A, b = kit.B;

        // T (row permit column): R1 base A, B from 15.03; March 12 split 5.5 (A) + 6.5 (B); Feb 4 (A); Apr 7 (B).
        var r1 = await RowAsync(kit, kit.T, "R1", 1);
        await CellAsync(kit.T, r1, kit.Col["PERMIT"], permit: a);
        await CellAsync(kit.T, r1, kit.Col["M02"], 4m);
        await CellAsync(kit.T, r1, kit.Col["M03"], 12m);
        await CellAsync(kit.T, r1, kit.Col["M04"], 7m);
        await SegmentAsync(kit, kit.T, "R1", new DateOnly(2026, 3, 15), b);
        await PartsAsync(kit, kit.T, "R1", kit.Col["M03"], 202603,
            (a, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14), 5.5m), (b, new DateOnly(2026, 3, 15), new DateOnly(2026, 3, 31), 6.5m));

        // R2: no history — its permit cell value.
        var r2 = await RowAsync(kit, kit.T, "R2", 2);
        await CellAsync(kit.T, r2, kit.Col["PERMIT"], permit: a);
        await CellAsync(kit.T, r2, kit.Col["M03"], 2m);

        // R3: B from 10.05; May is CALCULATED (never split) — credited to the permit in force on 31.05 (B).
        var r3 = await RowAsync(kit, kit.T, "R3", 3);
        await CellAsync(kit.T, r3, kit.Col["PERMIT"], permit: a);
        await CellAsync(kit.T, r3, kit.Col["M05"], 9m, calculated: true);
        await SegmentAsync(kit, kit.T, "R3", new DateOnly(2026, 5, 10), b);

        // R4: B from 20.04; April only partly split (part 2 has no value) — not split: the cell whole, B (in force on 30.04).
        var r4 = await RowAsync(kit, kit.T, "R4", 4);
        await CellAsync(kit.T, r4, kit.Col["PERMIT"], permit: a);
        await CellAsync(kit.T, r4, kit.Col["M04"], 10m);
        await SegmentAsync(kit, kit.T, "R4", new DateOnly(2026, 4, 20), b);
        await PartsAsync(kit, kit.T, "R4", kit.Col["M04"], 202604,
            (a, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 19), 3m), (b, new DateOnly(2026, 4, 20), new DateOnly(2026, 4, 30), null));

        // H (no row permit column): header permit A, header segment B from 10.03 — March 8 (B), February 1 (A).
        var h1 = await RowAsync(kit, kit.H, "H1", 1);
        await CellAsync(kit.H, h1, kit.Col["H.M02"], 1m);
        await CellAsync(kit.H, h1, kit.Col["H.M03"], 8m);
        await using (var db = sql.CreateContext())
        {
            db.DocumentHeaderValues.Add(new DocumentHeaderValue(kit.DocumentId, kit.HeaderField, new DocumentHeaderValueData { ValueRegistryEntryId = a }));
            db.PermitSegments.Add(PermitSegment.ForHeader(kit.DocumentId, kit.HeaderField, new DateOnly(2026, 3, 10), b, kit.UserId, Now));
            await db.SaveChangesAsync();
        }

        var rows = (await QueryAsync(
                "SELECT TableCode, RowKey, ColumnCode, PermitId, PermitSource, IsPart, PartNo, ValueNumeric, DayFrom, DayTo, PermitCode "
                + $"FROM rpt.v_DocumentCellsByPermit WHERE DocumentId = {kit.DocumentId} ORDER BY TableCode, RowKey, ColumnCode, PartNo"))
            .Select(r => (
                Table: (string)r[0]!, Row: (string)r[1]!, Col: (string)r[2]!, Permit: Convert.ToInt64(r[3], CultureInfo.InvariantCulture),
                Source: (string)r[4]!, IsPart: (bool)r[5]!, PartNo: r[6] is null ? (int?)null : Convert.ToInt32(r[6], CultureInfo.InvariantCulture),
                Value: (decimal)r[7]!))
            .ToList();

        Assert.Equal(
            [
                ("H", "H1", "M02", a, "header", false, (int?)null, 1m),
                ("H", "H1", "M03", b, "header", false, null, 8m),
                ("T", "R1", "M02", a, "row", false, null, 4m),
                ("T", "R1", "M03", a, "row", true, 1, 5.5m),
                ("T", "R1", "M03", b, "row", true, 2, 6.5m),
                ("T", "R1", "M04", b, "row", false, null, 7m),
                ("T", "R2", "M03", a, "row", false, null, 2m),
                ("T", "R3", "M05", b, "row", false, null, 9m),
                ("T", "R4", "M04", b, "row", false, null, 10m),
            ],
            rows);

        // Days of the parts and the permit code.
        var parts = await QueryAsync(
            $"SELECT DayFrom, DayTo, PermitCode FROM rpt.v_DocumentCellsByPermit WHERE DocumentId = {kit.DocumentId} AND IsPart = 1 ORDER BY PartNo");
        Assert.Equal(
            [(new DateTime(2026, 3, 1), new DateTime(2026, 3, 14), $"A{tag}"), (new DateTime(2026, 3, 15), new DateTime(2026, 3, 31), $"B{tag}")],
            parts.Select(p => ((DateTime)p[0]!, (DateTime)p[1]!, (string)p[2]!)).ToList());

        // ⛔ Never doubled: per cell, the sum of the rows = the cell value of rpt.v_DocumentCells.
        var mismatches = await QueryAsync(
            $"""
            SELECT c.TableCode, c.RowKey, c.ColumnCode
              FROM rpt.v_DocumentCells AS c
              LEFT JOIN (SELECT RowId, ColumnCode, SUM(ValueNumeric) AS Total
                           FROM rpt.v_DocumentCellsByPermit WHERE DocumentId = {kit.DocumentId}
                          GROUP BY RowId, ColumnCode) AS p ON p.RowId = c.RowId AND p.ColumnCode = c.ColumnCode
             WHERE c.DocumentId = {kit.DocumentId} AND c.DataType IN (1, 2) AND (p.Total IS NULL OR p.Total <> c.ValueNumeric)
            """);
        Assert.Empty(mismatches);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Kit(
        long DocumentId, int T, int H, Dictionary<string, int> Col, int HeaderField, long A, long B, int UserId);

    private async Task<Kit> BuildAsync(string tag)
    {
        await using var db = sql.CreateContext();

        // Permit type (published) and two permits.
        var header = new RegistryDef(EcrCode.Create($"PRM_J5{tag}"), Name("J5 permits"), isTemporal: true);
        db.RegistryDefs.Add(header);
        await db.SaveChangesAsync();
        var type = new PermitTypeDef(EcrCode.Create($"J5{tag}"), Name("J5"), PermitCategory.Air, header.Id);
        db.PermitTypeDefs.Add(type);
        await db.SaveChangesAsync();
        type.Publish(1, Now);
        var a = new RegistryEntry(header.Id, EcrCode.Create($"A{tag}"), Name("A"), 1, Now);
        var b = new RegistryEntry(header.Id, EcrCode.Create($"B{tag}"), Name("B"), 1, Now);
        db.RegistryEntries.AddRange(a, b);
        var user = new User($"j5_{tag}", "J5", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        // Template: header permit field; T — permit column + month columns (Feb–May); H — month columns only.
        var template = new Template(EcrCode.Create($"J5T{tag}"), Name("J5"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();
        var version = new TemplateVersion(template.Id, "1", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        var field = new HeaderFieldDef(version.Id, EcrCode.Create("PERMIT_H"), Name("Permit"), 1, CellDataType.Lookup);
        field.SetLookup(header.Id);
        db.HeaderFieldDefs.Add(field);
        var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var t = new TableDef(sheet.Id, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        var h = new TableDef(sheet.Id, EcrCode.Create("H"), Name("H"), 2, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.AddRange(t, h);
        await db.SaveChangesAsync();

        var cols = new Dictionary<string, ColumnDef>(StringComparer.Ordinal);
        var permit = new ColumnDef(t.Id, EcrCode.Create("PERMIT"), Name("PERMIT"), 1, CellDataType.Lookup);
        permit.SetLookup(header.Id);
        cols["PERMIT"] = permit;
        for (byte m = 2; m <= 5; m++)
        {
            cols[$"M0{m}"] = Month(t.Id, m, m);
        }

        cols["H.M02"] = Month(h.Id, 2, 1);
        cols["H.M03"] = Month(h.Id, 3, 2);
        db.ColumnDefs.AddRange(cols.Values);
        await db.SaveChangesAsync();
        version.Publish(1, Now);
        await db.SaveChangesAsync();

        // Project (monthly) with period 202601 and the document.
        var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();
        var project = new Ecr.Domain.Entities.Documents.Project(
            EcrCode.Create("PRJ" + tag), Name(tag), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, policyId, "Asia/Atyrau");
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        db.Periods.Add(new Period(project.Id, new PeriodKey(Period), 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        var document = new Document(project.Id, "DOC" + tag, 1, Now);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        return new Kit(document.Id, t.Id, h.Id, cols.ToDictionary(c => c.Key, c => c.Value.Id, StringComparer.Ordinal),
            field.Id, a.Id, b.Id, user.Id);

        static ColumnDef Month(int tableId, byte month, int ordinal)
        {
            var column = new ColumnDef(tableId, EcrCode.Create($"M0{month}"), Name($"M0{month}"), ordinal + 1, CellDataType.Decimal);
            column.SetMonth(true, month);
            return column;
        }
    }

    private readonly Dictionary<int, long> instances = [];

    /// <summary>A row of the table's instance in the period (the instance is created on first use).</summary>
    private async Task<long> RowAsync(Kit kit, int tableId, string rowKey, int ordinal)
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var key = new PeriodKey(Period);
        await using var db = sql.CreateContext();
        if (!instances.TryGetValue(tableId, out var instanceId))
        {
            instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            db.TableInstances.Add(new TableInstance(key, instanceId, kit.DocumentId, tableId, Now));
            instances[tableId] = instanceId;
        }

        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        db.TableRows.Add(new TableRow(key, rowId, instanceId, RowKey.Create(rowKey), ordinal, Now));
        await db.SaveChangesAsync();
        return rowId;
    }

    private async Task CellAsync(int tableId, long rowId, int columnId, decimal? value = null, long? permit = null, bool calculated = false)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, ValueRegistryEntryId, IsCalculated) "
            + "VALUES (@p, @r, @c, @t, @v, @e, @calc)";
        command.Parameters.AddWithValue("@p", Period);
        command.Parameters.AddWithValue("@r", rowId);
        command.Parameters.AddWithValue("@c", columnId);
        command.Parameters.AddWithValue("@t", tableId);
        command.Parameters.AddWithValue("@v", (object?)value ?? DBNull.Value);
        command.Parameters.AddWithValue("@e", (object?)permit ?? DBNull.Value);
        command.Parameters.AddWithValue("@calc", calculated);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SegmentAsync(Kit kit, int tableId, string rowKey, DateOnly from, long permit)
    {
        await using var db = sql.CreateContext();
        db.PermitSegments.Add(PermitSegment.ForRow(kit.DocumentId, tableId, null, rowKey, kit.Col["PERMIT"], from, permit, kit.UserId, Now));
        await db.SaveChangesAsync();
    }

    private async Task PartsAsync(
        Kit kit, int tableId, string rowKey, int columnId, int monthKey, params (long Permit, DateOnly From, DateOnly To, decimal? Value)[] parts)
    {
        await using var db = sql.CreateContext();
        for (var i = 0; i < parts.Length; i++)
        {
            db.CellPermitParts.Add(new CellPermitPart(
                monthKey, kit.DocumentId, tableId, null, rowKey, columnId, (byte)(i + 1), parts[i].Permit, parts[i].From, parts[i].To, parts[i].Value));
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<object?[]>> QueryAsync(string text)
    {
        var list = new List<object?[]>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var values = new object?[reader.FieldCount];
            reader.GetValues(values!);
            list.Add([.. values.Select(v => v is DBNull ? null : v)]);
        }

        return list;
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
