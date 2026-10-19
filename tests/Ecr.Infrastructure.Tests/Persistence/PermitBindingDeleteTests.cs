// tests/Ecr.Infrastructure.Tests/Persistence/PermitBindingDeleteTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Видалення прив'язки аркуша до дозволу: колонки явно, Breaking з документами, права (Permits, PM-09).</summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>bindings.RemoveColumns(columns)</c> - червоний «Видалення_прибирає…» (FK від колонок
/// Restrict); прибрати відмову на <c>Breaking</c> - червоний «Версія_з_документами…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitBindingDeleteTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_прибирає_прив_язку_і_її_колонки_і_скидає_кеш()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        var saved = await SaveAsync(sheet);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);

        await h.Delete.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", default);

        await h.MetadataCache.Received(1).InvalidateAsync(sheet.Doc.TemplateVersionId, Arg.Any<CancellationToken>());
        await using var read = await h.NextRequestAsync();
        Assert.Empty((await read.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items);
        Assert.Equal(0, await read.Db.PermitSheetBindingColumns.CountAsync(c => c.BindingId == saved.Id));
        var audited = await read.Db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS [Value] FROM aud.StructureChange WHERE EntityType = N'PermitSheetBinding' AND EntityId = {saved.Id} AND Operation = N'Delete'")
            .SingleAsync();
        Assert.Equal(1, audited);

        // Таблицю можна прив'язати знов: UQ_PermitSheetBinding_Sheet звільнено.
        await SaveAsync(sheet, $"B2{_tag}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідома_прив_язка_дає_404()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => h.Delete.HandleAsync(sheet.Doc.TemplateVersionId, "NOPE", default));

        Assert.Equal("err.ECR-PRM-0404.binding", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_з_документами_не_віддає_видалити_прив_язку_ECR_SCHM_0409()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql);
        await SaveAsync(sheet);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => h.Delete.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", default));

        Assert.Equal(("ECR-SCHM-0409", "err.ECR-SCHM-0409.permitBindingBreaking"), (error.ErrorCode, error.Details!["messageKey"]));
        await using var read = await h.NextRequestAsync();
        Assert.Single((await read.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Опублікована_версія_і_читач_не_видаляють()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await SaveAsync(sheet);

        await using var reader = await PermitBindingHarness.CreateAsync(sql, ["Template.View"]);
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => reader.Delete.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", default));

        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await using (var freeze = await h.NextRequestAsync())
        {
            var version = await freeze.Db.TemplateVersions.SingleAsync(v => v.Id == sheet.Doc.TemplateVersionId);
            version.Publish(freeze.Types.UserId, DateTime.UtcNow);
            await freeze.Db.SaveChangesAsync();
        }

        await using var del = await h.NextRequestAsync();
        var frozen = await Assert.ThrowsAsync<DomainException>(
            () => del.Delete.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", default));
        Assert.Equal("ECR-TMPL-0409", frozen.ErrorCode);
        Assert.Single((await del.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items);
    }

    private async Task<Ecr.Application.Permits.Bindings.PermitBindingDto> SaveAsync(PreparedSheet sheet, string? code = null)
    {
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        var typeCode = $"AIR{_tag}";
        if (await h.Types.Permits.FindByCodeAsync(typeCode, default) is null)
        {
            await h.PublishEmissionsTypeAsync(typeCode);
        }

        await using var save = await h.NextRequestAsync();
        return await save.Save.HandleAsync(
            sheet.Doc.TemplateVersionId,
            code ?? $"B{_tag}",
            new SavePermitBindingRequest(
                sheet.Doc.TableDefId, typeCode, "Emissions", "Rows", "ByFacilityPeriodEnd", null, true,
                [
                    new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                    new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
                    new PermitBindingColumnRequest("Info", "Note", sheet.NoteColumnId),
                ]),
            default);
    }
}
