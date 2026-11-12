// tests/Ecr.Application.Tests/Documents/CalculationRejectionReasonsMonthRoutedTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Q-17 (LAND-YEAR G5): причина відхилення правилом категорії (<c>GET …/calculation-issues</c>) читає ЛОГІЧНІ значення
/// рядка таблиці «місяці в колонках»: статус, ключі правил і входи, якими володіє якір року (202601), — з екземпляра
/// власника, а не порожні фізичні комірки жовтня.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Дефект: <c>CalculationRejectionReasons</c> читав фізичний <c>ICellStore.ReadCellsAsync</c> екземпляра перегляду. У
/// не-власному місяці ключі правил порожні — правило «не збігалося» (<c>ruleCode = null</c>), а заповнені у власника
/// колонки (<c>Land_Power</c>) називались порожніми. Той самий клас, що gate обов'язкових входів (5a3074b15).
/// </para>
/// <para>
/// Шлях: справжній <see cref="GetCalculationIssuesHandler"/> і справжні межі читання (<see cref="DocumentReadScope"/>
/// з профілю); роутер і сховища — підміни з тими самими відповідями, що дає <c>MonthColumnRouter</c> для doc 96.
/// </para>
/// <para>
/// ⚠ МУТАЦІЙНИЙ ДОКАЗ (локально, не в коміті): <c>PlanOwnerReadAsync</c> повертає <c>null</c> — червоніють
/// <see cref="Жовтень_бере_ключі_правила_з_якоря_а_застаріле_фізичне_затінене"/> і
/// <see cref="Deny_ключа_правила_не_бере_значення_власника_диференційно"/>.
/// </para>
/// </remarks>
public sealed class CalculationRejectionReasonsMonthRoutedTests
{
    private const long Document = 96;
    private const int Oct = 202610;
    private const int Jan = 202601;
    private const int MethodologyId = 2;
    private const long OctInstance = 56724;
    private const long JanInstance = 56001;
    private const long OctRow = 700100;
    private const long JanRow = 700001;
    private const string RowKey = "LTD-A01";
    private static readonly DateTime Now = new(2026, 11, 12, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "Q-17")]
    public async Task Жовтень_бере_ключі_правила_з_якоря_а_застаріле_фізичне_затінене()
    {
        var world = new World();

        var item = Assert.Single((await world.Handler().HandleAsync(Document, Oct, null, CancellationToken.None)).Items);

        // Ключі правила (статус 1, паливо 36) — з якоря; застаріле фізичне паливо 38 у жовтні затінене власником.
        Assert.Equal("R_C05_01_01", item.RuleCode);

        // Потужність заповнена у власника — не називається; Ref порожня і в нього — називається.
        Assert.Equal(["Land_OverhauledEnginesRef"], item.Columns!.Select(c => c.Code));

        // Комірки власника — тим самим пакетним читанням (одне звернення на таблицю).
        await world.Cells.Received(1).ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "Q-17")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Deny_ключа_правила_не_бере_значення_власника_диференційно()
    {
        // Той самий запит, різниться лише Deny колонки Land_TypeFuel (ключ правила, власник — якір).
        var allowed = Assert.Single((await new World().Handler().HandleAsync(Document, Oct, null, CancellationToken.None)).Items);
        var denied = Assert.Single(
            (await new World { DenyFuel = true }.Handler().HandleAsync(Document, Oct, null, CancellationToken.None)).Items);

        Assert.Equal("R_C05_01_01", allowed.RuleCode);

        // Значення власника прихованої колонки не береться (і фізичне застаріле — затінене): правило не збігається.
        Assert.Null(denied.RuleCode);
    }

    /// <summary>
    /// Контроль: маршрутизація вимкнена — читання фізичне, як до фіксу: роутер і сховище рядків не питаються, комірки —
    /// одним зверненням лише за адресами жовтня.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "Q-17")]
    public async Task Без_маршрутизації_читання_фізичне_як_було()
    {
        var world = new World { Routing = false };

        var item = Assert.Single((await world.Handler().HandleAsync(Document, Oct, null, CancellationToken.None)).Items);

        // Фізичні комірки жовтня: застаріле паливо 38 → правило R_C05_01_02; потужність порожня — називається.
        Assert.Equal("R_C05_01_02", item.RuleCode);
        Assert.Contains(item.Columns!, c => c.Code == "Land_Power");

        _ = world.Router.DidNotReceive().AppliesToTable(Arg.Any<TableDef>());
        await world.Router.DidNotReceive().GetDocumentAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await world.Rows.DidNotReceive().GetTableInstancesOfTableInYearAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await world.Cells.Received(1).ReadCellsAsync(
            Arg.Is<IReadOnlyCollection<CellAddress>>(a => a.All(x => x.PeriodKey.Value == Oct)), Arg.Any<CancellationToken>());
    }

    private sealed class World
    {
        public bool Routing { get; init; } = true;

        public bool DenyFuel { get; init; }

        public ICellStore Cells { get; } = Substitute.For<ICellStore>();

        public IRowStore Rows { get; } = Substitute.For<IRowStore>();

        public IMonthColumnRouter Router { get; } = Substitute.For<IMonthColumnRouter>();

        public GetCalculationIssuesHandler Handler()
        {
            var template = new TemplateBuilder { TemplateVersionId = 9 };
            var sheet = template.Sheet("S03_STATIONARY_EQUIPMENT");
            var table = template.Table(sheet, "T3_00", TableRowMode.Dynamic, TableLayoutKind.MonthsInColumns);
            var status = template.Column(table, "Land_Status", CellDataType.String);
            var fuel = template.Column(table, "Land_TypeFuel", CellDataType.String);
            var power = template.Column(table, "Land_Power");
            var overhauledRef = template.Column(table, "Land_OverhauledEnginesRef", CellDataType.String);
            var overhauled = template.Column(table, "Land_OverhauledEngines", CellDataType.String);
            template.Formula(table, "REGFIELD([Land_OverhauledEnginesRef], 'NAME')", column: overhauled);
            var snapshot = template.Build();
            var tableDef = snapshot.Sheets.SelectMany(s => s.Tables).Single(t => t.Id == table.Id);

            var builder = new AccessBuilder { UserId = 9 }
                .Permission(GetCalculationIssuesHandler.Permission)
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
            if (DenyFuel)
            {
                builder.Deny(ResourceKind.Column, fuel.Id);
            }

            var access = Substitute.For<IAccessDecisionService>();
            access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
            access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(EditDecision.Allow());
            access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

            var issues = Substitute.For<ICalculationIssueStore>();
            issues.ListAsync(Document, Oct, null, Arg.Any<CancellationToken>())
                  .Returns([new CalculationIssueRow(
                      table.Id, "T3_00", null, 1, RowKey, ICalculationIssueStore.CategoryRuleRejected, "ECW_C05_01", Now,
                      MethodologyId)]);

            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(9);

            var version = new MethodologyVersion(MethodologyId, "V1", CalculationLevel.Configuration, 1, Now);
            typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(version, 2);
            var rules = new List<MethodologyRule>
            {
                version.AddRule(EcrCode.Create("R_C05_01_02"), $$"""{"{{fuel.Id}}":"38","{{status.Id}}":"1"}""", 20),
                version.AddRule(EcrCode.Create("R_C05_01_01"), $$"""{"{{fuel.Id}}":"36","{{status.Id}}":"1"}""", 10),
            };
            var formulas = new List<MethodologyFormula>
            {
                version.AddFormula(EcrCode.Create("ECW_Location"), "!ECW_RepairStatus + '_' + !ECW_Category", FormulaResultType.Text, null),
                version.AddFormula(
                    EcrCode.Create("ECW_RepairStatus"),
                    "if(@Land_OverhauledEngines = 'Not passed overhaul', 'Loc_BeforeMR', '')", FormulaResultType.Text, null),
                version.AddFormula(EcrCode.Create("ECW_Category"), "if(@Land_Power < 73.6, 'A', 'B')", FormulaResultType.Text, null),
            };
            version.Publish(publishedByUserId: 2, "тест", new DateOnly(2024, 1, 1), testsPassed: true, Now);

            var methodologies = Substitute.For<IMethodologyStore>();
            methodologies.GetPublishedVersionsOfAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                         .Returns([version]);
            methodologies.GetRulesAsync(2, Arg.Any<CancellationToken>()).Returns(rules);
            methodologies.GetFormulasAsync(2, Arg.Any<CancellationToken>()).Returns(formulas);
            methodologies.GetCategoryRuleAsync(2, Arg.Any<CancellationToken>()).Returns("!ECW_Location");
            methodologies.GetLibraryContentsAsync(2, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                         .Returns(Array.Empty<MethodologyLibraryContent>());

            var periods = Substitute.For<IPeriodStore>();
            periods.FindPeriodBoundsAsync(Document, Oct, Arg.Any<CancellationToken>())
                   .Returns(new PeriodBounds(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));

            var oct = new PeriodKey(Oct);
            var jan = new PeriodKey(Jan);
            Rows.GetTableInstancesAsync(Document, oct, Arg.Any<CancellationToken>())
                .Returns([new TableInstanceRef(OctInstance, Document, table.Id, 9, Oct)]);
            Rows.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), oct, Arg.Any<CancellationToken>())
                .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>> { [OctInstance] = new Dictionary<string, long> { [RowKey] = OctRow } });
            Rows.GetTableInstancesOfTableInYearAsync(Document, table.Id, 2026, Arg.Any<CancellationToken>())
                .Returns([
                    new TableInstanceRef(JanInstance, Document, table.Id, 9, Jan),
                    new TableInstanceRef(OctInstance, Document, table.Id, 9, Oct),
                ]);
            Rows.GetRowIdsAsync(JanInstance, jan, Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, long> { [RowKey] = JanRow });

            // Як MonthColumnRouter для перегляду жовтня: введені не-місячні колонки — власник якір 202601;
            // обчислювана (Land_OverhauledEngines) — у перегляді.
            Router.Enabled.Returns(Routing);
            Router.AppliesToTable(Arg.Any<TableDef>()).Returns(true);
            Router.GetDocumentAsync(Document, Arg.Any<CancellationToken>())
                  .Returns(new MonthRoutingDocument(Document, AccessBuilder.ProjectId, 9));
            Router.ForeignColumnsAsync(Arg.Any<MonthRoutingDocument>(), Arg.Any<TableDef>(), oct, Arg.Any<CancellationToken>())
                  .Returns([.. new[] { status, fuel, power, overhauledRef }
                      .Select(c => new ForeignColumn(tableDef.Columns.Single(x => x.Id == c.Id), jan))]);

            // Значення — у якорі; у жовтні фізично лише застаріле паливо 38 (до маршрутизації).
            var values = new Dictionary<CellAddress, CellValueData>
            {
                [new CellAddress(jan, JanRow, status.Id)] = new() { ValueString = "1" },
                [new CellAddress(jan, JanRow, fuel.Id)] = new() { ValueString = "36" },
                [new CellAddress(jan, JanRow, power.Id)] = new() { ValueNumeric = 22m },
                [new CellAddress(oct, OctRow, status.Id)] = new() { ValueString = "1" },
                [new CellAddress(oct, OctRow, fuel.Id)] = new() { ValueString = "38" },
            };

            Cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
                 .Returns(call => (IReadOnlyDictionary<CellAddress, CellValueData>)call.Arg<IReadOnlyCollection<CellAddress>>()
                     .Where(values.ContainsKey).ToDictionary(a => a, a => values[a]));

            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(9, Arg.Any<CancellationToken>()).Returns(snapshot);

            return new GetCalculationIssuesHandler(
                issues, access, user, Rows, Cells, metadata, periods, methodologies,
                columnMapper: null, formulaEngine: new RealFormulaEngine(), monthRouter: Router);
        }
    }
}
