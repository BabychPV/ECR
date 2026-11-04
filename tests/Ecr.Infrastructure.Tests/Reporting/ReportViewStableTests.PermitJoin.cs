// tests/Ecr.Infrastructure.Tests/Reporting/ReportViewStableTests.PermitJoin.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Reporting;

/// <summary>
/// PERMUSE: звіт «факт проти ліміту» за дозволом, НАЕ та речовиною. Колонка <c>Lookup</c> стабільної вʼюхи таблиці документа
/// віддає ще й <c>&lt;Код&gt;_Id</c>, а вʼюхи даних дозволу (<c>rpt.v_Permit</c>, <c>rpt.v_PermitAirSource</c>,
/// <c>rpt.v_PermitAirSourceSubstance</c>) дають ліміти з тими самими ідентифікаторами записів довідників.
/// </summary>
/// <remarks>
/// Мутації (докази): (1) у <c>rpt.v_PermitAirSourceSubstance</c> прибрати <c>PermitId</c> із приєднання в тесті (або взяти ліміт без
/// дозволу) - ряд дозволу P2 підтягує ліміт P1 і сума/порівняння розходяться; (2) у <c>usp_GenerateStableViews</c> прибрати блок
/// <c>@hasId</c> - зникають колонки <c>*_Id</c> і запит не збирається.
/// </remarks>
public sealed partial class ReportViewStableTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Факт_документа_поєднується_з_лімітом_дозволу_за_дозволом_НАЕ_і_речовиною()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var air = await EnsureAirLimitsAsync(tag);

        // Довідники НАЕ й речовин + записи; два дозволи з різними лімітами для ТІЄЇ САМОЇ пари НАЕ/речовина.
        long p1, p2, nae1, nae2, s301, s337;
        await using (var db = sql.CreateContext())
        {
            var p1Entry = NewEntry(db, air.HeaderDef, $"P1{tag}", validFrom: new DateOnly(2026, 1, 1), validTo: new DateOnly(2027, 1, 1));
            var p2Entry = NewEntry(db, air.HeaderDef, $"P2{tag}");
            var n1 = NewEntry(db, air.NaeDef, $"N1{tag}");
            var n2 = NewEntry(db, air.NaeDef, $"N2{tag}");
            var sub1 = NewEntry(db, air.SubstanceDef, $"S301{tag}");
            var sub2 = NewEntry(db, air.SubstanceDef, $"S337{tag}");
            await db.SaveChangesAsync();

            SetValue(db, p1Entry, air.Field["H:Permit_Number"], CellDataType.String, $"№ P1 {tag}");
            SetValue(db, p1Entry, air.Field["H:Location"], CellDataType.String, "Tengiz");
            SetValue(db, p2Entry, air.Field["H:Permit_Number"], CellDataType.String, $"№ P2 {tag}");
            SetValue(db, n1, air.NaeCodeField, CellDataType.String, $"0001-{tag}");
            SetValue(db, n2, air.NaeCodeField, CellDataType.String, $"0002-{tag}");
            SetValue(db, sub1, air.SubstanceCodeField, CellDataType.String, "301");
            SetValue(db, sub2, air.SubstanceCodeField, CellDataType.String, "337");
            await db.SaveChangesAsync();
            (p1, p2, nae1, nae2, s301, s337) = (p1Entry.Id, p2Entry.Id, n1.Id, n2.Id, sub1.Id, sub2.Id);

            // Джерела (AIR_SOURCES): P1 має НАЕ1 і НАЕ2, P2 - лише НАЕ1.
            var a = await AddSourceAsync(db, air, p1, nae1, $"A{tag}");
            var b = await AddSourceAsync(db, air, p1, nae2, $"B{tag}");
            var c = await AddSourceAsync(db, air, p2, nae1, $"C{tag}");

            // Речовини джерел (ліміти, т/рік).
            await AddSubstanceAsync(db, air, p1, a, s301, 10m, $"U1{tag}");
            await AddSubstanceAsync(db, air, p1, a, s337, 5m, $"U2{tag}");
            await AddSubstanceAsync(db, air, p1, b, s301, 7m, $"U3{tag}");
            await AddSubstanceAsync(db, air, p2, c, s301, 99m, $"U4{tag}");
        }

        // Документ з таблицею «факт»: Lookup на дозвіл, НАЕ й речовину + число.
        var (templateId, tpl) = await NewTemplateAsync();
        var fact = await AddLookupVersionAsync(templateId, "1", air.HeaderDef, air.NaeDef, air.SubstanceDef);
        var project = await NewProjectAsync(fact.VersionId, 202609);
        await SeedAsync(
            project, 202609, fact,
            ("R1", 1, new() { ["TONS"] = 4m }), ("R2", 2, new() { ["TONS"] = 6m }),
            ("R3", 3, new() { ["TONS"] = 7m }), ("R4", 4, new() { ["TONS"] = 100m }));
        await InsertLookupCellsAsync(
            project, fact, 202609,
            ("R1", p1, nae1, s301), ("R2", p1, nae1, s337), ("R3", p1, nae2, s301), ("R4", p2, nae1, s301));

        await GenerateAsync(fact.VersionId);
        var stable = StableName(tpl, "S", "T");

        // Lookup-колонки віддають і значення, і явний <Код>_Id (то те саме Id запису); службові колонки на місці.
        Assert.Equal(
            ["PERMIT", "NAE", "SUBSTANCE", "TONS", "PERMIT_Id", "NAE_Id", "SUBSTANCE_Id"],
            await UserColumnsAsync(stable));
        var ids = await RowsAsync(stable, project.ProjectId, "_RowKey", "PERMIT", "PERMIT_Id", "SUBSTANCE_Id");
        Assert.All(ids, r => Assert.Equal(r[1], r[2]));
        Assert.Equal(p1, Convert.ToInt64(ids[0][2], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(s337, Convert.ToInt64(ids[1][3], System.Globalization.CultureInfo.InvariantCulture));

        // Звіт «факт проти ліміту»: приєднання за дозволом + НАЕ + речовиною; номер дозволу з rpt.v_Permit.
        var report = (await ScalarRowsAsync(
                $"""
                SELECT p.PermitNumber, s.NaeCode, s.SubstanceCode, a.TONS AS Actual, s.TonsPerYear AS Limit
                  FROM rpt.[{stable}] AS a
                  JOIN rpt.v_PermitAirSourceSubstance AS s
                    ON s.PermitId = a.PERMIT_Id AND s.NaeId = a.NAE_Id AND s.SubstanceId = a.SUBSTANCE_Id
                  JOIN rpt.v_Permit AS p ON p.PermitId = s.PermitId
                 WHERE a._ProjectId = {project.ProjectId}
                 ORDER BY a._RowKey
                """))
            .Select(r => ((string)r[0]!, (string)r[1]!, (string)r[2]!, (decimal)r[3]!, (decimal)r[4]!))
            .ToList();

        Assert.Equal(
            [
                ($"№ P1 {tag}", $"0001-{tag}", "301", 4m, 10m),
                ($"№ P1 {tag}", $"0001-{tag}", "337", 6m, 5m),      // перевищення: 6 > 5
                ($"№ P1 {tag}", $"0002-{tag}", "301", 7m, 7m),
                ($"№ P2 {tag}", $"0001-{tag}", "301", 100m, 99m),   // той самий НАЕ/речовина, ІНШИЙ дозвіл - свій ліміт (99, не 10)
            ],
            report);

        // Шапка дозволу у вʼюсі: поля, півінтервал чинності, розташування.
        var header = (await ScalarRowsAsync(
            $"SELECT TypeCode, PermitNumber, Location, ValidFrom, ValidTo FROM rpt.v_Permit WHERE PermitId = {p1}"))[0];
        Assert.Equal("AIR", (string)header[0]!);
        Assert.Equal(($"№ P1 {tag}", "Tengiz"), ((string)header[1]!, (string)header[2]!));
        Assert.Equal((new DateTime(2026, 1, 1), new DateTime(2027, 1, 1)), ((DateTime)header[3]!, (DateTime)header[4]!));

        // Джерела: НАЕ дозволу і лічильник рядків (3 джерела, 4 речовини серед наших дозволів).
        Assert.Equal(
            3,
            (int)(await ScalarRowsAsync($"SELECT COUNT(*) FROM rpt.v_PermitAirSource WHERE PermitId IN ({p1}, {p2})"))[0][0]!);
        Assert.Equal(
            4,
            (int)(await ScalarRowsAsync($"SELECT COUNT(*) FROM rpt.v_PermitAirSourceSubstance WHERE PermitId IN ({p1}, {p2})"))[0][0]!);

        // Ідемпотентність: повторна генерація нічого не міняє.
        Assert.All(await ExecStableAsync(fact.VersionId), r => Assert.Equal("Unchanged", r.Action));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "PERMUSE")]
    public async Task Колонка_Id_не_створюється_удруге_коли_шаблон_уже_має_колонку_з_таким_іменем()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        int lookupDef;
        await using (var db = sql.CreateContext())
        {
            var def = new RegistryDef(EcrCode.Create($"LK_{tag}"), Name("Lookup target"), isTemporal: false);
            db.RegistryDefs.Add(def);
            await db.SaveChangesAsync();
            lookupDef = def.Id;
        }

        var (templateId, tpl) = await NewTemplateAsync();
        int versionId;
        await using (var db = sql.CreateContext())
        {
            var version = new TemplateVersion(templateId, "1", 1, Now);
            db.TemplateVersions.Add(version);
            await db.SaveChangesAsync();
            var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
            db.SheetDefs.Add(sheet);
            await db.SaveChangesAsync();
            var table = new TableDef(sheet.Id, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync();
            var lookup = new ColumnDef(table.Id, EcrCode.Create("OWNER"), Name("OWNER"), 1, CellDataType.Lookup);
            lookup.SetLookup(lookupDef);
            db.ColumnDefs.AddRange(lookup, new ColumnDef(table.Id, EcrCode.Create("OWNER_Id"), Name("OWNER_Id"), 2, CellDataType.String));
            await db.SaveChangesAsync();
            version.Publish(1, Now);
            await db.SaveChangesAsync();
            versionId = version.Id;
        }

        await GenerateAsync(versionId);
        var columns = await ColumnNamesAsync(StableName(tpl, "S", "T"));

        Assert.Equal(["OWNER", "OWNER_Id"], columns.Where(name => !name.StartsWith('_')).ToList());
        var types = await ColumnTypesAsync(StableName(tpl, "S", "T"));
        Assert.Equal("nvarchar", types["OWNER_Id"]);   // власна колонка шаблону, а не супутник
    }

    // ── помічники ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record AirKit(
        int HeaderDef, int SourcesDef, int SubstancesDef, int NaeDef, int SubstanceDef,
        int NaeCodeField, int SubstanceCodeField, Dictionary<string, int> Field);

    /// <summary>Тип AIR (✎ AIR-ON-AIR: таблиці джерел перенесено з AIR_LIMITS) і його реєстри: беремо наявні або створюємо.</summary>
    private async Task<AirKit> EnsureAirLimitsAsync(string tag)
    {
        await using var db = sql.CreateContext();
        var type = await db.PermitTypeDefs.SingleOrDefaultAsync(t => t.Code == "AIR");
        int header, sources, substances;
        if (type is null)
        {
            var h = new RegistryDef(EcrCode.Create("PRM_AIR"), Name("Air limits"), isTemporal: true);
            var s = new RegistryDef(EcrCode.Create("PRM_AIR_AIR_SOURCES"), Name("Sources"), isTemporal: false);
            var u = new RegistryDef(EcrCode.Create("PRM_AIR_AIR_SOURCE_SUBSTANCES"), Name("Substances"), isTemporal: false);
            db.RegistryDefs.AddRange(h, s, u);
            await db.SaveChangesAsync();
            type = new PermitTypeDef(EcrCode.Create("AIR"), Name("Air limits"), PermitCategory.Air, h.Id);
            db.PermitTypeDefs.Add(type);
            await db.SaveChangesAsync();
            db.PermitTypeTables.AddRange(
                new PermitTypeTable(type.Id, EcrCode.Create("AIR_SOURCES"), Name("Sources"), 1, s.Id),
                new PermitTypeTable(type.Id, EcrCode.Create("AIR_SOURCE_SUBSTANCES"), Name("Substances"), 2, u.Id));
            type.Publish(1, DateTime.UtcNow);
            await db.SaveChangesAsync();
            (header, sources, substances) = (h.Id, s.Id, u.Id);
        }
        else
        {
            var tables = await db.PermitTypeTables.Where(t => t.PermitTypeDefId == type.Id).ToListAsync();
            (header, sources, substances) = (
                type.HeaderRegistryDefId,
                tables.Single(t => t.Code == "AIR_SOURCES").RegistryDefId,
                tables.Single(t => t.Code == "AIR_SOURCE_SUBSTANCES").RegistryDefId);
            if (type.Status != PermitTypeStatus.Published)
            {
                type.Publish(1, DateTime.UtcNow);
                await db.SaveChangesAsync();
            }
        }

        // НАЕ й речовини - власні довідники прогону (поле з кодом NaeCode / SUBSTANCE_CODE, як у промислових довідниках).
        var nae = new RegistryDef(EcrCode.Create($"NAE_{tag}"), Name("NAE"), isTemporal: false);
        var substance = new RegistryDef(EcrCode.Create($"SUB_{tag}"), Name("Substances"), isTemporal: false);
        db.RegistryDefs.AddRange(nae, substance);
        await db.SaveChangesAsync();

        var fields = new Dictionary<string, int>(StringComparer.Ordinal);
        async Task<int> FieldAsync(int def, string code, CellDataType dataType, int? target = null, bool compose = false)
        {
            var existing = await db.RegistryFieldDefs.FirstOrDefaultAsync(f => f.RegistryDefId == def && f.Code == code);
            if (existing is not null)
            {
                return existing.Id;
            }

            var field = new RegistryFieldDef(def, EcrCode.Create(code), Name(code), dataType, 1);
            if (target is { } t)
            {
                field.PointTo(t);
            }

            if (compose)
            {
                field.ComposeInto(ParentDeletePolicy.Cascade);
            }

            db.RegistryFieldDefs.Add(field);
            await db.SaveChangesAsync();
            return field.Id;
        }

        fields["H:Permit_Number"] = await FieldAsync(header, "Permit_Number", CellDataType.String);
        fields["H:Location"] = await FieldAsync(header, "Location", CellDataType.String);
        fields["S:PERMIT"] = await FieldAsync(sources, "PERMIT", CellDataType.Lookup, header, compose: true);
        fields["S:NAE"] = await FieldAsync(sources, "NAE", CellDataType.Lookup, nae.Id);
        fields["S:SOURCE_NAME"] = await FieldAsync(sources, "SOURCE_NAME", CellDataType.String);
        fields["U:PERMIT"] = await FieldAsync(substances, "PERMIT", CellDataType.Lookup, header, compose: true);
        fields["U:SOURCE"] = await FieldAsync(substances, "SOURCE", CellDataType.Lookup, sources);
        fields["U:SUBSTANCE"] = await FieldAsync(substances, "SUBSTANCE", CellDataType.Lookup, substance.Id);
        fields["U:TONS_PER_YEAR"] = await FieldAsync(substances, "TONS_PER_YEAR", CellDataType.Decimal);
        var naeCode = await FieldAsync(nae.Id, "NaeCode", CellDataType.String);
        var substanceCode = await FieldAsync(substance.Id, "SUBSTANCE_CODE", CellDataType.String);

        return new AirKit(header, sources, substances, nae.Id, substance.Id, naeCode, substanceCode, fields);
    }

    private static RegistryEntry NewEntry(EcrDbContext db, int defId, string code, DateOnly? validFrom = null, DateOnly? validTo = null)
    {
        var entry = new RegistryEntry(defId, EcrCode.Create(code), Name(code), 1, Now);
        if (validFrom is not null || validTo is not null)
        {
            entry.SetValidity(validFrom, validTo);
        }

        db.RegistryEntries.Add(entry);
        return entry;
    }

    private static void SetValue(EcrDbContext db, RegistryEntry entry, int fieldId, CellDataType type, object? value)
    {
        var v = new RegistryValue(entry, fieldId);
        v.Set(type, value, null);
        db.RegistryValues.Add(v);
    }

    private static async Task<long> AddSourceAsync(EcrDbContext db, AirKit air, long permit, long nae, string code)
    {
        var row = NewEntry(db, air.SourcesDef, code);
        await db.SaveChangesAsync();
        SetValue(db, row, air.Field["S:PERMIT"], CellDataType.Lookup, permit);
        SetValue(db, row, air.Field["S:NAE"], CellDataType.Lookup, nae);
        SetValue(db, row, air.Field["S:SOURCE_NAME"], CellDataType.String, code);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task AddSubstanceAsync(EcrDbContext db, AirKit air, long permit, long source, long substance, decimal tons, string code)
    {
        var row = NewEntry(db, air.SubstancesDef, code);
        await db.SaveChangesAsync();
        SetValue(db, row, air.Field["U:PERMIT"], CellDataType.Lookup, permit);
        SetValue(db, row, air.Field["U:SOURCE"], CellDataType.Lookup, source);
        SetValue(db, row, air.Field["U:SUBSTANCE"], CellDataType.Lookup, substance);
        SetValue(db, row, air.Field["U:TONS_PER_YEAR"], CellDataType.Decimal, tons);
        await db.SaveChangesAsync();
    }

    /// <summary>Версія шаблону з таблицею S/T: Lookup на дозвіл, НАЕ й речовину + число TONS.</summary>
    private async Task<BuiltTable> AddLookupVersionAsync(int templateId, string versionText, int permitDef, int naeDef, int substanceDef)
    {
        await using var db = sql.CreateContext();
        var version = new TemplateVersion(templateId, versionText, 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();
        var sheet = new SheetDef(version.Id, EcrCode.Create("S"), Name("S"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();
        var table = new TableDef(sheet.Id, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        ColumnDef Lookup(string code, int ordinal, int target)
        {
            var column = new ColumnDef(table.Id, EcrCode.Create(code), Name(code), ordinal, CellDataType.Lookup);
            column.SetLookup(target);
            return column;
        }

        var columns = new List<ColumnDef>
        {
            Lookup("PERMIT", 1, permitDef),
            Lookup("NAE", 2, naeDef),
            Lookup("SUBSTANCE", 3, substanceDef),
            new(table.Id, EcrCode.Create("TONS"), Name("TONS"), 4, CellDataType.Decimal),
        };
        db.ColumnDefs.AddRange(columns);
        await db.SaveChangesAsync();
        version.Publish(1, Now);
        await db.SaveChangesAsync();

        return new BuiltTable(version.Id, sheet.Id, table.Id, columns.ToDictionary(c => c.Code, c => c.Id, StringComparer.Ordinal));
    }

    /// <summary>Посилання Lookup у комірках рядків (рядки вже засіяно <c>SeedAsync</c>).</summary>
    private async Task InsertLookupCellsAsync(
        Project project, BuiltTable table, int periodKey, params (string RowKey, long Permit, long Nae, long Substance)[] rows)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        foreach (var (rowKey, permit, nae, substance) in rows)
        {
            long rowId;
            await using (var find = connection.CreateCommand())
            {
                find.CommandText =
                    "SELECT r.Id FROM doc.TableRow AS r JOIN doc.TableInstance AS i ON i.PeriodKey = r.PeriodKey AND i.Id = r.TableInstanceId "
                    + "WHERE r.PeriodKey = @p AND i.DocumentId = @d AND i.TableDefId = @t AND r.RowKey = @k";
                find.Parameters.AddWithValue("@p", periodKey);
                find.Parameters.AddWithValue("@d", project.DocumentId);
                find.Parameters.AddWithValue("@t", table.TableId);
                find.Parameters.AddWithValue("@k", rowKey);
                rowId = Convert.ToInt64(await find.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            }

            foreach (var (column, entryId) in new[] { ("PERMIT", permit), ("NAE", nae), ("SUBSTANCE", substance) })
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueRegistryEntryId) "
                    + "VALUES (@p, @r, @c, @t, @e)";
                insert.Parameters.AddWithValue("@p", periodKey);
                insert.Parameters.AddWithValue("@r", rowId);
                insert.Parameters.AddWithValue("@c", table.Cols[column]);
                insert.Parameters.AddWithValue("@t", table.TableId);
                insert.Parameters.AddWithValue("@e", entryId);
                await insert.ExecuteNonQueryAsync();
            }
        }
    }
}
