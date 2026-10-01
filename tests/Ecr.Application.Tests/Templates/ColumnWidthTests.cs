using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Типова ширина колонки <c>ColumnDef.WidthPx</c> (D-234, ФВ-2.7) — на
/// <b>реальному</b> SQL Server: поле презентаційне, тож правиться «на льоту»
/// і в опублікованій версії; межі 40..800; <c>null</c> скидає до типової.
/// </summary>
[Collection("SqlServer")]
public sealed class ColumnWidthTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string WidthPatch(int columnDefId, string? value)
        => $$"""[{"entityType":"ColumnDef","entityId":{{columnDefId}},"field":"WidthPx","value":{{(value is null ? "null" : $"\"{value}\"")}}}]""";

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Ecr.Application.Ports.IMetadataCache _cache = Substitute.For<Ecr.Application.Ports.IMetadataCache>();

    [Theory]
    [InlineData(40)]
    [InlineData(300)]
    [InlineData(800)]
    [InlineData(null)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Домен_приймає_ширину_в_межах_і_null(int? width)
    {
        var column = new ColumnDef(1, EcrCode.Create("Jan"), Name("Jan"), 1, CellDataType.Decimal);

        column.SetWidth(width);

        Assert.Equal(width, column.WidthPx);
    }

    [Theory]
    [InlineData(39)]
    [InlineData(801)]
    [InlineData(0)]
    [InlineData(-5)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Домен_відхиляє_ширину_поза_межами_422_з_ключем(int width)
    {
        var column = new ColumnDef(1, EcrCode.Create("Jan"), Name("Jan"), 1, CellDataType.Decimal);

        var ex = Assert.Throws<DomainException>(() => column.SetWidth(width));

        Assert.Equal("ECR-TMPL-0422", ex.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.widthOutOfRange", ex.Details?["messageKey"]);
        Assert.Null(column.WidthPx);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Патч_ширини_у_чернетці_пишеться_в_базу_а_null_скидає_її()
    {
        var version = await BareVersionAsync();

        await using (var db = Context())
        {
            await Handler(db).PatchAsync(version.VersionId, WidthPatch(version.ColumnDefId, "320"), 9, CancellationToken.None);
        }

        Assert.Equal(320, await WidthAsync(version.ColumnDefId));

        await using (var db = Context())
        {
            await Handler(db).PatchAsync(version.VersionId, WidthPatch(version.ColumnDefId, null), 9, CancellationToken.None);
        }

        Assert.Null(await WidthAsync(version.ColumnDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Ширина_правиться_й_в_опублікованій_версії_бо_це_презентація_а_тригер_її_пропускає()
    {
        var version = await BareVersionAsync();
        await SetStatusAsync(version.VersionId, 1); // Published

        Assert.Equal(
            ChangeClass.Presentation,
            new ChangeClassifier().Classify("ColumnDef", "WidthPx", hasDocuments: true));

        await using (var db = Context())
        {
            await Handler(db).PatchAsync(version.VersionId, WidthPatch(version.ColumnDefId, "250"), 9, CancellationToken.None);
        }

        Assert.Equal(250, await WidthAsync(version.ColumnDefId));

        // Той самий UPDATE повз застосунок: `TR_ColumnDef_Immutable` не бачить у ширині структурної зміни.
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cfg.ColumnDef SET WidthPx = 410 WHERE Id = @id";
        command.Parameters.AddWithValue("@id", version.ColumnDefId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        Assert.Equal(410, await WidthAsync(version.ColumnDefId));
    }

    [Theory]
    [InlineData("39")]
    [InlineData("801")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Ширина_поза_межами_у_патчі_відхиляється_422_і_нічого_не_міняє(string value)
    {
        var version = await BareVersionAsync();

        await using var db = Context();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler(db).PatchAsync(version.VersionId, WidthPatch(version.ColumnDefId, value), 9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", ex.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.widthOutOfRange", ex.Details?["messageKey"]);
        Assert.Null(await WidthAsync(version.ColumnDefId));

        await using var fresh = Context();
        Assert.Equal(0, (await fresh.TemplateVersions.AsNoTracking()
            .SingleAsync(v => v.Id == version.VersionId)).PresentationRevision);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Збереження_колонки_PUT_несе_ширину_і_відхиляє_межі()
    {
        var version = await BareVersionAsync();
        var save = new SaveColumnDefCommand(
            new Dictionary<string, string> { ["en"] = "Jan" }, null, CellDataType.Decimal,
            false, false, false, null, null, null, null, null, null, null, null, WidthPx: 275);

        await using (var db = Context())
        {
            _user.UserId.Returns(9);
            _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
                .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

            var handler = new SaveColumnDefHandler(
                new TemplateVersionStore(db), new ChangeClassifier(), _cache, new AuditWriter(db),
                new UnitOfWork(db), new TestClock(Now), _access, _user,
                Substitute.For<Ecr.Application.Ports.IUnitCatalog>());

            var dto = await handler.HandleAsync(version.VersionId, version.TableDefId, "Jan", save, CancellationToken.None);
            Assert.Equal(275, dto.WidthPx);
        }

        Assert.Equal(275, await WidthAsync(version.ColumnDefId));

        await using var tooWide = Context();
        var bad = save with { WidthPx = 801 };
        var ex = await Assert.ThrowsAsync<DomainException>(() => new SaveColumnDefHandler(
                new TemplateVersionStore(tooWide), new ChangeClassifier(), _cache, new AuditWriter(tooWide),
                new UnitOfWork(tooWide), new TestClock(Now), _access, _user,
                Substitute.For<Ecr.Application.Ports.IUnitCatalog>())
            .HandleAsync(version.VersionId, version.TableDefId, "Jan", bad, CancellationToken.None));
        Assert.Equal("err.ECR-TMPL-0422.widthOutOfRange", ex.Details?["messageKey"]);
        Assert.Equal(275, await WidthAsync(version.ColumnDefId));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private PatchPresentationHandler Handler(EcrDbContext db)
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        return new PatchPresentationHandler(
            new Repository<TemplateVersion, int>(db),
            new TemplateVersionStore(db),
            new ChangeClassifier(),
            _cache,
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now),
            _access,
            _user);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private async Task<int?> WidthAsync(int columnDefId)
    {
        await using var fresh = Context();
        return (await fresh.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == columnDefId)).WidthPx;
    }

    private async Task SetStatusAsync(int versionId, int status)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE cfg.TemplateVersion SET Status = @s, PublishedAt = SYSUTCDATETIME(), PublishedByUserId = 1 WHERE Id = @id";
        command.Parameters.AddWithValue("@s", status);
        command.Parameters.AddWithValue("@id", versionId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<BareVersion> BareVersionAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"CW{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(table.Id, EcrCode.Create("Jan"), Name("Jan"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();

        return new BareVersion(version.Id, table.Id, column.Id);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record BareVersion(int VersionId, int TableDefId, int ColumnDefId);
}

