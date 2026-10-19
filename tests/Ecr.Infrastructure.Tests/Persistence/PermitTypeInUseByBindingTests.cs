// tests/Ecr.Infrastructure.Tests/Persistence/PermitTypeInUseByBindingTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Permits.Dto;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Тип дозволу, на який посилається прив'язка аркуша, не видаляється (Permits, PM-09, аудит D7): <c>ECR-PRM-4094</c>.
/// </summary>
/// <remarks>
/// Прив'язка можлива лише до ОПУБЛІКОВАНОГО типу (<c>bindingTypeNotPublished</c>), а опублікований тип
/// <c>DeletePermitTypeHandler</c> не видаляє ніколи, тож окремої перевірки «є прив'язки» не потрібно - вона була б недосяжною.
/// Тест закріплює наслідок. Мутаційний доказ: прибрати перевірку <c>Status != Draft</c> у <c>DeletePermitTypeHandler</c> -
/// червоніє цей тест (тип зі спільною прив'язкою видалявся б, а FK Restrict дав би помилку провайдера замість 4094).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitTypeInUseByBindingTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тип_із_прив_язкою_аркуша_не_видаляється_ECR_PRM_4094_а_прив_язка_лишається()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var type = $"AIR{_tag}";
        await h.PublishEmissionsTypeAsync(type);

        await using (var save = await h.NextRequestAsync())
        {
            await save.Save.HandleAsync(
                sheet.Doc.TemplateVersionId,
                $"B{_tag}",
                new SavePermitBindingRequest(
                    sheet.Doc.TableDefId, type, "Emissions", "Rows", "ByFacilityPeriodEnd", null, true,
                    [
                        new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                        new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
                    ]),
                default);
        }

        await using var delete = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => delete.Types.Delete.HandleAsync(type, default));
        Assert.Equal("ECR-PRM-4094", error.ErrorCode);

        await using var check = await h.NextRequestAsync();
        Assert.NotNull(await check.Types.Permits.FindByCodeAsync(type, default));
        Assert.Single((await check.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items);
    }

    /// <summary>
    /// PM-09c: нова версія типу, що прибирає таблицю чи стовпець прив'язки, відхиляється ДО бази зрозумілою відмовою
    /// (<c>ECR-PRM-0422 tableRemoved/columnRemoved</c>), а не зовнішнім ключем: опублікований склад не зникає.
    /// Мутаційний доказ: прибрати з <c>ValidateAgainstPublished</c> гілку <c>tableRemoved</c> і <c>columnRemoved</c> -
    /// червоний цей тест (чернетка без таблиці/стовпця збереглась би й упала б лише на публікації).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Нова_версія_типу_без_таблиці_чи_стовпця_прив_язки_відхиляється_а_прив_язка_лишається()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var type = $"AIR{_tag}";
        await h.PublishEmissionsTypeAsync(type);

        await using (var save = await h.NextRequestAsync())
        {
            await save.Save.HandleAsync(
                sheet.Doc.TemplateVersionId,
                $"B{_tag}",
                new SavePermitBindingRequest(
                    sheet.Doc.TableDefId, type, "Emissions", "Rows", "ByFacilityPeriodEnd", null, true,
                    [
                        new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                        new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
                    ]),
                default);
        }

        await using var open = await h.NextRequestAsync();
        var draft = await open.Types.OpenVersion.HandleAsync(type, default);
        var emissions = draft.Tables.Single(t => t.Code == "Emissions");

        await using var noColumn = await h.NextRequestAsync();
        var column = await Assert.ThrowsAsync<BusinessRuleException>(
            () => noColumn.Types.Replace.HandleAsync(
                type,
                new ReplacePermitTypeRequest(
                    draft.RowVersion, draft.NameL10n, "Air", [emissions with { Columns = [.. emissions.Columns.Where(c => c.Code != "Limit")] }], []),
                default));
        Assert.Equal("err.ECR-PRM-0422.columnRemoved", column.Details!["messageKey"]);

        await using var noTable = await h.NextRequestAsync();
        var table = await Assert.ThrowsAsync<BusinessRuleException>(
            () => noTable.Types.Replace.HandleAsync(type, new ReplacePermitTypeRequest(draft.RowVersion, draft.NameL10n, "Air", [], []), default));
        Assert.Equal("err.ECR-PRM-0422.tableRemoved", table.Details!["messageKey"]);

        await using var check = await h.NextRequestAsync();
        Assert.Single((await check.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items);
    }
}
