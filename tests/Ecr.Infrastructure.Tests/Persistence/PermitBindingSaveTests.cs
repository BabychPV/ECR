// tests/Ecr.Infrastructure.Tests/Persistence/PermitBindingSaveTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Збереження прив'язки аркуша до дозволу на живому SQL: створення, заміна колонок, відмови (Permits, PM-09).</summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>version.EnsureStructurallyMutable()</c> - червоний «Опублікована_версія…»; прибрати
/// <c>FindCodeBySheetTableAsync</c>-перевірку - червоний «Друга_прив_язка_на_ту_саму_таблицю…» (замість 409 - помилка
/// провайдера); прибрати <c>RemoveColumns</c> у гілці зміни - червоний «Повторне_збереження_замінює_колонки…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitBindingSaveTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Створення_зберігає_прив_язку_колонки_і_скидає_кеш_метаданих()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");

        await using var save = await h.NextRequestAsync();
        var saved = await save.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", Request(sheet), default);

        Assert.Equal(($"B{_tag}", "Rows", "ByFacilityPeriodEnd", true), (saved.Code, saved.Mode, saved.SelectionRule, saved.IsActive));
        Assert.Equal(
            [("RowKey", "Substance"), ("Limit", "Limit"), ("Info", "Note")],
            saved.Columns.Select(c => (c.Role, c.PermitFieldCode)));
        Assert.Equal([1, 2, 3], saved.Columns.Select(c => c.Ordinal));
        await save.MetadataCache.Received(1).InvalidateAsync(sheet.Doc.TemplateVersionId, Arg.Any<CancellationToken>());

        await using var read = await h.NextRequestAsync();
        var one = await read.Get.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", default);
        Assert.Equal(saved.Id, one.Id);

        var versionId = sheet.Doc.TemplateVersionId;
        var audited = await read.Db.Database.SqlQuery<int>(
            $"SELECT COUNT(*) AS [Value] FROM aud.StructureChange WHERE TemplateVersionId = {versionId} AND EntityType = N'PermitSheetBinding' AND EntityId = {saved.Id} AND Operation = N'Create'").SingleAsync();
        Assert.Equal(1, audited);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторне_збереження_замінює_колонки_і_режим()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");
        await using (var first = await h.NextRequestAsync())
        {
            await first.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", Request(sheet), default);
        }

        // Ліміт стає довідковою колонкою, примітка зникає, правило не змінюється; поле Limit зіставлене знову (той самий ключ).
        var changed = Request(sheet) with
        {
            IsActive = false,
            Columns =
            [
                new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                new PermitBindingColumnRequest("Info", "Limit", sheet.Doc.ColumnDefIds[1]),
            ],
        };
        await using var second = await h.NextRequestAsync();
        var saved = await second.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", changed, default);

        Assert.False(saved.IsActive);
        Assert.Equal([("RowKey", "Substance"), ("Info", "Limit")], saved.Columns.Select(c => (c.Role, c.PermitFieldCode)));

        await using var read = await h.NextRequestAsync();
        Assert.Equal(2, await read.Db.PermitSheetBindingColumns.CountAsync(c => c.BindingId == saved.Id));
        Assert.Single((await read.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Некоректна_прив_язка_відхиляється_422_з_усіма_випадками_і_нічого_не_пишеться()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");

        // Редагована колонка (C3) під ліміт і тип Decimal без одиниць/точності: два випадки зразу.
        var bad = Request(sheet) with
        {
            Columns =
            [
                new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[2]),
            ],
        };
        await using var save = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => save.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", bad, default));

        Assert.Equal("ECR-PRM-0422", error.ErrorCode);
        var cases = ((string)error.Details!["caseKeys"]!).Split(',');
        Assert.Contains("bindingNotReadOnly", cases);
        Assert.Contains("bindingPrecisionMismatch", cases);

        await using var read = await h.NextRequestAsync();
        Assert.Empty((await read.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідомі_тип_таблиця_і_значення_перелічень_дають_422()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");
        var version = sheet.Doc.TemplateVersionId;

        static async Task<string> KeyAsync(Task task)
            => (string)(await Assert.ThrowsAsync<BusinessRuleException>(() => task)).Details!["messageKey"]!;

        Assert.Equal("err.ECR-PRM-0422.bindingTypeUnknown",
            await KeyAsync(h.Save.HandleAsync(version, $"B{_tag}", Request(sheet) with { PermitTypeCode = "NOPE" }, default)));
        Assert.Equal("err.ECR-PRM-0422.bindingPermitTableUnknown",
            await KeyAsync(h.Save.HandleAsync(version, $"B{_tag}", Request(sheet) with { PermitTableCode = "Nope" }, default)));
        Assert.Equal("err.ECR-PRM-0422.bindingEnumUnknown",
            await KeyAsync(h.Save.HandleAsync(version, $"B{_tag}", Request(sheet) with { Mode = "7" }, default)));
        Assert.Equal("err.ECR-PRM-0422.bindingEnumUnknown",
            await KeyAsync(h.Save.HandleAsync(version, $"B{_tag}", Request(sheet) with { SelectionRule = "bySomething" }, default)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Друга_прив_язка_на_ту_саму_таблицю_і_зайнятий_код_дають_409_ECR_PRM_4101()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql);
        var other = await PermitBindingHarness.PrepareSheetAsync(sql);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");
        await using (var first = await h.NextRequestAsync())
        {
            await first.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", Request(sheet), default);
        }

        await using var second = await h.NextRequestAsync();
        var sheetTaken = await Assert.ThrowsAsync<BusinessRuleException>(
            () => second.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"C{_tag}", Request(sheet), default));
        Assert.Equal(("ECR-PRM-4101", "err.ECR-PRM-4101.sheet"), (sheetTaken.ErrorCode, sheetTaken.Details!["messageKey"]));

        // Код унікальний по всій системі: той самий код в ІНШІЙ версії зайнятий теж.
        await using var third = await h.NextRequestAsync();
        var codeTaken = await Assert.ThrowsAsync<BusinessRuleException>(
            () => third.Save.HandleAsync(other.Doc.TemplateVersionId, $"B{_tag}", Request(other), default));
        Assert.Equal("err.ECR-PRM-4101.code", codeTaken.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Наявну_прив_язку_не_можна_перенацілити_на_іншу_таблицю_аркуша()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");
        await using (var first = await h.NextRequestAsync())
        {
            await first.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", Request(sheet), default);
        }

        // Друга таблиця ТІЄЇ Ж версії з тими самими типами колонок.
        int secondTableId;
        await using (var setup = await h.NextRequestAsync())
        {
            var table = new Ecr.Domain.Entities.Configuration.TableDef(
                sheet.Doc.SheetDefId, Ecr.Domain.ValueObjects.EcrCode.Create($"TB2_{_tag}"), PermitHarness.Text("B"), 2,
                Ecr.Domain.Enums.TableLayoutKind.PerPeriodInstance, Ecr.Domain.Enums.TableRowMode.Dynamic);
            setup.Db.TableDefs.Add(table);
            await setup.Db.SaveChangesAsync();
            secondTableId = table.Id;
        }

        await using var move = await h.NextRequestAsync();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => move.Save.HandleAsync(
                sheet.Doc.TemplateVersionId, $"B{_tag}",
                Request(sheet) with { SheetTableDefId = secondTableId, Mode = "Formulas", Columns = [] }, default));
        Assert.Equal("err.ECR-PRM-0422.bindingIdentityChanged", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Опублікована_версія_відхиляє_запис_а_читач_без_Template_Edit_отримує_відмову()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");

        await using var reader = await PermitBindingHarness.CreateAsync(sql, ["Template.View"]);
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => reader.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", Request(sheet), default));

        await using (var freeze = await h.NextRequestAsync())
        {
            var version = await freeze.Db.TemplateVersions.SingleAsync(v => v.Id == sheet.Doc.TemplateVersionId);
            version.Publish(freeze.Types.UserId, DateTime.UtcNow);
            await freeze.Db.SaveChangesAsync();
        }

        await using var save = await h.NextRequestAsync();
        var frozen = await Assert.ThrowsAsync<DomainException>(
            () => save.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", Request(sheet), default));
        Assert.Equal("ECR-TMPL-0409", frozen.ErrorCode);
        Assert.False((await save.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).IsEditable);
    }

    private SavePermitBindingRequest Request(PreparedSheet sheet) => new(
        sheet.Doc.TableDefId,
        $"AIR{_tag}",
        "Emissions",
        "Rows",
        "ByFacilityPeriodEnd",
        null,
        true,
        [
            new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
            new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
            new PermitBindingColumnRequest("Info", "Note", sheet.NoteColumnId),
        ]);
}
