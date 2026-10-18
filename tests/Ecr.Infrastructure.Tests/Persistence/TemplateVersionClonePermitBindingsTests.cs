// tests/Ecr.Infrastructure.Tests/Persistence/TemplateVersionClonePermitBindingsTests.cs
using Ecr.Application.Permits.Bindings;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// «Clone version» переносить прив'язки аркуша до дозволів (PM-09): таблиця, колонки й поле шапки перев'язуються на
/// КЛОН, код отримує суфікс <c>_v&lt;id версії&gt;</c> (<c>UQ_PermitSheetBinding_Code</c> - по всій системі), режим,
/// правило, активність і порядок колонок зберігаються, джерело не зачеплене.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>ClonePermitBindings</c> із <c>SaveCloneAsync</c> - червоні всі тести (прив'язок у клоні
/// немає); підставити <c>t.Code</c> замість <c>ClonedRelationCode</c> - червоний на <c>UQ_PermitSheetBinding_Code</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class TemplateVersionClonePermitBindingsTests(SqlServerFixture sql)
{
    private static int _versionSeed = 100;

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_переносить_прив_язку_колонки_і_налаштування_на_таблиці_та_колонки_клону()
    {
        var (sheet, typeId, tableId) = await ArrangeAsync(isActive: false);

        var cloneId = await CloneAsync(sheet.Doc.TemplateVersionId);

        await using var read = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var clone = await read.PermitSheetBindings.AsNoTracking().Include(b => b.Columns)
            .SingleAsync(b => b.TemplateVersionId == cloneId);
        var cloneTable = await TableOfAsync(read, cloneId);
        var cloneColumns = await read.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == cloneTable.Id).ToDictionaryAsync(c => c.Code, c => c.Id);
        var sourceColumns = await read.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == sheet.Doc.TableDefId).ToDictionaryAsync(c => c.Id, c => c.Code);

        Assert.Equal($"B{_tag}_v{cloneId}", clone.Code);
        Assert.Equal(cloneTable.Id, clone.SheetTableDefId);
        Assert.NotEqual(sheet.Doc.TableDefId, clone.SheetTableDefId);
        Assert.Equal((typeId, tableId), (clone.PermitTypeDefId, clone.PermitTypeTableId));
        Assert.Equal((PermitBindingMode.Rows, PermitSelectionRule.ByFacilityPeriodEnd, false), (clone.Mode, clone.SelectionRule, clone.IsActive));
        Assert.Null(clone.HeaderFieldDefId);

        // Колонки: ті самі ролі, поля й порядок, але на колонки КЛОНУ (за кодом колонки джерела).
        Assert.Equal(
            [(PermitColumnRole.RowKey, "Substance", cloneColumns[sourceColumns[sheet.Doc.ColumnDefIds[0]]], 1),
             (PermitColumnRole.Limit, "Limit", cloneColumns[sourceColumns[sheet.Doc.ColumnDefIds[1]]], 2),
             (PermitColumnRole.Info, "Note", cloneColumns[sourceColumns[sheet.NoteColumnId]], 3)],
            clone.Columns.OrderBy(c => c.Ordinal).Select(c => (c.Role, c.PermitFieldCode, c.SheetColumnDefId, c.Ordinal)));

        // Джерело не зачеплене.
        var original = await read.PermitSheetBindings.AsNoTracking().Include(b => b.Columns)
            .SingleAsync(b => b.TemplateVersionId == sheet.Doc.TemplateVersionId);
        Assert.Equal(($"B{_tag}", sheet.Doc.TableDefId, 3, false), (original.Code, original.SheetTableDefId, original.Columns.Count, original.IsActive));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_перев_язує_поле_шапки_правила_з_поля_шапки_і_переносить_режим_формул_без_колонок()
    {
        var (sheet, _, _) = await ArrangeAsync(fromHeader: true, formulas: true);

        var cloneId = await CloneAsync(sheet.Doc.TemplateVersionId);

        await using var read = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var clone = await read.PermitSheetBindings.AsNoTracking().Include(b => b.Columns)
            .SingleAsync(b => b.TemplateVersionId == cloneId);
        var cloneHeader = await read.HeaderFieldDefs.AsNoTracking().SingleAsync(h => h.TemplateVersionId == cloneId && h.Code == "PermitRef");
        var sourceHeader = await read.HeaderFieldDefs.AsNoTracking().SingleAsync(h => h.TemplateVersionId == sheet.Doc.TemplateVersionId && h.Code == "PermitRef");

        Assert.Equal((PermitBindingMode.Formulas, PermitSelectionRule.FromHeaderLookup), (clone.Mode, clone.SelectionRule));
        Assert.Equal(cloneHeader.Id, clone.HeaderFieldDefId);
        Assert.NotEqual(sourceHeader.Id, clone.HeaderFieldDefId);
        Assert.Empty(clone.Columns);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_клона_не_накопичує_суфікс_і_версія_без_прив_язок_клонується_як_раніше()
    {
        var (sheet, _, _) = await ArrangeAsync();

        var first = await CloneAsync(sheet.Doc.TemplateVersionId);
        var second = await CloneAsync(first);

        await using var read = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var codes = await read.PermitSheetBindings.AsNoTracking().Where(b => b.Code.StartsWith($"B{_tag}")).Select(b => b.Code).ToListAsync();
        Assert.Equal(3, codes.Count);
        Assert.Contains($"B{_tag}_v{second}", codes);
        Assert.DoesNotContain(codes, c => c.Contains($"_v{first}_v", StringComparison.Ordinal));

        var plain = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: default);
        var plainClone = await CloneAsync(plain.TemplateVersionId);
        Assert.False(await read.PermitSheetBindings.AnyAsync(b => b.TemplateVersionId == plainClone));
    }

    private async Task<int> CloneAsync(int sourceVersionId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await new TemplateVersionStore(db).CloneAsync(
            sourceVersionId, $"8.{Interlocked.Increment(ref _versionSeed)}.0.1", 1, new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc), default);
    }

    private static async Task<TableDef> TableOfAsync(EcrDbContext db, int versionId)
        => await db.TableDefs.AsNoTracking()
            .SingleAsync(t => db.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == versionId));

    private async Task<(PreparedSheet Sheet, int TypeId, int TableId)> ArrangeAsync(
        bool isActive = true, bool fromHeader = false, bool formulas = false)
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var typeCode = $"AIR{_tag}";
        await h.PublishEmissionsTypeAsync(typeCode);

        int? headerFieldId = null;
        if (fromHeader)
        {
            var type = (await h.Types.Permits.FindByCodeAsync(typeCode, default))!;
            await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
            var field = new HeaderFieldDef(sheet.Doc.TemplateVersionId, EcrCode.Create("PermitRef"), PermitHarness.Text("Permit"), 2, CellDataType.Lookup);
            field.SetLookup(type.HeaderRegistryDefId);
            db.HeaderFieldDefs.Add(field);
            await db.SaveChangesAsync();
            headerFieldId = field.Id;
        }

        await using var save = await h.NextRequestAsync();
        await save.Save.HandleAsync(
            sheet.Doc.TemplateVersionId,
            $"B{_tag}",
            new SavePermitBindingRequest(
                sheet.Doc.TableDefId, typeCode, "Emissions", formulas ? "Formulas" : "Rows",
                fromHeader ? "FromHeaderLookup" : "ByFacilityPeriodEnd", headerFieldId, isActive,
                formulas
                    ? []
                    :
                    [
                        new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                        new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
                        new PermitBindingColumnRequest("Info", "Note", sheet.NoteColumnId),
                    ]),
            default);

        await using var read = await h.NextRequestAsync();
        var typeRow = (await read.Types.Permits.FindByCodeAsync(typeCode, default))!;
        var table = (await read.Types.Permits.ListTablesAsync(typeRow.Id, default)).Single(t => t.Code == "Emissions");
        return (sheet, typeRow.Id, table.Id);
    }
}
