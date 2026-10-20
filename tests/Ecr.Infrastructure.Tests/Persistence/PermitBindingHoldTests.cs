// tests/Ecr.Infrastructure.Tests/Persistence/PermitBindingHoldTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Що таке «прив'язка є» для охоронця правки структури (PM-09b2, PM-11c): охоронець видалення таблиці, аркуша й колонки
/// рахує і ЧИННІ, і ВИМКНЕНІ прив'язки (вимкнена теж лишила б мертву ціль), тоді як прапор знімка метаданих
/// <c>HasActivePermitBindings</c> та екран документа - лише чинні. Охоронець видалення питає прив'язки ОДНИМ запитом.
/// </summary>
/// <remarks>
/// Мутаційні докази (запущені): у <c>PermitBindingStore.FindHoldAsync</c> додати <c>b.IsActive</c> - червоніють обидва
/// тести про вимкнену прив'язку; у <c>FindHoldAsync</c> прибрати ранній вихід для порожнього переліку - червоніє
/// «Порожній_перелік…» (один запит замість нуля).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitBindingHoldTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пошук_тримача_робить_один_запит_і_нуль_для_порожнього_переліку_а_вимкнена_прив_язка_теж_тримає()
    {
        var free = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        var off = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await BindAsync(off, isActive: false);

        var counter = new DbCommandCounter();
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).AddInterceptors(counter).Options);
        var store = new PermitBindingStore(db);

        counter.Tally.Reset();
        var none = await store.FindHoldAsync([free.Doc.TableDefId], default);
        var noneQueries = counter.Tally.Snapshot().Total;

        counter.Tally.Reset();
        var hold = await store.FindHoldAsync([free.Doc.TableDefId, off.Doc.TableDefId], default);
        var holdQueries = counter.Tally.Snapshot().Total;

        counter.Tally.Reset();
        var empty = await store.FindHoldAsync([], default);
        var emptyQueries = counter.Tally.Snapshot().Total;

        Assert.Null(none);
        Assert.Equal((off.Doc.TableDefId, $"B{_tag}"), (hold!.SheetTableDefId, hold.Code));
        Assert.Null(empty);
        Assert.Equal((1, 1, 0), (noneQueries, holdQueries, emptyQueries));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вимкнена_прив_язка_тримає_таблицю_аркуш_і_колонку_але_не_режим_рядків_і_стелю()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using (var set = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var table = await set.TableDefs.SingleAsync(t => t.Id == sheet.Doc.TableDefId);
            table.SetMaxDynamicRows(10);
            await set.SaveChangesAsync();
        }

        await BindAsync(sheet, isActive: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var tableCode = await h.Db.TableDefs.AsNoTracking().Where(t => t.Id == sheet.Doc.TableDefId).Select(t => t.Code).SingleAsync();
        var columnCode = await h.Db.ColumnDefs.AsNoTracking().Where(c => c.Id == sheet.Doc.ColumnDefIds[0]).Select(c => c.Code).SingleAsync();

        // Рантайм рядків вимкнену прив'язку не читає: режим Fixed і зниження стелі проходять (правка за цими двома - лише чинній).
        await h.SaveTable.HandleAsync(
            sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, tableCode, Command(TableRowMode.Dynamic, 5), default);
        await using var fixedMode = await h.NextRequestAsync();
        await fixedMode.SaveTable.HandleAsync(
            sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, tableCode, Command(TableRowMode.Fixed), default);

        // А ціль (таблиця, колонка, аркуш) вимкнена прив'язка тримає: їх м'яке видалення лишило б мертву ціль.
        await using var column = await h.NextRequestAsync();
        var onColumn = await Assert.ThrowsAsync<BusinessRuleException>(
            () => column.DeleteColumn.HandleAsync(sheet.Doc.TemplateVersionId, sheet.Doc.TableDefId, columnCode, default));
        await using var tableDelete = await h.NextRequestAsync();
        var onTable = await Assert.ThrowsAsync<BusinessRuleException>(
            () => tableDelete.DeleteTable.HandleAsync(sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, tableCode, default));
        await using var sheetDelete = await h.NextRequestAsync();
        var onSheet = await Assert.ThrowsAsync<BusinessRuleException>(
            () => sheetDelete.DeleteSheet.HandleAsync(sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, default));

        Assert.Equal(
            ("err.ECR-PRM-4102.column", "err.ECR-PRM-4102.table", "err.ECR-PRM-4102.sheet"),
            (onColumn.Details!["messageKey"], onTable.Details!["messageKey"], onSheet.Details!["messageKey"]));
        Assert.All([onColumn, onTable, onSheet], e => Assert.Equal($"B{_tag}", e.Details!["bindingCode"]));
    }

    private static Ecr.Application.Templates.SaveTableDefCommand Command(TableRowMode mode, int? maxDynamicRows = null)
        => new(new Dictionary<string, string> { ["en"] = "Table" }, null, TableLayoutKind.PerPeriodInstance, mode, maxDynamicRows);

    private async Task BindAsync(PreparedSheet sheet, bool isActive)
    {
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");

        await using var save = await h.NextRequestAsync();
        await save.Save.HandleAsync(
            sheet.Doc.TemplateVersionId,
            $"B{_tag}",
            new SavePermitBindingRequest(
                sheet.Doc.TableDefId, $"AIR{_tag}", "Emissions", "Rows", "ByFacilityPeriodEnd", null, isActive,
                [
                    new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                    new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
                    new PermitBindingColumnRequest("Info", "Note", sheet.NoteColumnId),
                ]),
            default);
    }
}
