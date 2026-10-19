// tests/Ecr.Infrastructure.Tests/Persistence/PermitTypeInUseByBindingTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
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
}
