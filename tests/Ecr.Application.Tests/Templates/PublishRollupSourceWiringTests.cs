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
/// Проводка «приймач активного Rollup — джерело значення колонки <c>Formula</c>» у
/// <c>PublishTemplateVersionHandler</c> (FP-5, <c>ECR-TMPL-4226</c>). На <b>реальному</b> SQL Server.
/// </summary>
/// <remarks>
/// ⛔ <c>PublishChecksTests.Приймач_активного_Rollup…</c> доводить чисті функції
/// (<c>RollupTargetColumnIds</c>, <c>CheckStructure</c>), але не те, що ОБРОБНИК їх з'єднує:
/// прибрати в обробнику об'єднання <c>boundColumnIds ∪ rollupTargets</c> і лишити
/// <c>CheckStructure(version, boundColumnIds)</c> — жоден чистий тест не червоніє. Тут — справжній
/// обробник зі справжнім сховищем зв'язків; відмінність між сценаріями — лише наявність і стан
/// Rollup-зв'язку (прив'язки методології немає в жодному).
/// <para>
/// Мутація: у <c>PublishTemplateVersionHandler</c> передати в <c>CheckStructure</c> самі
/// <c>boundColumnIds</c> — червоніє <c>Приймач_активного_Rollup_без_прив_язки…</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishRollupSourceWiringTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string Map = """{"sourceColumn":"Amount","targetColumn":"Total","aggregate":"sum"}""";

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Приймач_активного_Rollup_без_прив_язки_не_дає_4226()
    {
        var version = await ArrangeAsync(Relation.Active);

        var codes = await PublishCodesAsync(version);

        Assert.DoesNotContain("ECR-TMPL-4226", codes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Та_сама_колонка_без_Rollup_зв_язку_дає_4226()
    {
        var version = await ArrangeAsync(Relation.None);

        var codes = await PublishCodesAsync(version);

        Assert.Contains("ECR-TMPL-4226", codes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Та_сама_колонка_з_вимкненим_Rollup_зв_язком_дає_4226()
    {
        var version = await ArrangeAsync(Relation.Disabled);

        var codes = await PublishCodesAsync(version);

        Assert.Contains("ECR-TMPL-4226", codes);
    }

    private enum Relation
    {
        None,
        Active,
        Disabled,
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

    /// <summary>
    /// Чернетка з двома таблицями: <c>Detail</c> (колонка <c>Amount</c>) і <c>Summary</c> (колонка
    /// <c>Total</c> типу <c>Formula</c> без формули й без прив'язки), та, за потреби, Rollup-зв'язком
    /// <c>Detail → Summary</c>.
    /// </summary>
    private async Task<int> ArrangeAsync(Relation relation)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"RS{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Water"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var detail = new TableDef(
            sheet.Id, EcrCode.Create($"Det{tag}"), Name("Detail"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        var summary = new TableDef(
            sheet.Id, EcrCode.Create($"Sum{tag}"), Name("Summary"), 2,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.AddRange(detail, summary);
        await db.SaveChangesAsync();

        db.ColumnDefs.Add(new ColumnDef(detail.Id, EcrCode.Create("Amount"), Name("Amount"), 1, CellDataType.Decimal));
        db.ColumnDefs.Add(new ColumnDef(summary.Id, EcrCode.Create("Total"), Name("Total"), 1, CellDataType.Formula));
        db.RowDefs.Add(new RowDef(detail.Id, RowKey.Create("7001001"), 1, Name("Row"), RowKind.Item));
        db.RowDefs.Add(new RowDef(summary.Id, RowKey.Create("7001002"), 1, Name("Row"), RowKind.Item));
        await db.SaveChangesAsync();

        if (relation != Relation.None)
        {
            var rollup = new TableRelationDef(
                EcrCode.Create($"RL{tag}"), detail.Id, summary.Id, TableRelationKind.Rollup, "{}");
            rollup.Update(
                detail.Id, summary.Id, TableRelationKind.Rollup, "{}", Map, 0,
                isActive: relation == Relation.Active);
            db.TableRelations.Add(rollup);
            await db.SaveChangesAsync();
        }

        return version.Id;
    }
}
