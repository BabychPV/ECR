// tests/Ecr.Infrastructure.Tests/Persistence/PermitBindingPreviewTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Перегляд прив'язки аркуша на живому SQL: вибір дозволу за правилом, рядки з дозволу, ролі колонок, причини «дозволу
/// немає», документ як джерело об'єкта й фактичних комірок (Permits, PM-09).
/// </summary>
/// <remarks>
/// Мутаційні докази: замінити <c>&lt;</c> на <c>&lt;=</c> у вікні чинності (<c>PermitCandidate.IsLiveOn</c>) - червоний
/// «Межі_вікна…»; прибрати відбір <c>IsActive</c> - червоний «Причини…»; прибрати фільтр незіставлених колонок в
/// <c>OverlayFactsAsync</c> - червоний «Документ…». C4 (Registry.View): прибрати виклик
/// <c>PermitRegistryReadGuard.Require</c> у <c>PreviewPermitBindingHandler</c> - червоніють «Читач_з_Template_View…» і
/// «Ресурсний_грант…».
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitBindingPreviewTests(SqlServerFixture sql)
{
    private static readonly DateOnly June30 = new(2026, 6, 30);
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private string Type => $"AIR{_tag}";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дозвіл_за_об_єктом_дає_рядки_з_дозволу_ролі_колонок_і_блокування()
    {
        var (h, sheet) = await ArrangeAsync();
        await using var _ = h;
        var current = await h.SeedPermitAsync(Type, $"N1{_tag}", "Site A", new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 1), true, ("NOx", 12.5m, "first"), ("SO2", 3m, null));
        await h.SeedPermitAsync(Type, $"N0{_tag}", "Site A", new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1), true, ("OLD", 1m, null));
        await h.SeedPermitAsync(Type, $"N2{_tag}", "Site B", new DateOnly(2026, 1, 1), null, true, ("OTHER", 2m, null));

        await using var read = await h.NextRequestAsync();
        var preview = await read.Preview.HandleAsync(
            sheet.Doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(null, null, "  site a ", June30, null), default);

        Assert.Equal((current.EntryId, $"N1{_tag}"), (preview.Permit.EntryId, preview.Permit.Number));
        Assert.Null(preview.Permit.ReasonIfNone);
        Assert.Equal(new DateOnly(2026, 1, 1), preview.Permit.ValidFrom);

        Assert.Equal(current.RowEntryIds.Select(id => $"P{id}"), preview.Rows.Select(r => r.RowKey));
        var first = preview.Rows[0];
        Assert.Equal("NOx", first.Cells[ColumnCode(preview, "RowKey")]);
        Assert.Equal(12.5m, first.Cells[ColumnCode(preview, "Limit")]);
        Assert.Equal("first", first.Cells[ColumnCode(preview, "Info")]);
        Assert.Null(preview.Rows[1].Cells[ColumnCode(preview, "Info")]);

        Assert.Equal(["RowKey", "Limit", "Fact", "Info"], preview.Columns.Select(c => c.Role));
        Assert.Equal([true, true, false, true], preview.Columns.Select(c => c.Locked));
        Assert.Equal(3, first.Locked.Count);
        Assert.Empty(preview.Warnings);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Межі_вікна_чинності_пів_інтервал_а_ValidFrom_включно()
    {
        var (h, sheet) = await ArrangeAsync();
        await using var _ = h;
        await h.SeedPermitAsync(Type, $"N1{_tag}", "Site A", new DateOnly(2026, 1, 1), June30, true, ("NOx", 1m, null));
        await h.SeedPermitAsync(Type, $"N2{_tag}", "Site C", June30, null, true, ("SO2", 2m, null));

        await using var read = await h.NextRequestAsync();
        PermitBindingPreviewDto Preview(string facility, DateOnly end)
            => read.Preview.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(null, null, facility, end, null), default).GetAwaiter().GetResult();

        // ValidTo - перший НЕчинний день: у цей день дозвілу вже немає, за день до того - є.
        Assert.Equal("noPermit", Preview("Site A", June30).Permit.ReasonIfNone);
        Assert.Equal($"N1{_tag}", Preview("Site A", June30.AddDays(-1)).Permit.Number);

        // ValidFrom - перший чинний день.
        Assert.Equal($"N2{_tag}", Preview("Site C", June30).Permit.Number);
        Assert.Equal("noPermit", Preview("Site C", June30.AddDays(-1)).Permit.ReasonIfNone);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Причини_відсутності_дозволу_без_запасного_варіанту()
    {
        var (h, sheet) = await ArrangeAsync();
        await using var _ = h;
        await h.SeedPermitAsync(Type, $"N1{_tag}", "Site A", new DateOnly(2026, 1, 1), null, isActive: false, ("NOx", 1m, null));

        await using var read = await h.NextRequestAsync();
        async Task<PermitBindingPreviewDto> Preview(string? facility)
            => await read.Preview.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(null, null, facility, June30, null), default);

        var noFacility = await Preview(null);
        Assert.Equal("noFacility", noFacility.Permit.ReasonIfNone);
        Assert.Empty(noFacility.Rows);

        // Чернетка (IsActive = false) не береться, хоч розташування збігається.
        var draft = await Preview("Site A");
        Assert.Equal(("noPermit", (long?)null), (draft.Permit.ReasonIfNone, draft.Permit.EntryId));
        Assert.Empty(draft.Rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дозвіл_з_поля_шапки_нечинний_на_кінець_періоду_дає_попередження_і_порожні_рядки()
    {
        var (h, sheet) = await ArrangeAsync(fromHeader: true);
        await using var _ = h;
        var valid = await h.SeedPermitAsync(Type, $"N1{_tag}", "Site A", new DateOnly(2026, 1, 1), null, true, ("NOx", 1m, null));
        var expired = await h.SeedPermitAsync(Type, $"N2{_tag}", "Site A", new DateOnly(2025, 1, 1), new DateOnly(2026, 1, 1), true, ("OLD", 2m, null));

        await using var read = await h.NextRequestAsync();
        PermitBindingPreviewDto Preview(long? entryId)
            => read.Preview.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(null, null, null, June30, entryId), default).GetAwaiter().GetResult();

        var ok = Preview(valid.EntryId);
        Assert.Equal(valid.RowEntryIds.Select(id => $"P{id}"), ok.Rows.Select(r => r.RowKey));
        Assert.Empty(ok.Warnings);

        // REGSUM над таким дозволом мовчки дав би 0: перегляд каже про це прямо.
        var stale = Preview(expired.EntryId);
        Assert.Equal(expired.EntryId, stale.Permit.EntryId);
        Assert.Empty(stale.Rows);
        Assert.Contains(stale.Warnings, w => w.Code == "permitNotValidAtPeriodEnd");

        Assert.Equal("headerEmpty", Preview(null).Permit.ReasonIfNone);
        Assert.Equal("headerUnknown", Preview(long.MaxValue).Permit.ReasonIfNone);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Документ_дає_об_єкт_кінець_періоду_і_накладає_фактичні_комірки_лише_на_незіставлені_колонки()
    {
        var (h, sheet) = await ArrangeAsync(permissions: [.. PermitBindingHarness.Everything, "Document.View"]);
        await using var _ = h;
        var permit = await h.SeedPermitAsync(Type, $"N1{_tag}", "Site A", new DateOnly(2026, 1, 1), null, true, ("NOx", 12.5m, null), ("SO2", 3m, null));

        await using var read = await h.NextRequestAsync();
        const long documentId = 9001;
        const long instanceId = 7001;
        var periodKey = new PeriodKey(202606);
        var doc = sheet.Doc;
        read.Types.Access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), documentId, Arg.Any<CancellationToken>()).Returns(EditDecision.Allow());
        read.Documents.GetTemplateVersionIdAsync(documentId, Arg.Any<CancellationToken>()).Returns(doc.TemplateVersionId);
        read.Periods.FindPeriodBoundsAsync(documentId, 202606, Arg.Any<CancellationToken>()).Returns(new PeriodBounds(new DateOnly(2026, 6, 1), June30));
        read.Headers.GetExpressionValuesAsync(documentId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue> { ["Facility"] = ExpressionValue.Text("SITE A") });
        read.RowsStore.GetTableInstancesAsync(documentId, periodKey, Arg.Any<CancellationToken>())
            .Returns([new TableInstanceRef(instanceId, documentId, doc.TableDefId, doc.TemplateVersionId, 202606)]);
        read.RowsStore.GetRowsAsync(instanceId, periodKey, Arg.Any<CancellationToken>())
            .Returns([new RowState($"P{permit.RowEntryIds[0]}", 500, "v", false)]);
        read.CellsStore.ReadSliceAsync(instanceId, periodKey, Arg.Any<CancellationToken>()).Returns(
        [
            new CellRecord(new CellAddress(periodKey, 500, doc.ColumnDefIds[2]), doc.TableDefId, new CellValueData { ValueNumeric = 7m }),
            new CellRecord(new CellAddress(periodKey, 500, doc.ColumnDefIds[1]), doc.TableDefId, new CellValueData { ValueNumeric = 999m }),
        ]);

        var preview = await read.Preview.HandleAsync(
            doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(documentId, 202606, null, null, null), default);

        Assert.Equal(permit.EntryId, preview.Permit.EntryId);
        Assert.Equal(7m, preview.Rows[0].Cells[ColumnCode(preview, "Fact")]);
        Assert.Equal(12.5m, preview.Rows[0].Cells[ColumnCode(preview, "Limit")]);
        Assert.False(preview.Rows[1].Cells.ContainsKey(ColumnCode(preview, "Fact")));
        Assert.Empty(preview.Warnings);

        // Без Document.View документ як джерело недоступний.
        await using var denied = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => denied.Preview.HandleAsync(doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(documentId, 202606, null, null, null), default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Режим_формул_рядків_не_створює_а_документ_без_періоду_відхиляється()
    {
        var (h, sheet) = await ArrangeAsync(formulas: true, permissions: [.. PermitBindingHarness.Everything, "Document.View"]);
        await using var _ = h;
        await h.SeedPermitAsync(Type, $"N1{_tag}", "Site A", new DateOnly(2026, 1, 1), null, true, ("NOx", 1m, null));

        await using var read = await h.NextRequestAsync();
        var preview = await read.Preview.HandleAsync(
            sheet.Doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(null, null, "Site A", June30, null), default);

        Assert.NotNull(preview.Permit.EntryId);
        Assert.Empty(preview.Rows);
        Assert.Contains(preview.Warnings, w => w.Code == "formulasMode");
        Assert.All(preview.Columns, c => Assert.Equal("Fact", c.Role));

        read.Types.Access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), 9001, Arg.Any<CancellationToken>()).Returns(EditDecision.Allow());
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => read.Preview.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(9001, null, null, null, null), default));
        Assert.Equal("err.ECR-PRM-0422.bindingPreviewPeriodRequired", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_без_Template_View_і_невідома_прив_язка()
    {
        var (h, sheet) = await ArrangeAsync();
        await using var _ = h;

        await using var reader = await PermitBindingHarness.CreateAsync(sql, ["Registry.View"]);
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => reader.Preview.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", new PermitPreviewQuery(null, null, "Site A", June30, null), default));

        await using var read = await h.NextRequestAsync();
        await Assert.ThrowsAsync<NotFoundException>(
            () => read.Preview.HandleAsync(sheet.Doc.TemplateVersionId, "NOPE", new PermitPreviewQuery(null, null, "Site A", June30, null), default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_з_Template_View_і_Document_View_без_Registry_View_отримує_відмову_без_жодних_даних_дозволу()
    {
        var (h, sheet) = await ArrangeAsync();
        await using var _ = h;
        await h.SeedPermitAsync(Type, $"N1{_tag}", "Site A", new DateOnly(2026, 1, 1), null, true, ("NOx", 12.5m, "first"));

        await using var reader = await PermitBindingHarness.CreateAsync(sql, ["Template.View", "Document.View"]);
        var query = new PermitPreviewQuery(null, null, "Site A", June30, null);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => reader.Preview.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", query, default));

        // Та сама відмова, що й у PermitQueryHandlers (Registry.View), і ніде ні номера, ні ліміту, ні рядків.
        var listDenied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => reader.Types.ListPermits.HandleAsync(new Ecr.Application.Common.CursorRequest(10), null, null, null, null, default));
        Assert.Equal((listDenied.ErrorCode, listDenied.Details!["messageKey"]), (denied.ErrorCode, denied.Details!["messageKey"]));
        Assert.Equal("Registry.View", denied.Details["permission"]);
        var shown = denied.Message + string.Join(';', denied.Details.Select(p => $"{p.Key}={p.Value}"));
        Assert.DoesNotContain($"N1{_tag}", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("NOx", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("12.5", shown, StringComparison.Ordinal);

        // Із правом - той самий запит віддає дозвіл і рядки.
        await using var allowed = await PermitBindingHarness.CreateAsync(sql, ["Template.View", "Registry.View"]);
        var preview = await allowed.Preview.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", query, default);
        Assert.Equal($"N1{_tag}", preview.Permit.Number);
        Assert.Single(preview.Rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ресурсний_грант_має_покривати_і_шапку_і_таблицю_а_заборона_на_реєстр_ховає_прив_язку()
    {
        var (h, sheet) = await ArrangeAsync();
        await using var _ = h;
        await h.SeedPermitAsync(Type, $"N1{_tag}", "Site A", new DateOnly(2026, 1, 1), null, true, ("NOx", 12.5m, null));
        var type = (await h.Types.Permits.FindByCodeAsync(Type, default))!;
        var tableRegistry = (await h.Types.Permits.ListTablesAsync(type.Id, default)).Single(t => t.Code == "Emissions").RegistryDefId;
        var query = new PermitPreviewQuery(null, null, "Site A", June30, null);

        async Task<PermitBindingPreviewDto> PreviewAs(Func<AccessBuilder, AccessBuilder> profile)
        {
            await using var reader = await PermitBindingHarness.CreateAsync(sql, ["Template.View"]);
            reader.Types.Access.BuildProfileAsync(reader.Types.UserId, Arg.Any<CancellationToken>())
                .Returns(profile(new AccessBuilder { UserId = reader.Types.UserId }.Permission("Template.View")).Build());
            return await reader.Preview.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", query, default);
        }

        // Гранти Read на обидва реєстри без глобального права - достатньо.
        var granted = await PreviewAs(b => b
            .Grant(ResourceKind.Registry, type.HeaderRegistryDefId, GrantLevel.Read)
            .Grant(ResourceKind.Registry, tableRegistry, GrantLevel.Read));
        Assert.Equal($"N1{_tag}", granted.Permit.Number);

        // Грант лише на шапку: рядки з таблиці прочитати не можна - відмова, а не часткова відповідь.
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => PreviewAs(b => b.Grant(ResourceKind.Registry, type.HeaderRegistryDefId, GrantLevel.Read)));

        // Явна заборона на реєстр шапки перекриває глобальне право і відповідає як неіснуюча прив'язка.
        var hidden = await Assert.ThrowsAsync<NotFoundException>(
            () => PreviewAs(b => b.Permission("Registry.View").Deny(ResourceKind.Registry, type.HeaderRegistryDefId)));
        Assert.Equal("ECR-PRM-0404", hidden.ErrorCode);
    }

    private static string ColumnCode(PermitBindingPreviewDto preview, string role) => preview.Columns.Single(c => c.Role == role).ColumnCode;

    /// <summary>Опублікований тип + шаблон + прив'язка <c>B&lt;tag&gt;</c> (за об'єктом, з шапки або лише формули).</summary>
    private async Task<(PermitBindingHarness Harness, PreparedSheet Sheet)> ArrangeAsync(
        bool fromHeader = false, bool formulas = false, string[]? permissions = null)
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        var h = await PermitBindingHarness.CreateAsync(sql, permissions ?? PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync(Type);

        int? headerFieldId = null;
        if (fromHeader)
        {
            var type = (await h.Types.Permits.FindByCodeAsync(Type, default))!;
            await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
            var field = new HeaderFieldDef(sheet.Doc.TemplateVersionId, EcrCode.Create("PermitRef"), PermitHarness.Text("Permit"), 2, CellDataType.Lookup);
            field.SetLookup(type.HeaderRegistryDefId);
            db.HeaderFieldDefs.Add(field);
            await db.SaveChangesAsync();
            headerFieldId = field.Id;
        }

        await using var save = await h.NextRequestAsync();
        await save.Save.HandleAsync(
            sheet.Doc.TemplateVersionId,
            $"B{_tag}",
            new SavePermitBindingRequest(
                sheet.Doc.TableDefId, Type, "Emissions", formulas ? "Formulas" : "Rows",
                fromHeader ? "FromHeaderLookup" : "ByFacilityPeriodEnd", headerFieldId, true,
                formulas
                    ? []
                    :
                    [
                        new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
                        new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
                        new PermitBindingColumnRequest("Info", "Note", sheet.NoteColumnId),
                    ]),
            default);

        return (await h.NextRequestAsync(), sheet);
    }
}
