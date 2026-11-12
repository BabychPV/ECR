// tests/Ecr.Application.Tests/Documents/CalculationRejectionReasonsRunArgumentsTests.cs
using Ecr.Application.Calculations;
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
/// AN-164b (аудит RC #825, doc 96): проблема <c>MissingInputs</c> називає САМЕ ті входи, яких бракувало рядку в прогоні
/// (профіль прогону), а не здогад за формулами. P2-a: рядок T7b_00 @202609 мав <c>columns = null</c>. P2-b: таблиця
/// «місяці в колонках» @202610 давала «Running hours per month» ×12 без місяця і «rule unknown».
/// </summary>
/// <remarks>
/// Мутації: читати фізичні комірки замість логічного зрізу (<c>ReadSliceAsync</c>) для «місяців у колонках» — червоніє
/// «правило з логічних значень»; не відсікати місячні колонки інших місяців — червоніє «одна колонка з місяцем»; брати
/// здогад за формулами замість профілю прогону — червоніє «вхід поза колонками»; прибрати <c>canReadColumn</c> —
/// червоніє «прихована місячна колонка».
/// </remarks>
public sealed class CalculationRejectionReasonsRunArgumentsTests
{
    private const long Document = 96;
    private const int Period = 202609;
    private const int MethodologyId = 7;
    private const long Instance = 7700;
    private const long RowId = 880001;
    private const string RowKey = "S1_6";
    private const string Rule = "R_C09_02_01_01";
    private static readonly DateTime Now = new(2026, 11, 12, 9, 41, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Місячна_таблиця_одна_колонка_з_місяцем_і_правило_з_логічних_значень()
    {
        var result = await new World { Arguments = ["Land_RunningHours"] }.Handler()
            .HandleAsync(Document, Period, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ICalculationIssueStore.MissingInputs, item.Kind);
        Assert.Equal(Rule, item.RuleCode);
        var column = Assert.Single(item.Columns!);
        Assert.Equal("Land_RunningHours_M09", column.Code);
        Assert.Equal("Land_RunningHours_M09 · Sep", column.NameL10n.Get("en"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Вхід_поза_колонками_таблиці_називається_кодом_без_заголовка()
    {
        var result = await new World { Arguments = ["Land_Consumption", "Land_Density"] }.Handler()
            .HandleAsync(Document, Period, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(Rule, item.RuleCode);
        Assert.Equal(["Land_Consumption", "Land_Density"], item.Columns!.Select(c => c.Code));
        Assert.Equal("Land_Consumption", item.Columns![0].NameL10n.Get("en"));
        Assert.Empty(item.Columns![1].NameL10n.Values);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Кожна_колонка_один_раз()
    {
        var result = await new World { Arguments = ["Land_RunningHours", "Land_RunningHours_M09", "land_runninghours", "X", "x"] }
            .Handler()
            .HandleAsync(Document, Period, null, CancellationToken.None);

        Assert.Equal(["Land_RunningHours_M09", "X"], Assert.Single(result.Items).Columns!.Select(c => c.Code));
    }

    /// <summary>⛔ Безпека: прихована колонка серед названих — не називається жодна (і входи теж), правило лишається.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Прихована_місячна_колонка_не_називається()
    {
        var result = await new World { Arguments = ["Land_RunningHours", "Land_Density"], HideMonth = true }.Handler()
            .HandleAsync(Document, Period, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(Rule, item.RuleCode);
        Assert.Null(item.Columns);
    }

    private sealed class World
    {
        public required IReadOnlyList<string> Arguments { get; init; }

        public bool HideMonth { get; init; }

        public GetCalculationIssuesHandler Handler()
        {
            var template = new TemplateBuilder { TemplateVersionId = 11 };
            var sheet = template.Sheet("S07_RUNNING");
            var table = template.Table(sheet, "T7b_00", TableRowMode.Dynamic, TableLayoutKind.MonthsInColumns);
            var status = template.Column(table, "Land_Status", CellDataType.String);
            var fuel = template.Column(table, "Land_TypeFuel", CellDataType.String);
            var months = new List<Ecr.Domain.Entities.Configuration.ColumnDef>();
            for (var m = 1; m <= 12; m++)
            {
                var month = template.Column(table, $"Land_RunningHours_M{m:00}");
                month.SetMonth(true, (byte)m);
                months.Add(month);
            }

            var hours = template.Column(table, "Land_RunningHours", CellDataType.Formula);
            template.Formula(table, string.Join(" + ", months.Select(c => $"[{c.Code}]")), column: hours);
            _ = template.Column(table, "Land_Consumption");
            var snapshot = template.Build();

            var builder = new AccessBuilder { UserId = 9 }
                .Permission(GetCalculationIssuesHandler.Permission)
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
            if (HideMonth)
            {
                builder.Deny(ResourceKind.Column, months[8].Id);
            }

            var access = Substitute.For<IAccessDecisionService>();
            access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
            access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(EditDecision.Allow());
            access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

            var issues = Substitute.For<ICalculationIssueStore>();
            issues.ListAsync(Document, Period, null, Arg.Any<CancellationToken>())
                  .Returns([new CalculationIssueRow(
                      table.Id, "T7b_00", null, 1, RowKey, ICalculationIssueStore.MissingInputs, "ECW_C09_02_01", Now,
                      MethodologyId)]);

            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(9);

            var version = new MethodologyVersion(MethodologyId, "V1", CalculationLevel.Configuration, 1, Now);
            typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(version, 3);
            var rules = new List<MethodologyRule>
            {
                version.AddRule(EcrCode.Create(Rule), $$"""{"{{fuel.Id}}":"36","{{status.Id}}":"1"}""", 10),
            };
            var formulas = new List<MethodologyFormula>
            {
                version.AddFormula(
                    EcrCode.Create("ECW_Tons"), "@Land_RunningHours * @Land_Consumption? * @Land_Density",
                    FormulaResultType.Number, null),
            };
            version.Publish(publishedByUserId: 2, "тест", new DateOnly(2024, 1, 1), testsPassed: true, Now);

            var methodologies = Substitute.For<IMethodologyStore>();
            methodologies.GetPublishedVersionsOfAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                         .Returns([version]);
            methodologies.GetRulesAsync(3, Arg.Any<CancellationToken>()).Returns(rules);
            methodologies.GetFormulasAsync(3, Arg.Any<CancellationToken>()).Returns(formulas);
            methodologies.GetLibraryContentsAsync(3, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                         .Returns(Array.Empty<MethodologyLibraryContent>());

            var periods = Substitute.For<IPeriodStore>();
            periods.FindPeriodBoundsAsync(Document, Period, Arg.Any<CancellationToken>())
                   .Returns(new PeriodBounds(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)));

            var rowStore = Substitute.For<IRowStore>();
            rowStore.GetTableInstancesAsync(Document, new PeriodKey(Period), Arg.Any<CancellationToken>())
                    .Returns([new TableInstanceRef(Instance, Document, table.Id, 11, Period)]);
            rowStore.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), new PeriodKey(Period), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
                    {
                        [Instance] = new Dictionary<string, long> { [RowKey] = RowId },
                    });

            // Фізично в екземплярі вересня введених комірок немає (власник — інший період, Q-17); логічний зріз (як у
            // прогоні) несе статус і паливо з власника, а серпень заповнений — він вересню не потрібен.
            var key = new PeriodKey(Period);
            var cells = Substitute.For<ICellStore>();
            cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<CellAddress, CellValueData>());
            cells.ReadSliceAsync(Instance, key, Arg.Any<CancellationToken>())
                 .Returns(
                 [
                     new CellRecord(new CellAddress(key, RowId, status.Id), table.Id, new CellValueData { ValueString = "1" }),
                     new CellRecord(new CellAddress(key, RowId, fuel.Id), table.Id, new CellValueData { ValueString = "36" }),
                     new CellRecord(new CellAddress(key, RowId, months[7].Id), table.Id, new CellValueData { ValueNumeric = 5m }),
                 ]);

            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(11, Arg.Any<CancellationToken>()).Returns(snapshot);

            // Прогін записав, яких аргументів бракувало (ModuleProfile → ModulesProfileJson).
            var profile = new ModuleProfile();
            var row = new RejectedRow(Instance, 1, RowKey, Document, MethodologyId);
            profile.RecordMissingInputs([row]);
            profile.RecordMissingArguments(row, Arguments);
            var results = Substitute.For<ICalculationResultStore>();
            results.ReadCurrentRunProfileAsync(Document, Period, Arg.Any<CancellationToken>())
                   .Returns(new CurrentRunProfile(1, Now, profile.ToJson()));

            return new GetCalculationIssuesHandler(
                issues, access, user, rowStore, cells, metadata, periods, methodologies,
                columnMapper: null, formulaEngine: new RealFormulaEngine(), results: results);
        }
    }
}
