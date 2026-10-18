// tests/Ecr.Infrastructure.Tests/Persistence/ReplacePermitTypeTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Заміна чернетки типу: збереження, токен, 422 без сліду, права, нова версія неопублікованого (PM-03).</summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>RequireRowVersion</c> - червоний «Застарілий_токен…»; прибрати
/// <c>PermissionCheck.RequireAsync</c> - червоний «Читач_не_змінює…».
/// </remarks>
[Collection("SqlServer")]
public sealed class ReplacePermitTypeTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string Code(string name) => $"{name}{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заміна_чернетки_зберігає_склад_нормалізує_порядок_і_ключ_та_віддає_новий_токен()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("AIR"), Name("Air"), "Air"), default);

        await using var put = await h.NextRequestAsync();
        var saved = await put.Replace.HandleAsync(Code("AIR"), Request(created.RowVersion, Emissions()), default);

        // Ключовий стовпець став обов'язковим, позиції - за порядком масиву.
        var table = Assert.Single(saved.Tables);
        Assert.Equal([1, 2], table.Columns.Select(c => c.Order));
        Assert.True(table.Columns[0].Required);
        Assert.NotEqual(created.RowVersion, saved.RowVersion);

        await using var read = await h.NextRequestAsync();
        var again = await read.Get.HandleAsync(Code("AIR"), default);
        Assert.Equal(saved.RowVersion, again.RowVersion);
        Assert.Equal(["Substance", "Limit"], again.Tables[0].Columns.Select(c => c.Code));
        Assert.Equal("Block", Assert.Single(again.Tables[0].Rules).Severity);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застарілий_токен_дає_ECR_PRM_4093_і_чужа_правка_не_затирається()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("AIR"), Name("Air"), "Air"), default);

        await using var first = await h.NextRequestAsync();
        await first.Replace.HandleAsync(Code("AIR"), Request(created.RowVersion, Emissions()), default);

        // Друга вкладка ще тримає токен створення.
        await using var second = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => second.Replace.HandleAsync(Code("AIR"), Request(created.RowVersion, []), default));

        Assert.Equal("ECR-PRM-4093", error.ErrorCode);
        Assert.Equal("err.ECR-PRM-4093.changed", error.Details!["messageKey"]);

        await using var read = await h.NextRequestAsync();
        Assert.Single((await read.Get.HandleAsync(Code("AIR"), default)).Tables);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Склад_що_не_складається_відхиляється_422_а_чернетка_лишається_попередньою()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("AIR"), Name("Air"), "Air"), default);

        var noKey = Emissions() with { Columns = [Col("Limit", false)] };
        await using var put = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => put.Replace.HandleAsync(Code("AIR"), Request(created.RowVersion, noKey), default));

        Assert.Equal("ECR-PRM-0422", error.ErrorCode);
        Assert.Equal("err.ECR-PRM-0422.tableWithoutKey", error.Details!["messageKey"]);

        await using var read = await h.NextRequestAsync();
        var current = await read.Get.HandleAsync(Code("AIR"), default);
        Assert.Empty(current.Tables);
        Assert.Equal(created.RowVersion, current.RowVersion);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Спільний_довідник_якого_немає_відхиляється_422()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("AIR"), Name("Air"), "Air"), default);

        var table = Emissions() with
        {
            Columns = [Col("Substance", true), Col("Site", false) with { DataType = "Lookup", ListScope = "SharedRegistry", ListRegistryCode = $"NO_SUCH_{_tag}" }],
        };

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => h.Replace.HandleAsync(Code("AIR"), Request(created.RowVersion, table), default));
        Assert.Equal("err.ECR-PRM-0422.sharedRegistryUnknown", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_не_змінює_чернетку_а_нова_версія_неопублікованого_типу_відхиляється()
    {
        await using var h = await PermitHarness.CreateAsync(sql, PermitHarness.Everything);
        var created = await h.Create.HandleAsync(new CreatePermitTypeRequest(Code("AIR"), Name("Air"), "Air"), default);

        await using var reader = await PermitHarness.CreateAsync(sql, ["Registry.View"]);
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => reader.Replace.HandleAsync(Code("AIR"), Request(created.RowVersion, Emissions()), default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.OpenVersion.HandleAsync(Code("AIR"), default));

        var notPublished = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.OpenVersion.HandleAsync(Code("AIR"), default));
        Assert.Equal("err.ECR-PRM-4093.notPublished", notPublished.Details!["messageKey"]);

        await using var read = await h.NextRequestAsync();
        Assert.Empty((await read.Get.HandleAsync(Code("AIR"), default)).Tables);
    }

    private static ReplacePermitTypeRequest Request(string rowVersion, params PermitTypeTableDto[] tables)
        => new(rowVersion, Name("Air"), "Air", tables, []);

    private static PermitTypeTableDto Emissions()
        => new(
            "Emissions", Name("Emissions"), 9,
            [Col("Substance", true), Col("Limit", false) with { DataType = "Decimal", Precision = 12, Scale = 3 }],
            [new PermitTypeRuleDto("NonNegative", Name("Not negative"), "[Limit] >= 0", "Block")]);

    private static PermitTypeColumnDto Col(string code, bool key)
        => new(code, Name(code), "String", null, false, key, null, null, null, null, null, null, null, 9);

    private static Dictionary<string, string> Name(string en) => new() { ["en"] = en };
}
