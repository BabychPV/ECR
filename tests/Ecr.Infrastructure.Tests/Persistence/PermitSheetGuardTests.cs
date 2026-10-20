// tests/Ecr.Infrastructure.Tests/Persistence/PermitSheetGuardTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Templates;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Правка структури аркуша, яку тримає прив'язка до дозволу (Permits, PM-09b): <c>Dynamic</c> → <c>Fixed</c> під прив'язкою
/// «рядки», видалення прив'язаної таблиці й зіставленої колонки - <c>409 ECR-PRM-4102</c> ДО запису, а не 422 при наступному
/// збереженні прив'язки.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати виклик <c>RequireRowModeAllowedAsync</c> у <c>SaveTableDefHandler</c> - червоний
/// «Dynamic_у_Fixed…»; прибрати <c>RequireTableFreeAsync</c> у <c>DeleteTableDefHandler</c> - червоний «Таблиця_з_прив_язкою…»;
/// прибрати <c>RequireColumnFreeAsync</c> у <c>DeleteColumnDefHandler</c> - червоний «Зіставлена_колонка…».
/// PM-09b2: прибрати <c>RequireMaxRowsNotLoweredAsync</c> у <c>SaveTableDefHandler</c> - червоний «Стеля_рядків…»;
/// прибрати <c>RequireSheetFreeAsync</c> у <c>DeleteSheetDefHandler</c> - червоний «Аркуш_з_прив_язаною_таблицею…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitSheetGuardTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Dynamic_у_Fixed_під_прив_язкою_рядки_дає_409_з_назвою_прив_язки_а_режим_лишається()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await BindAsync(sheet, "Rows");
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var tableCode = await TableCodeAsync(h, sheet);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => h.SaveTable.HandleAsync(sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, tableCode, Command(TableRowMode.Fixed), default));

        Assert.Equal(("ECR-PRM-4102", "err.ECR-PRM-4102.rowMode"), (error.ErrorCode, error.Details!["messageKey"]));
        Assert.Equal(($"B{_tag}", tableCode), (error.Details["bindingCode"], error.Details["tableCode"]));
        await using var read = await h.NextRequestAsync();
        Assert.Equal(TableRowMode.Dynamic, (await read.Db.TableDefs.SingleAsync(t => t.Id == sheet.Doc.TableDefId)).RowMode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_що_лишає_Dynamic_і_прив_язка_формул_і_таблиця_без_прив_язки_проходять()
    {
        var rows = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await BindAsync(rows, "Rows");
        var formulas = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await BindAsync(formulas, "Formulas", code: $"F{_tag}");
        var free = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);

        // Перейменування під прив'язкою «рядки» (режим той самий) - можна.
        var rowsTable = await TableCodeAsync(h, rows);
        await h.SaveTable.HandleAsync(rows.Doc.TemplateVersionId, rows.Doc.SheetCode, rowsTable, Command(TableRowMode.Dynamic), default);

        // Режим формул рядків не читає: Fixed не блокується.
        await using var next = await h.NextRequestAsync();
        await next.SaveTable.HandleAsync(
            formulas.Doc.TemplateVersionId, formulas.Doc.SheetCode, await TableCodeAsync(next, formulas), Command(TableRowMode.Fixed), default);

        // Таблиця без прив'язки.
        await using var other = await h.NextRequestAsync();
        await other.SaveTable.HandleAsync(
            free.Doc.TemplateVersionId, free.Doc.SheetCode, await TableCodeAsync(other, free), Command(TableRowMode.Fixed), default);

        await using var read = await h.NextRequestAsync();
        Assert.Equal(TableRowMode.Dynamic, (await read.Db.TableDefs.SingleAsync(t => t.Id == rows.Doc.TableDefId)).RowMode);
        Assert.Equal(TableRowMode.Fixed, (await read.Db.TableDefs.SingleAsync(t => t.Id == formulas.Doc.TableDefId)).RowMode);
        Assert.Equal(TableRowMode.Fixed, (await read.Db.TableDefs.SingleAsync(t => t.Id == free.Doc.TableDefId)).RowMode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таблиця_з_прив_язкою_не_видаляється_а_без_прив_язки_видаляється()
    {
        var bound = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await BindAsync(bound, "Rows");
        var free = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var tableCode = await TableCodeAsync(h, bound);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => h.DeleteTable.HandleAsync(bound.Doc.TemplateVersionId, bound.Doc.SheetCode, tableCode, default));

        Assert.Equal(("ECR-PRM-4102", "err.ECR-PRM-4102.table"), (error.ErrorCode, error.Details!["messageKey"]));
        Assert.Equal($"B{_tag}", error.Details["bindingCode"]);

        await using var other = await h.NextRequestAsync();
        await other.DeleteTable.HandleAsync(free.Doc.TemplateVersionId, free.Doc.SheetCode, await TableCodeAsync(other, free), default);

        await using var read = await h.NextRequestAsync();
        Assert.False((await read.Db.TableDefs.SingleAsync(t => t.Id == bound.Doc.TableDefId)).IsDeleted);
        Assert.True((await read.Db.TableDefs.SingleAsync(t => t.Id == free.Doc.TableDefId)).IsDeleted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зіставлена_колонка_не_видаляється_а_вільна_видаляється()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await BindAsync(sheet, "Rows");
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var mappedCode = await ColumnCodeAsync(h, sheet.Doc.ColumnDefIds[0]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => h.DeleteColumn.HandleAsync(sheet.Doc.TemplateVersionId, sheet.Doc.TableDefId, mappedCode, default));

        Assert.Equal(("ECR-PRM-4102", "err.ECR-PRM-4102.column"), (error.ErrorCode, error.Details!["messageKey"]));
        Assert.Equal(($"B{_tag}", mappedCode), (error.Details["bindingCode"], error.Details["columnCode"]));

        // C3 (ColumnDefIds[2]) прив'язка не зіставляє.
        await using var other = await h.NextRequestAsync();
        await other.DeleteColumn.HandleAsync(
            sheet.Doc.TemplateVersionId, sheet.Doc.TableDefId, await ColumnCodeAsync(other, sheet.Doc.ColumnDefIds[2]), default);

        await using var read = await h.NextRequestAsync();
        Assert.False((await read.Db.ColumnDefs.SingleAsync(c => c.Id == sheet.Doc.ColumnDefIds[0])).IsDeleted);
        Assert.True((await read.Db.ColumnDefs.SingleAsync(c => c.Id == sheet.Doc.ColumnDefIds[2])).IsDeleted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стеля_рядків_під_прив_язкою_рядки_не_знижується_а_підняття_і_зняття_проходять()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await SetMaxAsync(sheet, 10);
        await BindAsync(sheet, "Rows");
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var tableCode = await TableCodeAsync(h, sheet);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => h.SaveTable.HandleAsync(sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, tableCode, Command(TableRowMode.Dynamic, 9), default));

        Assert.Equal(("ECR-PRM-4102", "err.ECR-PRM-4102.maxRows"), (error.ErrorCode, error.Details!["messageKey"]));
        Assert.Equal(($"B{_tag}", tableCode), (error.Details["bindingCode"], error.Details["tableCode"]));
        Assert.Equal(("10", "9"), (error.Details["previousMax"], error.Details["newMax"]));
        await using var unchanged = await h.NextRequestAsync();
        Assert.Equal(10, (await unchanged.Db.TableDefs.SingleAsync(t => t.Id == sheet.Doc.TableDefId)).MaxDynamicRows);

        // Та сама стеля й підняття проходять; зняття стелі (null) - теж.
        foreach (var allowed in new int?[] { 10, 20, null })
        {
            await using var next = await h.NextRequestAsync();
            await next.SaveTable.HandleAsync(
                sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, tableCode, Command(TableRowMode.Dynamic, allowed), default);
            await using var read = await h.NextRequestAsync();
            Assert.Equal(allowed, (await read.Db.TableDefs.SingleAsync(t => t.Id == sheet.Doc.TableDefId)).MaxDynamicRows);
        }

        // «Без стелі» найбільша: з null поставити число - це зниження.
        await using var fromNone = await h.NextRequestAsync();
        var lowered = await Assert.ThrowsAsync<BusinessRuleException>(
            () => fromNone.SaveTable.HandleAsync(
                sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, tableCode, Command(TableRowMode.Dynamic, 5), default));
        Assert.Equal(("err.ECR-PRM-4102.maxRows", "none", "5"), (lowered.Details!["messageKey"], lowered.Details["previousMax"], lowered.Details["newMax"]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стелю_можна_знизити_у_таблиці_без_прив_язки_і_під_прив_язкою_формули()
    {
        var formulas = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await SetMaxAsync(formulas, 10);
        await BindAsync(formulas, "Formulas", code: $"F{_tag}");
        var free = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await SetMaxAsync(free, 10);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);

        await h.SaveTable.HandleAsync(
            formulas.Doc.TemplateVersionId, formulas.Doc.SheetCode, await TableCodeAsync(h, formulas), Command(TableRowMode.Dynamic, 3), default);
        await using var other = await h.NextRequestAsync();
        await other.SaveTable.HandleAsync(
            free.Doc.TemplateVersionId, free.Doc.SheetCode, await TableCodeAsync(other, free), Command(TableRowMode.Dynamic, 3), default);

        await using var read = await h.NextRequestAsync();
        Assert.Equal(3, (await read.Db.TableDefs.SingleAsync(t => t.Id == formulas.Doc.TableDefId)).MaxDynamicRows);
        Assert.Equal(3, (await read.Db.TableDefs.SingleAsync(t => t.Id == free.Doc.TableDefId)).MaxDynamicRows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Аркуш_з_прив_язаною_таблицею_не_видаляється_а_вільний_видаляється()
    {
        var bound = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await BindAsync(bound, "Rows");
        var free = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var tableCode = await TableCodeAsync(h, bound);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => h.DeleteSheet.HandleAsync(bound.Doc.TemplateVersionId, bound.Doc.SheetCode, default));

        Assert.Equal(("ECR-PRM-4102", "err.ECR-PRM-4102.sheet"), (error.ErrorCode, error.Details!["messageKey"]));
        Assert.Equal(($"B{_tag}", tableCode, bound.Doc.SheetCode), (error.Details["bindingCode"], error.Details["tableCode"], error.Details["sheetCode"]));

        await using var other = await h.NextRequestAsync();
        await other.DeleteSheet.HandleAsync(free.Doc.TemplateVersionId, free.Doc.SheetCode, default);

        await using var read = await h.NextRequestAsync();
        Assert.False((await read.Db.SheetDefs.SingleAsync(s => s.Id == bound.Doc.SheetDefId)).IsDeleted);
        Assert.True((await read.Db.SheetDefs.SingleAsync(s => s.Id == free.Doc.SheetDefId)).IsDeleted);
    }

    private static SaveTableDefCommand Command(TableRowMode mode, int? maxDynamicRows = null)
        => new(new Dictionary<string, string> { ["en"] = "Table" }, null, TableLayoutKind.PerPeriodInstance, mode, maxDynamicRows);

    /// <summary>Виставляє стелю рядків таблиці напряму (до прив'язки: під нею зі «без стелі» число вже не поставити).</summary>
    private async Task SetMaxAsync(PreparedSheet sheet, int max)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var table = await db.TableDefs.SingleAsync(t => t.Id == sheet.Doc.TableDefId);
        table.SetMaxDynamicRows(max);
        await db.SaveChangesAsync();
    }

    private static Task<string> TableCodeAsync(PermitBindingHarness h, PreparedSheet sheet)
        => h.Db.TableDefs.AsNoTracking().Where(t => t.Id == sheet.Doc.TableDefId).Select(t => t.Code).SingleAsync();

    private static Task<string> ColumnCodeAsync(PermitBindingHarness h, int columnId)
        => h.Db.ColumnDefs.AsNoTracking().Where(c => c.Id == columnId).Select(c => c.Code).SingleAsync();

    /// <summary>Прив'язує таблицю шаблону до <c>Emissions</c>: «рядки» - ключ, ліміт, примітка; «формули» - без колонок.</summary>
    private async Task BindAsync(PreparedSheet sheet, string mode, string? code = null)
    {
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var typeCode = $"AIR{_tag}";
        if (await h.Types.Permits.FindByCodeAsync(typeCode, default) is null)
        {
            await h.PublishEmissionsTypeAsync(typeCode);
        }

        PermitBindingColumnRequest[] columns = mode == "Rows"
            ?
            [
                new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
                new PermitBindingColumnRequest("Info", "Note", sheet.NoteColumnId),
            ]
            : [];

        await using var save = await h.NextRequestAsync();
        await save.Save.HandleAsync(
            sheet.Doc.TemplateVersionId,
            code ?? $"B{_tag}",
            new SavePermitBindingRequest(sheet.Doc.TableDefId, typeCode, "Emissions", mode, "ByFacilityPeriodEnd", null, true, columns),
            default);
    }
}
