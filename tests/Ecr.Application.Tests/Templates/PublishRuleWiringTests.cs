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
/// Підключення перевірок правил (<c>PublishChecks.CheckRules</c>) у
/// <c>PublishTemplateVersionHandler</c>: суперечливі рівні (<c>ФВ-5.10</c>) і
/// обов'язкова колонка без покриття (<c>ФВ-5.11</c>). На <b>реальному</b> SQL Server.
/// </summary>
/// <remarks>
/// ⛔ <c>PublishRuleCoverageTests</c> доводить саму функцію <c>CheckRules</c>, але
/// не те, що ОБРОБНИК її викликає: видалення рядка
/// <c>diagnostics = [.. diagnostics, .. PublishChecks.CheckRules(version)]</c> нічого
/// не червоніло (звірка вимог 2026-09-30, №13). Тут — справжній обробник зі
/// справжніми складниками (той самий прийом, що <c>ValidateExpressionTests</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishRuleWiringTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.11")]
    public async Task Обовязкова_колонка_без_правила_відхиляє_публікацію_кодом_4225()
    {
        // Обов'язкова колонка, яку ніхто не перевіряє і не рахує.
        var version = await ArrangeAsync(requiredWithoutRule: true, conflictingRules: false);

        var codes = await PublishCodesAsync(version);

        Assert.Contains("ECR-TMPL-4225", codes);
        Assert.DoesNotContain("ECR-TMPL-4224", codes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.10")]
    public async Task Правила_однієї_області_з_різними_рівнями_відхиляють_публікацію_кодом_4224()
    {
        var version = await ArrangeAsync(requiredWithoutRule: false, conflictingRules: true);

        var codes = await PublishCodesAsync(version);

        Assert.Contains("ECR-TMPL-4224", codes);
        Assert.DoesNotContain("ECR-TMPL-4225", codes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.10")]
    public async Task Без_дефектів_правил_публікація_цих_кодів_не_дає()
    {
        // Контроль: доводить, що два тести вище залежать саме від дефекту правил.
        var version = await ArrangeAsync(requiredWithoutRule: false, conflictingRules: false);

        var codes = await PublishCodesAsync(version);

        Assert.DoesNotContain("ECR-TMPL-4224", codes);
        Assert.DoesNotContain("ECR-TMPL-4225", codes);
    }

    /// <summary>Коди діагностик відмови публікації; порожньо, якщо публікація пройшла.</summary>
    private async Task<IReadOnlyList<string>> PublishCodesAsync(int versionId)
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
            return [.. ((IEnumerable<DiagnosticInfo>)error.Details!["diagnostics"]!).Select(d => d.Code)];
        }
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Чернетка з однією таблицею, колонкою <c>Cap</c> і, за потреби, дефектами правил.</summary>
    private async Task<int> ArrangeAsync(bool requiredWithoutRule, bool conflictingRules)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"PR{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Water"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"Main{tag}"), Name("Main"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var cap = new ColumnDef(table.Id, EcrCode.Create("Cap"), Name("Cap"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(cap);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("7001001"), 1, Name("Row"), RowKind.Item));

        ColumnDef? required = null;
        if (requiredWithoutRule)
        {
            required = new ColumnDef(table.Id, EcrCode.Create("Req"), Name("Req"), 2, CellDataType.Decimal);
            required.SetRequired(true);
            db.ColumnDefs.Add(required);
        }

        await db.SaveChangesAsync();

        // Правила чіпляються НАВІГАЦІЄЮ `AddValidationRule` — як бойовий шлях і як
        // `PublishRuleCoverageTests` (подвійний зовнішній ключ, див. `Q-163`).
        if (conflictingRules)
        {
            table.AddValidationRule(Rule(table, "R1", ValidationSeverity.Error, cap.Id));
            table.AddValidationRule(Rule(table, "R2", ValidationSeverity.Warning, cap.Id));
            await db.SaveChangesAsync();
        }

        return version.Id;
    }

    private static ValidationRule Rule(TableDef table, string code, ValidationSeverity severity, int columnDefId)
        => new(table.Id, EcrCode.Create(code), severity, scope: 0, "1 = 1", Name(code), columnDefId);
}
