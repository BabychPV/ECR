// tests/Ecr.Infrastructure.Tests/Persistence/PermitBindingQueryTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Читання прив'язок аркуша до дозволу: перелік, одна за кодом, права, 404 (Permits, PM-09).</summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>PermissionCheck.RequireAsync</c> у <c>ListPermitBindingsHandler</c> - червоний
/// «Без_права_Template_View…»; прибрати відбір за версією в <c>PermitBindingStore.ListAsync</c> - червоний
/// «Перелік_віддає_лише_прив_язки_своєї_версії».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitBindingQueryTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_без_прив_язок_дає_порожній_перелік_і_ознаку_редагування()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: default);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);

        var all = await h.List.HandleAsync(doc.TemplateVersionId, default);

        Assert.True(all.IsEditable);
        Assert.Empty(all.Items);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_віддає_лише_прив_язки_своєї_версії_з_кодами_таблиць_і_колонок()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var mine = await builder.BuildAsync(ct: default);
        var other = await builder.BuildAsync(ct: default);
        var typeCode = $"AIRQ{_tag}";

        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync(typeCode);
        await using var seed = await h.NextRequestAsync();
        var (typeId, tableId) = await TypeAndTableAsync(seed, typeCode);
        AddBinding(seed, $"BQ1{_tag}", mine, typeId, tableId);
        AddBinding(seed, $"BQ2{_tag}", other, typeId, tableId);
        await seed.Db.SaveChangesAsync();

        await using var read = await h.NextRequestAsync();
        var all = await read.List.HandleAsync(mine.TemplateVersionId, default);

        var item = Assert.Single(all.Items);
        Assert.Equal($"BQ1{_tag}", item.Code);
        Assert.Equal((typeCode, "Emissions", "Rows", "ByFacilityPeriodEnd"), (item.PermitTypeCode, item.PermitTableCode, item.Mode, item.SelectionRule));
        Assert.Equal($"TBL{mine.SheetCode["SHEET".Length..]}", item.SheetTableCode);
        var column = Assert.Single(item.Columns);
        Assert.Equal(("RowKey", "Substance"), (column.Role, column.PermitFieldCode));
        Assert.Equal(mine.ColumnDefIds[0], column.SheetColumnDefId);
        Assert.StartsWith("C1_", column.SheetColumnCode, StringComparison.Ordinal);

        var one = await read.Get.HandleAsync(mine.TemplateVersionId, $"BQ1{_tag}", default);
        Assert.Equal(item.Id, one.Id);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідомий_код_і_чужа_версія_дають_ECR_PRM_0404()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var mine = await builder.BuildAsync(ct: default);
        var other = await builder.BuildAsync(ct: default);
        var typeCode = $"AIRN{_tag}";

        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync(typeCode);
        await using var seed = await h.NextRequestAsync();
        var (typeId, tableId) = await TypeAndTableAsync(seed, typeCode);
        AddBinding(seed, $"BN{_tag}", other, typeId, tableId);
        await seed.Db.SaveChangesAsync();

        await using var read = await h.NextRequestAsync();
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => read.Get.HandleAsync(mine.TemplateVersionId, "NOPE", default));
        Assert.Equal(("ECR-PRM-0404", "err.ECR-PRM-0404.binding"), (missing.ErrorCode, missing.Details!["messageKey"]));

        // Прив'язка існує, але в ІНШІЙ версії: у цій її немає.
        await Assert.ThrowsAsync<NotFoundException>(() => read.Get.HandleAsync(mine.TemplateVersionId, $"BN{_tag}", default));
        var noVersion = await Assert.ThrowsAsync<NotFoundException>(() => read.Get.HandleAsync(int.MaxValue, $"BN{_tag}", default));
        Assert.Equal("ECR-TMPL-0404", noVersion.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Template_View_читання_відмовлено()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: default);
        await using var h = await PermitBindingHarness.CreateAsync(sql, ["Registry.View"]);

        await Assert.ThrowsAsync<AccessDeniedException>(() => h.List.HandleAsync(doc.TemplateVersionId, default));
        await Assert.ThrowsAsync<AccessDeniedException>(() => h.Get.HandleAsync(doc.TemplateVersionId, "X", default));
    }

    private static async Task<(int TypeId, int TableId)> TypeAndTableAsync(PermitBindingHarness h, string typeCode)
    {
        var type = (await h.Types.Permits.FindByCodeAsync(typeCode, default))!;
        var table = (await h.Types.Permits.ListTablesAsync(type.Id, default)).Single(t => t.Code == "Emissions");
        return (type.Id, table.Id);
    }

    private static void AddBinding(PermitBindingHarness h, string code, TestDocument doc, int typeId, int tableId)
    {
        var binding = new PermitSheetBinding(
            EcrCode.Create(code), doc.TemplateVersionId, doc.TableDefId, typeId, tableId,
            PermitBindingMode.Rows, PermitSelectionRule.ByFacilityPeriodEnd, null);
        binding.AddColumn(new PermitSheetBindingColumn(PermitColumnRole.RowKey, EcrCode.Create("Substance"), doc.ColumnDefIds[0], 1));
        h.Db.PermitSheetBindings.Add(binding);
    }
}
