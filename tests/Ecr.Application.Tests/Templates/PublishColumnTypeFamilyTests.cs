using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ✎ R-3 (аудит 2026-11, рішення PO 11.11, D-478): ОБРОБНИК публікації відхиляє версію, у якій код колонки змінює
/// родину типу зберігання щодо опублікованих/застарілих версій того самого шаблону. На <b>реальному</b> SQL Server —
/// разом зі справжнім запитом <c>TemplateVersionStore.ListPublishedColumnTypesAsync</c>.
/// </summary>
/// <remarks>
/// Мутація: прибрати виклик <c>CheckColumnTypeFamilies</c> з <c>PublishTemplateVersionHandler</c> — перший тест
/// червоний (публікація проходить); прибрати фільтр статусу в запиті сховища — тест чернетки червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishColumnTypeFamilyTests(SqlServerFixture sql)
{
    private const string FamilyKey = "err.ECR-TMPL-0422.columnTypeFamilyChanged";
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Код_колонки_що_змінив_родину_типу_відхиляє_публікацію()
    {
        var (templateId, tag) = await TemplateAsync();
        await VersionAsync(templateId, tag, "1.0.0.0", CellDataType.String, publish: true);
        var draft = await VersionAsync(templateId, tag, "2.0.0.0", CellDataType.Decimal, publish: false);

        var diagnostics = await PublishAsync(draft);

        var family = Assert.Single(diagnostics, d => d.MessageKey == FamilyKey);
        Assert.Equal($"S{tag}.T{tag}.X", family.MessageParams!["columnPath"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_в_межах_родини_публікується()
    {
        var (templateId, tag) = await TemplateAsync();
        await VersionAsync(templateId, tag, "1.0.0.0", CellDataType.Decimal, publish: true);
        var draft = await VersionAsync(templateId, tag, "2.0.0.0", CellDataType.Int, publish: false);

        Assert.DoesNotContain(await PublishAsync(draft), d => d.MessageKey == FamilyKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Інші_чернетки_шаблону_не_рахуються()
    {
        var (templateId, tag) = await TemplateAsync();
        await VersionAsync(templateId, tag, "1.0.0.0", CellDataType.String, publish: false);
        var draft = await VersionAsync(templateId, tag, "2.0.0.0", CellDataType.Decimal, publish: false);

        Assert.DoesNotContain(await PublishAsync(draft), d => d.MessageKey == FamilyKey);
    }

    /// <summary>Діагностики відмови публікації; порожньо, якщо публікація пройшла.</summary>
    private async Task<IReadOnlyList<DiagnosticInfo>> PublishAsync(int versionId)
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Publish").Build());

        await using var db = Context();
        using var memory = new MemoryCache(new MemoryCacheOptions());

        var handler = new PublishTemplateVersionHandler(
            new Repository<TemplateVersion, int>(db),
            new TemplateVersionStore(db),
            new RealFormulaEngine(),
            new CalculationBindingStore(db),
            new MetadataCache(memory, db),
            new UnitCatalog(db),
            _access,
            _user,
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now),
            new Ecr.Infrastructure.Reporting.ReportViewGenerator(db));

        try
        {
            await handler.PublishAsync(versionId, userId: 9, reason: "Тест", CancellationToken.None);
            return [];
        }
        catch (BusinessRuleException error)
        {
            return [.. (IEnumerable<DiagnosticInfo>)error.Details!["diagnostics"]!];
        }
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<(int TemplateId, string Tag)> TemplateAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();
        var template = new Template(EcrCode.Create($"TF{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();
        return (template.Id, tag);
    }

    /// <summary>Версія з аркушем <c>S{tag}</c>, таблицею <c>T{tag}</c> і колонкою <c>X</c> заданого типу.</summary>
    private async Task<int> VersionAsync(int templateId, string tag, string number, CellDataType type, bool publish)
    {
        await using var db = Context();

        var version = new TemplateVersion(templateId, number, 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(table.Id, EcrCode.Create("X"), Name("X"), 1, type));
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("7001001"), 1, Name("Row"), RowKind.Item));
        await db.SaveChangesAsync();

        if (publish)
        {
            // Пряма зміна стану — як дані, опубліковані до D-478 (обхід перевірки).
            version.Publish(1, Now);
            await db.SaveChangesAsync();
        }

        return version.Id;
    }
}
