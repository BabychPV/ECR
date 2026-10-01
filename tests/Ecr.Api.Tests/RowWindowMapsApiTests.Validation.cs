// tests/Ecr.Api.Tests/RowWindowMapsApiTests.Validation.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Відмови прив'язки за вікном рядка, які гайд тестувальника (TESTER-SCENARIOS Н-А7) називає, а HTTP-тест досі не
/// перевіряв: колонка вікна з чужої таблиці, невідома згортка і 404 з ключем на кожному маршруті однієї прив'язки.
/// </summary>
/// <remarks>
/// Мутаційні докази:
/// <list type="bullet">
/// <item>у <c>RowWindowMap</c> прибрати цикл <c>foreach (var window in new[] { start, end })</c> з перевіркою таблиці —
/// червоніє <see cref="Колонка_вікна_з_чужої_таблиці_і_невідома_згортка_422_а_невідома_прив_язка_404_з_ключем"/>
/// (прив'язка створюється, 201);</item>
/// <item>у <c>RowWindowMapSupport.RequireShape</c> прибрати <c>Enum.IsDefined(summary)</c> — той самий тест отримує 500
/// (домен кидає <c>ArgumentOutOfRangeException</c>) замість <c>rowWindowSummaryUnknown</c>.</item>
/// </list>
/// </remarks>
public sealed partial class RowWindowMapsApiTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-A1-CRUD")]
    [Trait("Scenario", "Н-А7")]
    public async Task Колонка_вікна_з_чужої_таблиці_і_невідома_згортка_422_а_невідома_прив_язка_404_з_ключем()
    {
        await using var stand = await ArrangeAsync();

        // Колонка чужої таблиці — Date: інакше раніше спрацювала б перевірка типу (windowColumnsNotDate).
        await ExecuteAsync($"UPDATE cfg.ColumnDef SET DataType = {(int)CellDataType.Date} WHERE Id = {stand.ForeignColumn}");

        using var app = new EcrApiFactory(sql);
        using var manager = await SignedInAsync(app, ["Integration.Manage"], stand.ProjectId, GrantLevel.Manage);

        await ExpectAsync(manager, Body(stand, stand.TargetA, start: stand.ForeignColumn), 422, "err.ECR-INT-0422.windowColumnNotInTable");
        await ExpectAsync(manager, Body(stand, stand.TargetA, end: stand.ForeignColumn), 422, "err.ECR-INT-0422.windowColumnNotInTable");

        // Числове значення поза переліком згорток доходить до обробника (рядок «Foo» відсік би ще біндер моделі).
        await ExpectAsync(
            manager,
            new
            {
                tableDefId = stand.TableDefId,
                targetColumnDefId = stand.TargetA,
                startColumnDefId = stand.Start,
                endColumnDefId = stand.End,
                selectorColumnDefId = stand.Selector,
                summary = 99,
                isStep = false,
                targetUnitId = stand.UnitTarget,
                sources = new[] { Source("A", stand.EntityId, "Flare.Total", stand.UnitSource) },
            },
            422,
            "err.ECR-REQ-0422.rowWindowSummaryUnknown");

        Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM ext.RowWindowMap WHERE TableDefId = {stand.TableDefId}"));

        var missing = new Uri("/api/v1/row-window-maps/2147483000", UriKind.Relative);
        HttpResponseMessage[] notFound =
        [
            await manager.GetAsync(missing),
            await manager.PutAsJsonAsync(missing, Replace(stand)),
            await manager.DeleteAsync(missing),
        ];
        foreach (var response in notFound)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("err.ECR-INT-0404.rowWindowMap", (await JsonAsync(response)).GetProperty("messageKey").GetString());
        }
    }
}
