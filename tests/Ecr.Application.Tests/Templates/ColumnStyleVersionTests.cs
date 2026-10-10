using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// A1-04: <c>ColumnDef.StyleId</c> з тіла запиту мусить належати ТІЙ САМІЙ версії — і в <c>PUT</c> колонки, і в
/// презентаційному <c>PATCH</c>.
/// </summary>
/// <remarks>
/// ⛔ Ключа на <c>cfg.StyleDef</c> у колонки немає, а обробники брали число з тіла як є: стиль ЧУЖОЇ версії чи
/// неіснуючий записувався мовчки (показ читав чужий вигляд, клон версії лишав посилання порожнім). Справжня СУБД.
/// Мутація: прибрати перевірку <c>ofVersion.ContainsKey</c> в обробнику — відповідний тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class ColumnStyleVersionTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Ecr.Application.Ports.IMetadataCache _cache = Substitute.For<Ecr.Application.Ports.IMetadataCache>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "A1-04")]
    public async Task PATCH_стилю_чужої_версії_відхиляється_422_а_власної_проходить()
    {
        var own = await BareVersionAsync();
        var foreign = await BareVersionAsync();

        await using (var db = Context())
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => PatchHandler(db).PatchAsync(
                    own.VersionId, StylePatch(own.ColumnDefId, foreign.StyleId), 9, CancellationToken.None));

            Assert.Equal("err.ECR-TMPL-0422.presentationValueInvalid", error.Details!["messageKey"]);
            Assert.Equal("StyleId", error.Details["field"]);
        }

        Assert.Null(await StyleOfColumnAsync(own.ColumnDefId));

        await using (var db = Context())
        {
            await PatchHandler(db).PatchAsync(
                own.VersionId, StylePatch(own.ColumnDefId, own.StyleId), 9, CancellationToken.None);
        }

        Assert.Equal(own.StyleId, await StyleOfColumnAsync(own.ColumnDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "A1-04")]
    public async Task PATCH_неіснуючого_стилю_відхиляється_422()
    {
        var own = await BareVersionAsync();

        await using var db = Context();
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => PatchHandler(db).PatchAsync(
                own.VersionId, StylePatch(own.ColumnDefId, 2_000_000_000), 9, CancellationToken.None));

        Assert.Null(await StyleOfColumnAsync(own.ColumnDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "A1-04")]
    public async Task PUT_колонки_зі_стилем_чужої_версії_відхиляється_422_а_власної_проходить()
    {
        var own = await BareVersionAsync();
        var foreign = await BareVersionAsync();

        await using (var db = Context())
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => SaveHandler(db).HandleAsync(
                    own.VersionId, own.TableDefId, "Jan", Save(foreign.StyleId), CancellationToken.None));

            Assert.Equal("err.ECR-TMPL-0422.presentationValueInvalid", error.Details!["messageKey"]);
        }

        Assert.Null(await StyleOfColumnAsync(own.ColumnDefId));

        await using (var db = Context())
        {
            var dto = await SaveHandler(db).HandleAsync(
                own.VersionId, own.TableDefId, "Jan", Save(own.StyleId), CancellationToken.None);
            Assert.Equal(own.StyleId, dto.StyleId);
        }

        Assert.Equal(own.StyleId, await StyleOfColumnAsync(own.ColumnDefId));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static string StylePatch(int columnDefId, int styleId)
        => $$"""[{"entityType":"ColumnDef","entityId":{{columnDefId}},"field":"StyleId","value":"{{styleId}}"}]""";

    private static SaveColumnDefCommand Save(int styleId)
        => new(
            new Dictionary<string, string> { ["en"] = "Jan" }, null, CellDataType.Decimal,
            false, false, false, null, null, null, null, styleId, null, null, null);

    private void Authorize()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());
    }

    private PatchPresentationHandler PatchHandler(EcrDbContext db)
    {
        Authorize();

        return new PatchPresentationHandler(
            new Repository<TemplateVersion, int>(db), new TemplateVersionStore(db), new ChangeClassifier(), _cache,
            new AuditWriter(db), new UnitOfWork(db), new TestClock(Now), _access, _user, new StyleCatalog(db));
    }

    private SaveColumnDefHandler SaveHandler(EcrDbContext db)
    {
        Authorize();

        return new SaveColumnDefHandler(
            new TemplateVersionStore(db), new ChangeClassifier(), _cache, new AuditWriter(db),
            new UnitOfWork(db), new TestClock(Now), _access, _user,
            Substitute.For<Ecr.Application.Ports.IUnitCatalog>(), registries: null, styles: new StyleCatalog(db));
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private async Task<int?> StyleOfColumnAsync(int columnDefId)
    {
        await using var fresh = Context();
        return (await fresh.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == columnDefId)).StyleId;
    }

    private async Task<Stand> BareVersionAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"CS{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        var style = new StyleDef(version.Id, EcrCode.Create($"ST{tag}"));
        db.StyleDefs.Add(style);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(table.Id, EcrCode.Create("Jan"), Name("Jan"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();

        return new Stand(version.Id, table.Id, column.Id, style.Id);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Stand(int VersionId, int TableDefId, int ColumnDefId, int StyleId);
}
