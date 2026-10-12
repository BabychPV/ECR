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
/// CELL-STYLE L2: презентаційний <c>PATCH</c> задає й скидає <c>RowDef.StyleId</c> та <c>TableDef.HeaderStyleId</c>
/// на справжній СУБД (білий список колонок у <c>TemplateVersionStore.PresentationColumns</c>); стиль чужої версії — 422.
/// </summary>
/// <remarks>
/// Мутація: прибрати <c>[("RowDef","StyleId")]</c> / <c>[("TableDef","HeaderStyleId")]</c> із
/// <c>PresentationColumns</c> — відповідний тест червоний (<c>presentationFieldUnknown</c>); прибрати
/// <c>"HeaderStyleId"</c> із перевірки в <c>PatchPresentationHandler</c> — тест чужого стилю шапки червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class RowHeaderStylePatchTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Ecr.Application.Ports.IMetadataCache _cache = Substitute.For<Ecr.Application.Ports.IMetadataCache>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task PATCH_задає_і_скидає_стиль_рядка()
    {
        var own = await BareVersionAsync();

        await using (var db = Context())
        {
            await PatchHandler(db).PatchAsync(
                own.VersionId, Patch("RowDef", own.RowDefId, "StyleId", own.StyleId), 9, CancellationToken.None);
        }

        Assert.Equal(own.StyleId, (await ReadAsync(own)).RowStyle);

        await using (var db = Context())
        {
            await PatchHandler(db).PatchAsync(
                own.VersionId, Patch("RowDef", own.RowDefId, "StyleId", null), 9, CancellationToken.None);
        }

        Assert.Null((await ReadAsync(own)).RowStyle);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task PATCH_задає_стиль_шапки_таблиці()
    {
        var own = await BareVersionAsync();

        await using (var db = Context())
        {
            await PatchHandler(db).PatchAsync(
                own.VersionId, Patch("TableDef", own.TableDefId, "HeaderStyleId", own.StyleId), 9,
                CancellationToken.None);
        }

        Assert.Equal(own.StyleId, (await ReadAsync(own)).Header);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task PATCH_стилю_чужої_версії_для_рядка_і_шапки_відхиляється_422()
    {
        var own = await BareVersionAsync();
        var foreign = await BareVersionAsync();

        await using (var db = Context())
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => PatchHandler(db).PatchAsync(
                    own.VersionId, Patch("RowDef", own.RowDefId, "StyleId", foreign.StyleId), 9,
                    CancellationToken.None));
            Assert.Equal("err.ECR-TMPL-0422.presentationValueInvalid", error.Details!["messageKey"]);
        }

        await using (var db = Context())
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => PatchHandler(db).PatchAsync(
                    own.VersionId, Patch("TableDef", own.TableDefId, "HeaderStyleId", foreign.StyleId), 9,
                    CancellationToken.None));
            Assert.Equal("HeaderStyleId", error.Details!["field"]);
        }

        var state = await ReadAsync(own);
        Assert.Null(state.RowStyle);
        Assert.Null(state.Header);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static string Patch(string entity, int id, string field, int? value)
        => $$"""[{"entityType":"{{entity}}","entityId":{{id}},"field":"{{field}}","value":{{(value is null ? "null" : $"\"{value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"")}}}]""";

    private PatchPresentationHandler PatchHandler(EcrDbContext db)
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        return new PatchPresentationHandler(
            new Repository<TemplateVersion, int>(db), new TemplateVersionStore(db), new ChangeClassifier(), _cache,
            new AuditWriter(db), new UnitOfWork(db), new TestClock(Now), _access, _user, new StyleCatalog(db));
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private async Task<(int? RowStyle, int? Header)> ReadAsync(Stand stand)
    {
        await using var fresh = Context();
        var row = await fresh.RowDefs.AsNoTracking().SingleAsync(r => r.Id == stand.RowDefId);
        var table = await fresh.TableDefs.AsNoTracking().SingleAsync(t => t.Id == stand.TableDefId);
        return (row.StyleId, table.HeaderStyleId);
    }

    private async Task<Stand> BareVersionAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"RS{tag}"), Name($"Template {tag}"), 1, Now);
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

        var row = new RowDef(table.Id, RowKey.Create("R1"), 0, Name("Row"), RowKind.Item);
        db.RowDefs.Add(row);
        await db.SaveChangesAsync();

        return new Stand(version.Id, table.Id, row.Id, style.Id);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Stand(int VersionId, int TableDefId, int RowDefId, int StyleId);
}
