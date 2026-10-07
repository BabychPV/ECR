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
/// Місяць колонки <c>ColumnDef.IsMonthColumn</c>/<c>MonthNumber</c> (PS-P1C, D-PS-1) —
/// домен і <c>PUT …/columns/{code}</c> на <b>реальному</b> SQL Server: без цього
/// <c>PeriodRuleFacts.ColumnMonthNumber</c> завжди <c>null</c>, а правила вікна
/// періоду (<c>SourceWindow</c>/<c>OutsidePermitWindow</c>) мертві.
/// </summary>
[Collection("SqlServer")]
public sealed class ColumnMonthTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly Ecr.Application.Ports.IMetadataCache _cache = Substitute.For<Ecr.Application.Ports.IMetadataCache>();

    private static ColumnDef NewColumn()
        => new(1, EcrCode.Create("Jan"), Name("Jan"), 1, CellDataType.Decimal);

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)7)]
    [InlineData((byte)12)]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Домен_приймає_місяць_1_12_разом_із_прапором(byte month)
    {
        var column = NewColumn();

        column.SetMonth(true, month);

        Assert.True(column.IsMonthColumn);
        Assert.Equal(month, column.MonthNumber);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Прапор_знятий_скидає_місяць_у_null()
    {
        var column = NewColumn();
        column.SetMonth(true, 3);

        column.SetMonth(false, null);

        Assert.False(column.IsMonthColumn);
        Assert.Null(column.MonthNumber);
    }

    [Theory]
    [InlineData(true, null, "monthColumnWithoutMonth")]
    [InlineData(true, (byte)0, "monthOutOfRange")]
    [InlineData(true, (byte)13, "monthOutOfRange")]
    [InlineData(false, (byte)5, "monthWithoutFlag")]
    [InlineData(false, (byte)13, "monthOutOfRange")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Домен_відхиляє_недопустиму_пару_422_з_ключем_і_нічого_не_міняє(bool flag, byte? month, string key)
    {
        var column = NewColumn();

        var ex = Assert.Throws<DomainException>(() => column.SetMonth(flag, month));

        Assert.Equal("ECR-TMPL-0422", ex.ErrorCode);
        Assert.Equal($"err.ECR-TMPL-0422.{key}", ex.Details?["messageKey"]);
        Assert.False(column.IsMonthColumn);
        Assert.Null(column.MonthNumber);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task PUT_колонки_пише_місяць_у_базу_повертає_його_і_знімається_повторним_PUT()
    {
        var version = await BareVersionAsync();
        var save = new SaveColumnDefCommand(
            new Dictionary<string, string> { ["en"] = "Jan" }, null, CellDataType.Decimal,
            false, false, false, null, null, null, null, null, null, null, null,
            IsMonthColumn: true, MonthNumber: 4);

        await using (var db = Context())
        {
            var dto = await Save(db).HandleAsync(version.VersionId, version.TableDefId, "Jan", save, CancellationToken.None);
            Assert.True(dto.IsMonthColumn);
            Assert.Equal((byte)4, dto.MonthNumber);
        }

        Assert.Equal((true, (byte?)4), await MonthAsync(version.ColumnDefId));

        await using (var db = Context())
        {
            await Save(db).HandleAsync(
                version.VersionId, version.TableDefId, "Jan",
                save with { IsMonthColumn = false, MonthNumber = null }, CancellationToken.None);
        }

        Assert.Equal((false, (byte?)null), await MonthAsync(version.ColumnDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task PUT_без_полів_місяця_не_стирає_збережений_місяць()
    {
        var version = await BareVersionAsync();
        var withMonth = new SaveColumnDefCommand(
            new Dictionary<string, string> { ["en"] = "Jan" }, null, CellDataType.Decimal,
            false, false, false, null, null, null, null, null, null, null, null,
            IsMonthColumn: true, MonthNumber: 6);

        await using (var db = Context())
        {
            await Save(db).HandleAsync(version.VersionId, version.TableDefId, "Jan", withMonth, CancellationToken.None);
        }

        // Старий клієнт / імпорт: поля місяця не прислано.
        await using (var db = Context())
        {
            await Save(db).HandleAsync(
                version.VersionId, version.TableDefId, "Jan",
                withMonth with { IsMonthColumn = null, MonthNumber = null, IsRequired = true }, CancellationToken.None);
        }

        Assert.Equal((true, (byte?)6), await MonthAsync(version.ColumnDefId));
    }

    [Theory]
    [InlineData(true, null, "monthColumnWithoutMonth")]
    [InlineData(true, (byte)13, "monthOutOfRange")]
    [InlineData(false, (byte)2, "monthWithoutFlag")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task PUT_колонки_з_недопустимим_місяцем_відхиляється_і_база_не_міняється(bool flag, byte? month, string key)
    {
        var version = await BareVersionAsync();
        var save = new SaveColumnDefCommand(
            new Dictionary<string, string> { ["en"] = "Jan" }, null, CellDataType.Decimal,
            false, false, false, null, null, null, null, null, null, null, null,
            IsMonthColumn: flag, MonthNumber: month);

        await using var db = Context();
        var ex = await Assert.ThrowsAsync<DomainException>(
            () => Save(db).HandleAsync(version.VersionId, version.TableDefId, "Jan", save, CancellationToken.None));

        Assert.Equal($"err.ECR-TMPL-0422.{key}", ex.Details?["messageKey"]);
        Assert.Equal((false, (byte?)null), await MonthAsync(version.ColumnDefId));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private SaveColumnDefHandler Save(EcrDbContext db)
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        return new SaveColumnDefHandler(
            new TemplateVersionStore(db), new ChangeClassifier(), _cache, new AuditWriter(db),
            new UnitOfWork(db), new TestClock(Now), _access, _user,
            Substitute.For<Ecr.Application.Ports.IUnitCatalog>());
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private async Task<(bool, byte?)> MonthAsync(int columnDefId)
    {
        await using var fresh = Context();
        var c = await fresh.ColumnDefs.AsNoTracking().SingleAsync(x => x.Id == columnDefId);
        return (c.IsMonthColumn, c.MonthNumber);
    }

    private async Task<BareVersion> BareVersionAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"CM{tag}"), Name($"Template {tag}"), 1, Now);
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

