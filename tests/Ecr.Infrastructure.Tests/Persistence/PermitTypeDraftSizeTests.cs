// tests/Ecr.Infrastructure.Tests/Persistence/PermitTypeDraftSizeTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits;
using Ecr.Application.Permits.Dto;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Межа розміру чернетки типу (аудит, прохід A UI, PA-U-01): чернетка на десятки МБ вішала перелік і сторінки типів для всіх.
/// Запис понад межу - 422 <c>draftTooLarge</c>; уже збережена понад межу не розбирається (читання - той самий 422, перелік - тип
/// без таблиць, видалення працює).
/// </summary>
/// <remarks>
/// Мутаційний доказ: підняти <c>PermitTypeLimits.MaxDraftJsonChars</c> удвічі - червоніють обидва тести (запис проходить,
/// збережений «зіпсований» JSON розбирається чи ламає перелік).
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitTypeDraftSizeTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Запис_чернетки_понад_2_Mі_символів_відхиляється_422_draftTooLarge_і_чернетка_не_змінюється()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var code = $"DS{_tag}";
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(code, PermitFixtures.Name(code), "Air"), default);

        // 100 значень списку по 21 000 символів = 2,1 млн символів: кожне значення, назва й кількість у межах інших лімітів.
        var choices = Enumerable.Range(0, 100).Select(i => i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 21_000)).ToList();
        var big = new PermitTypeTableDto(
            "T", PermitFixtures.Name("T"), 1,
            [
                new PermitTypeColumnDto("K", PermitFixtures.Name("K"), "String", null, true, true, null, null, null, null, null, null, null, 1),
                new PermitTypeColumnDto("L", PermitFixtures.Name("L"), "Lookup", null, false, false, null, null, null, "TypeOnly", null, null, choices, 2),
            ],
            []);

        await using var put = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => put.Replace.HandleAsync(code, new ReplacePermitTypeRequest(created.RowVersion, PermitFixtures.Name(code), "Air", [big], []), default));

        Assert.Equal(("ECR-PRM-0422", "err.ECR-PRM-0422.draftTooLarge"), (error.ErrorCode, error.Details!["messageKey"]));
        Assert.Equal("2097152", error.Details["max"]);

        await using var read = await h.NextRequestAsync();
        var after = await read.Get.HandleAsync(code, default);
        Assert.Equal((created.RowVersion, 0), (after.RowVersion, after.Tables.Count));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збережена_понад_межу_чернетка_не_розбирається_читання_422_перелік_без_таблиць_видалення_працює()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.EverythingWithData);
        var code = $"DB{_tag}";
        await h.Create.HandleAsync(new CreatePermitTypeRequest(code, PermitFixtures.Name(code), "Air"), default);

        // Чернетку дописано поза API (як у знахідці PA-U-01): це лише пробіли, розбір ще й не потрібен для відмови.
        await using (var seed = await h.NextRequestAsync())
        {
            var type = await seed.Db.PermitTypeDefs.SingleAsync(t => t.Code == code);
            type.SaveDraft("{" + new string(' ', PermitTypeLimits.MaxDraftJsonChars + 10) + "}", h.UserId, new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc));
            await seed.Db.SaveChangesAsync();
        }

        await using var get = await h.NextRequestAsync();
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => get.Get.HandleAsync(code, default));
        Assert.Equal("err.ECR-PRM-0422.draftTooLarge", refused.Details!["messageKey"]);

        await using var list = await h.NextRequestAsync();
        var page = await list.List.HandleAsync(new CursorRequest(50), null, null, code, default);
        var item = Assert.Single(page.Items, i => i.Code == code);
        Assert.Empty(item.Tables);

        await using var delete = await h.NextRequestAsync();
        await delete.Delete.HandleAsync(code, default);
        await using var gone = await h.NextRequestAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => gone.Get.HandleAsync(code, default));
    }
}
