// tests/Ecr.Application.Tests/Documents/CalculationRejectionReasonsTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ 10.11 (doc 95, P1-V9-0093): рядок T3_00 закрило правило методології, але правило категорії
/// (<c>!ECW_Location = !ECW_RepairStatus + '_' + !ECW_Category</c>) не дало ключа — порожня «Overhauled engines».
/// Панель казала «none» без причини. Тепер <c>GET …/calculation-issues</c> для проблеми <c>CategoryRuleRejected</c>
/// називає правило, що закрило рядок, і колонку, яку треба заповнити (редаговану, а не обчислювану з неї).
/// </summary>
/// <remarks>
/// Мутації: не мапити обчислювану колонку на джерело її формули — червоніє перший тест (назветься «Overhauled»);
/// прибрати перевірку <c>canReadColumn</c> — червоніє «прихована колонка».
/// </remarks>
public sealed class CalculationRejectionReasonsTests
{
    private const long Document = 95;
    private const int Period = 202610;
    private const int MethodologyId = 2;
    private const long Instance = 55527;
    private const long RowId = 667208;
    private const string RowKey = "2ac8045fc3d5422089751775b45be468";
    private static readonly DateTime Now = new(2026, 11, 10, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Відхилений_рядок_називає_правило_і_порожню_редаговану_колонку()
    {
        var result = await new World().Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ICalculationIssueStore.CategoryRuleRejected, item.Kind);
        Assert.Equal("R_C05_01_01", item.RuleCode);
        var column = Assert.Single(item.Columns!);
        Assert.Equal("Land_OverhauledEnginesRef", column.Code);
        Assert.Null(item.CurrentValues);
        Assert.Null(result.Tables);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Прихована_колонка_не_називається_а_правило_лишається()
    {
        var result = await new World { HideRef = true }.Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("R_C05_01_01", item.RuleCode);
        Assert.Null(item.Columns);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Заповнена_колонка_не_називається()
    {
        var result = await new World { RefFilled = true }.Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        Assert.Null(Assert.Single(result.Items).Columns);
    }

    /// <summary>
    /// ⚡ 10.11 (людина: «продуктивність на великих навантаженнях»): 450 відхилених рядків однієї таблиці — комірки
    /// читаються пачками по <c>ReadChunkRows</c> рядків (3 звернення), а не по зверненню на рядок (450).
    /// Мутація: читати комірки в циклі по проблемі (як було) — 450 викликів, тест червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Комірки_відхилених_рядків_читаються_пачками_а_не_по_рядку()
    {
        var world = new World { Rows = 450 };

        var result = await world.Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        Assert.Equal(450, result.Items.Count);
        Assert.All(result.Items, i => Assert.Equal("R_C05_01_01", i.RuleCode));
        Assert.All(result.Items, i => Assert.Equal("Land_OverhauledEnginesRef", Assert.Single(i.Columns!).Code));
        await world.Cells.Received(3).ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AN-164 (doc 96, LTD-A41): рядок, закритий правилом, але без входів формул (<c>MissingInputs</c>), називає правило
    /// й порожні колонки, які читають УСІ формули версії, — і ті, яких правило категорії не згадує (<c>Land_Consumption</c>).
    /// Мутація: для <c>MissingInputs</c> брати аргументи лише правила категорії — <c>Land_Consumption</c> не названо, тест червоний.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядок_без_входів_називає_правило_і_порожні_колонки_всіх_формул()
    {
        var result = await new World { Kind = ICalculationIssueStore.MissingInputs }.Handler()
            .HandleAsync(Document, Period, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ICalculationIssueStore.MissingInputs, item.Kind);
        Assert.Equal("R_C05_01_01", item.RuleCode);
        Assert.Equal(["Land_OverhauledEnginesRef", "Land_Consumption"], item.Columns!.Select(c => c.Code));
        Assert.Null(item.CurrentValues);
        Assert.Null(result.Tables);
    }

    private sealed class World
    {
        public string Kind { get; init; } = ICalculationIssueStore.CategoryRuleRejected;

        public bool HideRef { get; init; }

        public bool RefFilled { get; init; }

        /// <summary>Скільки відхилених рядків у таблиці (перший — <see cref="RowKey"/>/<see cref="RowId"/>).</summary>
        public int Rows { get; init; } = 1;

        public ICellStore Cells { get; } = Substitute.For<ICellStore>();

        private string KeyOf(int i) => i == 0 ? RowKey : $"k{i}";

        private long IdOf(int i) => RowId + i;

        public GetCalculationIssuesHandler Handler()
        {
            var template = new TemplateBuilder { TemplateVersionId = 9 };
            var sheet = template.Sheet("S03_STATIONARY_EQUIPMENT");
            var table = template.Table(sheet, "T3_00", TableRowMode.Dynamic, TableLayoutKind.PerPeriodInstance);
            var status = template.Column(table, "Land_Status", CellDataType.String);
            var fuel = template.Column(table, "Land_TypeFuel", CellDataType.String);
            var power = template.Column(table, "Land_Power");
            var overhauledRef = template.Column(table, "Land_OverhauledEnginesRef", CellDataType.String);
            var overhauled = template.Column(table, "Land_OverhauledEngines", CellDataType.String);
            template.Formula(table, "REGFIELD([Land_OverhauledEnginesRef], 'NAME')", column: overhauled);
            _ = template.Column(table, "Land_Consumption");
            var snapshot = template.Build();

            var builder = new AccessBuilder { UserId = 9 }
                .Permission(GetCalculationIssuesHandler.Permission)
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
            if (HideRef)
            {
                builder.Deny(ResourceKind.Column, overhauledRef.Id);
            }

            var access = Substitute.For<IAccessDecisionService>();
            access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
            access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(EditDecision.Allow());
            access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

            var issues = Substitute.For<ICalculationIssueStore>();
            issues.ListAsync(Document, Period, null, Arg.Any<CancellationToken>())
                  .Returns([.. Enumerable.Range(0, Rows).Select(i => new CalculationIssueRow(
                      table.Id, "T3_00", null, i + 1, KeyOf(i), Kind, "ECW_C05_01", Now,
                      MethodologyId))]);

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
                version.AddFormula(EcrCode.Create("ECW_Tons"), "@Land_Consumption? * CST.K", FormulaResultType.Number, null),
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
            periods.FindPeriodBoundsAsync(Document, Period, Arg.Any<CancellationToken>())
                   .Returns(new PeriodBounds(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));

            var rowStore = Substitute.For<IRowStore>();
            rowStore.GetTableInstancesAsync(Document, new PeriodKey(Period), Arg.Any<CancellationToken>())
                    .Returns([new TableInstanceRef(Instance, Document, table.Id, 9, Period)]);
            rowStore.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), new PeriodKey(Period), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
                    {
                        [Instance] = Enumerable.Range(0, Rows).ToDictionary(KeyOf, IdOf),
                    });

            var key = new PeriodKey(Period);
            var values = new Dictionary<CellAddress, CellValueData>();
            for (var i = 0; i < Rows; i++)
            {
                values[new CellAddress(key, IdOf(i), status.Id)] = new() { ValueString = "1" };
                values[new CellAddress(key, IdOf(i), fuel.Id)] = new() { ValueString = "36" };
                values[new CellAddress(key, IdOf(i), power.Id)] = new() { ValueNumeric = 22m };
                if (RefFilled)
                {
                    values[new CellAddress(key, IdOf(i), overhauledRef.Id)] = new() { ValueString = "48" };
                }
            }

            var cells = Cells;
            cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
                 .Returns(call => (IReadOnlyDictionary<CellAddress, CellValueData>)call.Arg<IReadOnlyCollection<CellAddress>>()
                     .Where(values.ContainsKey).ToDictionary(a => a, a => values[a]));

            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(9, Arg.Any<CancellationToken>()).Returns(snapshot);

            return new GetCalculationIssuesHandler(
                issues, access, user, rowStore, cells, metadata, periods, methodologies,
                columnMapper: null, formulaEngine: new RealFormulaEngine());
        }
    }
}
