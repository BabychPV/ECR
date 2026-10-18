// tests/Ecr.Infrastructure.Tests/Persistence/CreatePermitTypeTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Заведення типу дозволу: шапка, чернетка, дублікати, копія, права (Permits, PM-03).</summary>
/// <remarks>
/// Мутаційні докази: прибрати перевірку <c>FindByCodeAsync</c> у <c>CreatePermitTypeHandler</c> - червоний
/// «Дубль_коду…»; прибрати <c>PermissionCheck.RequireAsync</c> - червоний «Без_права_EditDefinition…».
/// </remarks>
[Collection("SqlServer")]
public sealed class CreatePermitTypeTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string Code(string name) => $"{name}{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тип_створюється_чернеткою_з_порожнім_прихованим_реєстром_шапки()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);

        var created = await h.Create.HandleAsync(Request(Code("AIR")), default);

        Assert.Equal(("Draft", 1, (int?)1, true), (created.Status, created.Version, created.DraftVersion, created.HasUnpublishedChanges));
        Assert.Equal(8, created.HeaderFields.Count);

        await using var next = await h.NextRequestAsync();
        var type = await next.Permits.FindByCodeAsync(Code("AIR"), default);
        Assert.NotNull(type);
        Assert.True(type.HasDraft);

        var header = await next.Registries.FindDefinitionByIdAsync(type.HeaderRegistryDefId, default);
        Assert.Equal($"PRM_{Code("AIR")}", header!.Code);
        Assert.True(header.IsTemporal);
        Assert.Empty(header.Fields);

        // Реєстр шапки прихований від загального переліку.
        Assert.Contains(type.HeaderRegistryDefId, await next.Permits.ListHiddenRegistryIdsAsync(default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дубль_коду_типу_і_зайнятий_код_реєстру_шапки_дають_ECR_PRM_4091()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        await h.Create.HandleAsync(Request(Code("DUP")), default);

        await using var again = await h.NextRequestAsync();
        var duplicate = await Assert.ThrowsAsync<BusinessRuleException>(
            () => again.Create.HandleAsync(Request(Code("DUP")), default));

        Assert.Equal("ECR-PRM-4091", duplicate.ErrorCode);
        Assert.Equal("err.ECR-PRM-4091.code", duplicate.Details!["messageKey"]);

        // Реєстр з кодом PRM_<тип> уже є (створений вручну): тип не заводиться, а відмова називає реєстр.
        await using (var db = sql.CreateContext())
        {
            db.RegistryDefs.Add(new RegistryDef(Ecr.Domain.ValueObjects.EcrCode.Create($"PRM_{Code("TAKEN")}"), PermitHarness.Text("x"), false));
            await db.SaveChangesAsync();
        }

        await using var taken = await h.NextRequestAsync();
        var clash = await Assert.ThrowsAsync<BusinessRuleException>(
            () => taken.Create.HandleAsync(Request(Code("TAKEN")), default));
        Assert.Equal("err.ECR-PRM-4091.registryCode", clash.Details!["messageKey"]);

        await using var check = await h.NextRequestAsync();
        Assert.Null(await check.Permits.FindByCodeAsync(Code("TAKEN"), default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_EditDefinition_тип_не_заводиться_а_читач_читає()
    {
        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View"]);

        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.Create.HandleAsync(Request(Code("NOPE")), default));

        await using var check = await reader.NextRequestAsync();
        Assert.Null(await check.Permits.FindByCodeAsync(Code("NOPE"), default));
        Assert.NotNull(await check.List.HandleAsync(new Ecr.Application.Common.CursorRequest(), null, null, null, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Некоректні_назва_категорія_і_задовгий_код_відхиляються_422_без_сліду_в_базі()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);

        var category = await Assert.ThrowsAsync<BusinessRuleException>(
            () => h.Create.HandleAsync(Request(Code("BADC")) with { Category = "Soil" }, default));
        var longCode = await Assert.ThrowsAsync<BusinessRuleException>(
            () => h.Create.HandleAsync(Request(new string('X', 62)), default));

        Assert.Equal("err.ECR-PRM-0422.categoryUnknown", category.Details!["messageKey"]);
        Assert.Equal("err.ECR-PRM-0422.registryCodeInvalid", longCode.Details!["messageKey"]);

        await using var check = await h.NextRequestAsync();
        Assert.Null(await check.Permits.FindByCodeAsync(Code("BADC"), default));
        Assert.False(await check.Db.RegistryDefs.AnyAsync(d => d.Code == $"PRM_{Code("BADC")}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Копія_переносить_склад_джерела_і_відкидає_посилання_на_його_приховані_списки()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        await h.Create.HandleAsync(Request(Code("SRC")), default);

        // Склад джерела - чернеткою напряму (PUT з'являється наступним кроком).
        await using (var seed = await h.NextRequestAsync())
        {
            var source = (await seed.Permits.FindByCodeAsync(Code("SRC"), default))!;
            var content = new PermitTypeContent(
                Name("Source"), "Air",
                [new PermitTypeTableDto(
                    "Emissions", Name("Emissions"), 1,
                    [
                        Col("Substance", true, null),
                        Col("Kind", false, "TypeOnly") with { DataType = "Lookup", ListRegistryCode = "PRM_OLD_KIND", Choices = ["A", "B"] },
                    ],
                    [])],
                []);
            source.SaveDraft(Ecr.Application.Permits.PermitTypeCodec.Serialize(content), seed.UserId, DateTime.UtcNow);
            await seed.Uow.SaveChangesAsync(default);
        }

        await using var copying = await h.NextRequestAsync();
        var copy = await copying.Create.HandleAsync(Request(Code("CPY")) with { CopyFromCode = Code("SRC") }, default);

        var table = Assert.Single(copy.Tables);
        Assert.Equal(["Substance", "Kind"], table.Columns.Select(c => c.Code));
        var kind = table.Columns[1];
        Assert.Equal(["A", "B"], kind.Choices);
        Assert.Null(kind.ListRegistryCode);

        await using var missing = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => missing.Create.HandleAsync(Request(Code("NOSRC")) with { CopyFromCode = "NO_SUCH_TYPE" }, default));
        Assert.Equal("ECR-PRM-0404", error.ErrorCode);
    }

    private static CreatePermitTypeRequest Request(string code)
        => new(code, Name(code), "Air");

    private static Dictionary<string, string> Name(string en) => new() { ["en"] = en };

    private static PermitTypeColumnDto Col(string code, bool key, string? scope)
        => new(code, Name(code), "String", null, key, key, null, null, null, scope, null, null, null, 1);
}
