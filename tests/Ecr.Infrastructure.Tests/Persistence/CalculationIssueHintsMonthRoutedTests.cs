// tests/Ecr.Infrastructure.Tests/Persistence/CalculationIssueHintsMonthRoutedTests.cs
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B10 (клас RI1-B / Q-17): поточні значення рядка в підказці «що приймають правила»
/// (<c>GET /documents/{id}/calculation-issues</c>) — ЛОГІЧНИЙ зріз періоду. У таблиці «місяці в колонках» введене
/// значення не-місячної колонки живе в якорі року (202601); у не-власному місяці (202610) підказка має показати його,
/// а не порожнечу.
/// </summary>
/// <remarks>
/// Справжні <see cref="RowStore"/>, <see cref="MonthRoutedCellStore"/> над <see cref="NormalizedCellStore"/>,
/// <see cref="MonthColumnRouter"/> і <see cref="MetadataCache"/>; підмінені лише проблеми, методології, межі періоду і
/// рішення доступу (профіль будується <see cref="AccessBuilder"/>, Deny колонки — диференційно, без змін фільтрів).
/// Мутація (поточні значення знову через фізичне <c>ReadCellsAsync</c>): червоніє перший тест, решта зелені.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationIssueHintsMonthRoutedTests(SqlServerFixture sql) : IDisposable
{
    private const int Year = 2026;
    private const int ViewMonth = 10;
    private const int MethodologyId = 25;
    private const int UserId = 9;
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());
    private readonly TestDocumentBuilder _builder = new(sql.ConnectionString);

    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Маршрутизована_таблиця_у_не_власному_місяці_показує_значення_якоря()
    {
        var a = await ArrangeAsync(TableLayoutKind.MonthsInColumns);
        await PutAsync(a.Anchor.PeriodKey, a.Anchor.RowIds[0], a.Doc.TableDefId, a.Category, "Engine");

        var result = await HandleAsync(a, routing: true, denyCategory: false);

        Assert.NotNull(result.Tables);
        var item = Assert.Single(result.Items);
        Assert.Equal(["Engine"], item.CurrentValues);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Маршрутизована_таблиця_Deny_колонки_прибирає_значення_власника()
    {
        // Диференційно до першого тесту: ті самі дані й маршрутизація, різниться лише Deny колонки правила.
        var a = await ArrangeAsync(TableLayoutKind.MonthsInColumns);
        await PutAsync(a.Anchor.PeriodKey, a.Anchor.RowIds[0], a.Doc.TableDefId, a.Category, "Engine");

        var result = await HandleAsync(a, routing: true, denyCategory: true);

        Assert.Null(result.Tables);
        Assert.Null(Assert.Single(result.Items).CurrentValues);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Немаршрутизована_таблиця_показує_значення_свого_періоду_як_доти()
    {
        // Контроль: звичайна таблиця (PerPeriodInstance) — значення періоду перегляду; значення іншого періоду
        // (202601) у підказку 202610 не потрапляє, хоч маршрутизацію і ввімкнено.
        var a = await ArrangeAsync(TableLayoutKind.PerPeriodInstance);
        await PutAsync(a.Anchor.PeriodKey, a.Anchor.RowIds[0], a.Doc.TableDefId, a.Category, "Engine");
        await PutAsync(a.Doc.PeriodKey, a.Doc.RowIds[0], a.Doc.TableDefId, a.Category, "Own");

        var result = await HandleAsync(a, routing: true, denyCategory: false);

        Assert.Equal(["Own"], Assert.Single(result.Items).CurrentValues);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-17")]
    public async Task Маршрутизацію_вимкнено_значення_фізичне_як_доти()
    {
        // Контроль прапора: без маршрутизації значення якоря в 202610 не видно — поведінка до Q-17.
        var a = await ArrangeAsync(TableLayoutKind.MonthsInColumns);
        await PutAsync(a.Anchor.PeriodKey, a.Anchor.RowIds[0], a.Doc.TableDefId, a.Category, "Engine");

        var result = await HandleAsync(a, routing: false, denyCategory: false);

        Assert.Equal([string.Empty], Assert.Single(result.Items).CurrentValues);
    }

    private sealed record Arranged(TestDocument Doc, TestPeriodInstance Anchor, int Category, string RowKey);

    private async Task<Arranged> ArrangeAsync(TableLayoutKind layout)
    {
        var doc = await _builder.BuildAsync(
            periodKey: (Year * 100) + ViewMonth,
            columnCount: 2,
            rowCount: 2,
            layoutKind: layout,
            monthColumns: layout == TableLayoutKind.MonthsInColumns ? 12 : 0);
        var anchor = await _builder.AddPeriodInstanceAsync(doc, new PeriodKey((Year * 100) + 1));

        // Колонка 1 будівника — текстова, не-місячна: власник у маршрутизованій таблиці — якір року.
        return new Arranged(doc, anchor, doc.ColumnDefIds[0], anchor.RowKeys[0]);
    }

    private async Task PutAsync(PeriodKey period, long rowId, int tableDefId, int columnDefId, string value)
    {
        await using var db = _builder.CreateContext();
        db.CellValues.Add(new CellValue(
            new CellAddress(period, rowId, columnDefId),
            tableDefId,
            new CellValueData { ValueString = value }));
        await db.SaveChangesAsync();
    }

    private static IConfiguration Config(bool routing)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = routing ? "true" : "false" })
            .Build();

    private async Task<CalculationIssuesDto> HandleAsync(Arranged a, bool routing, bool denyCategory)
    {
        await using var db = _builder.CreateContext();
        var metadata = new MetadataCache(_memory, db);
        var snapshot = await metadata.GetAsync(a.Doc.TemplateVersionId, CancellationToken.None);
        var view = a.Doc.PeriodKey.Value;

        var builder = new AccessBuilder { UserId = UserId }
            .Permission(GetCalculationIssuesHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (denyCategory)
        {
            builder.Deny(ResourceKind.Column, a.Category);
        }

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(builder.Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), a.Doc.DocumentId, Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), a.Doc.DocumentId, Arg.Any<CancellationToken>())
              .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

        var issues = Substitute.For<ICalculationIssueStore>();
        issues.ListAsync(a.Doc.DocumentId, view, null, Arg.Any<CancellationToken>())
              .Returns([new CalculationIssueRow(a.Doc.TableDefId, "TBL", null, 1, a.RowKey, "NoMatchingRule", "M_B10", Now)]);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var version = new MethodologyVersion(MethodologyId, "1.0", CalculationLevel.Configuration, 1, Now);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(version, 11);
        var rules = new List<MethodologyRule>
        {
            version.AddRule(EcrCode.Create("R1"), $$"""{"{{a.Category}}":"Turbine"}""", 1),
        };
        version.Publish(publishedByUserId: 2, "тест", new DateOnly(2026, 1, 1), testsPassed: true, Now);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns([MethodologyId]);
        methodologies.GetPublishedVersionsOfAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns([version]);
        methodologies.GetRulesOfVersionsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(rules);
        methodologies.GetVersionLabelsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(new Dictionary<int, MethodologyVersionLabel> { [11] = new(11, "M_B10", "1.0") });

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(a.Doc.DocumentId, view, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(Year, ViewMonth, 1), new DateOnly(Year, ViewMonth, 31)));

        var rowStore = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now));
        var cells = new MonthRoutedCellStore(
            new NormalizedCellStore(db), new MonthColumnRouter(db, Config(routing)), metadata, db);

        var handler = new GetCalculationIssuesHandler(issues, access, user, rowStore, cells, metadata, periods, methodologies);
        return await handler.HandleAsync(a.Doc.DocumentId, view, null, CancellationToken.None);
    }
}
