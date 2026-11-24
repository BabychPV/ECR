// tests/Ecr.Application.Tests/Documents/CalculationRejectionReasonsMonthAliasTests.cs
using Ecr.Application.Calculations;
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
/// Missing-input month alias (live doc 96, Land 1.0.20.0 @202610): прогін записав, що рядку бракує
/// <c>Land_RunningHoursMonthly</c> і <c>Land_Measure_FuelSubstanceConsumptionOrPumpedVolume</c> — це псевдоніми місячних
/// входів річної розкладки, а панель казала «(this table has no such column)». Проблема має назвати КОЛОНКУ місяця періоду
/// (код <c>_M10</c> і заголовок з місяцем); справжній вхід поза колонками лишається входом.
/// </summary>
/// <remarks>
/// Мутації: прибрати пошук псевдоніма в <c>MonthColumnOf</c> — червоніє «псевдонім cfg.MonthInputAlias»; прибрати запасний
/// шлях <c>&lt;X&gt;_M&lt;mm&gt;</c> — червоніє «основа колонки місяця»; не перевіряти місяць колонки — червоніє «чужий місяць».
/// </remarks>
public sealed class CalculationRejectionReasonsMonthAliasTests
{
    private const long Document = 96;
    private const int Period = 202610;
    private const int MethodologyId = 7;
    private const long Instance = 7700;
    private const long RowId = 880003;
    private const string RowKey = "S1_3";
    private const string Rule = "R_C05_04_01";
    private static readonly DateTime Now = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Псевдонім_cfg_MonthInputAlias_називає_колонку_місяця_періоду_з_місяцем()
    {
        var result = await new World { Arguments = ["Land_Measure_FuelSubstanceConsumptionOrPumpedVolume"] }.Handler()
            .HandleAsync(Document, Period, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ICalculationIssueStore.MissingInputs, item.Kind);
        Assert.Equal(Rule, item.RuleCode);
        var column = Assert.Single(item.Columns!);
        Assert.Equal("LiquidFuelConsumption_M10", column.Code);
        Assert.Equal("Liquid fuel consumption (t) · Oct", column.NameL10n.Get("en"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Основа_колонки_місяця_без_псевдоніма_називає_колонку_місяця_періоду()
    {
        var result = await new World { Arguments = ["Land_RunningHoursMonthly"], WithRunningHoursAlias = false }.Handler()
            .HandleAsync(Document, Period, null, CancellationToken.None);

        var column = Assert.Single(Assert.Single(result.Items).Columns!);
        Assert.Equal("Land_RunningHoursMonthly_M10", column.Code);
        Assert.Equal("Running hours per month (h) · Oct", column.NameL10n.Get("en"));
    }

    /// <summary>Doc 96 рядок 3: обидва входи — колонки жовтня, а вхід, якого в таблиці справді немає, — входом.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Doc96_обидва_псевдоніми_колонками_а_справжній_вхід_поза_колонками_входом()
    {
        var result = await new World
        {
            Arguments = ["Land_Measure_FuelSubstanceConsumptionOrPumpedVolume", "Land_RunningHoursMonthly", "Land_Density"],
        }.Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        var columns = Assert.Single(result.Items).Columns!;
        Assert.Equal(
            ["Land_RunningHoursMonthly_M10", "LiquidFuelConsumption_M10", "Land_Density"],
            columns.Select(c => c.Code));
        Assert.All(columns.Take(2), c => Assert.EndsWith(" · Oct", c.NameL10n.Get("en")));
        Assert.Empty(columns[2].NameL10n.Values);
    }

    /// <summary>Заповнена колонка місяця періоду не називається — бракує лише порожнього.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Заповнена_колонка_псевдоніма_не_називається()
    {
        var result = await new World
        {
            Arguments = ["Land_Measure_FuelSubstanceConsumptionOrPumpedVolume", "Land_RunningHoursMonthly"],
            FillOctoberFuel = true,
        }.Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        Assert.Equal(["Land_RunningHoursMonthly_M10"], Assert.Single(result.Items).Columns!.Select(c => c.Code));
    }

    /// <summary>Колонка <c>&lt;X&gt;_M10</c>, прив'язана до ІНШОГО місяця, — не колонка жовтня: вхід лишається входом.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Колонка_з_суфіксом_іншого_місяця_не_підміняє_вхід()
    {
        var result = await new World { Arguments = ["Land_Odd"], WithRunningHoursAlias = false }.Handler()
            .HandleAsync(Document, Period, null, CancellationToken.None);

        var column = Assert.Single(Assert.Single(result.Items).Columns!);
        Assert.Equal("Land_Odd", column.Code);
        Assert.Empty(column.NameL10n.Values);
    }

    private sealed class World
    {
        public required IReadOnlyList<string> Arguments { get; init; }

        public bool WithRunningHoursAlias { get; init; } = true;

        public bool FillOctoberFuel { get; init; }

        public GetCalculationIssuesHandler Handler()
        {
            var template = new TemplateBuilder { TemplateVersionId = 11 };
            var sheet = template.Sheet("S05_FUEL");
            var table = template.Table(sheet, "T5_04", TableRowMode.Dynamic, TableLayoutKind.MonthsInColumns);
            var status = template.Column(table, "Land_Status", CellDataType.String);
            var hours = new List<ColumnDef>();
            var fuel = new List<ColumnDef>();
            for (var m = 1; m <= 12; m++)
            {
                var h = template.Column(table, $"Land_RunningHoursMonthly_M{m:00}");
                h.SetMonth(true, (byte)m);
                h.Rename(new LocalizedText(new Dictionary<string, string> { ["en"] = "Running hours per month (h)" }));
                hours.Add(h);
                var f = template.Column(table, $"LiquidFuelConsumption_M{m:00}");
                f.SetMonth(true, (byte)m);
                f.Rename(new LocalizedText(new Dictionary<string, string> { ["en"] = "Liquid fuel consumption (t)" }));
                fuel.Add(f);
            }

            // Пастка: код «<X>_M10», але прив'язка — травень; жовтнева колонка для `Land_Odd` її не підміняє.
            var odd = template.Column(table, "Land_Odd_M10");
            odd.SetMonth(true, 5);

            var aliases = new Dictionary<(int TableDefId, int MonthNumber), IReadOnlyList<MonthInputAlias>>();
            for (var m = 1; m <= 12; m++)
            {
                var list = new List<MonthInputAlias>
                {
                    new(table.Id, "Land_Measure_FuelSubstanceConsumptionOrPumpedVolume", (byte)m, fuel[m - 1].Id),
                };
                if (WithRunningHoursAlias)
                {
                    list.Add(new MonthInputAlias(table.Id, "Land_RunningHoursMonthly", (byte)m, hours[m - 1].Id));
                }

                aliases[(table.Id, m)] = list;
            }

            var snapshot = template.Build() with { MonthInputAliases = aliases };

            var builder = new AccessBuilder { UserId = 9 }
                .Permission(GetCalculationIssuesHandler.Permission)
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
            var access = Substitute.For<IAccessDecisionService>();
            access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
            access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(EditDecision.Allow());
            access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

            var issues = Substitute.For<ICalculationIssueStore>();
            issues.ListAsync(Document, Period, null, Arg.Any<CancellationToken>())
                  .Returns([new CalculationIssueRow(
                      table.Id, "T5_04", null, 3, RowKey, ICalculationIssueStore.MissingInputs, "ECW_C05_04", Now,
                      MethodologyId)]);

            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(9);

            var version = new MethodologyVersion(MethodologyId, "V1", CalculationLevel.Configuration, 1, Now);
            typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(version, 3);
            var rules = new List<MethodologyRule>
            {
                version.AddRule(EcrCode.Create(Rule), $$"""{"{{status.Id}}":"1"}""", 10),
            };
            var formulas = new List<MethodologyFormula>
            {
                version.AddFormula(
                    EcrCode.Create("ECW_Tons"),
                    "@Land_Measure_FuelSubstanceConsumptionOrPumpedVolume * @Land_RunningHoursMonthly",
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
                   .Returns(new PeriodBounds(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));

            var rowStore = Substitute.For<IRowStore>();
            rowStore.GetTableInstancesAsync(Document, new PeriodKey(Period), Arg.Any<CancellationToken>())
                    .Returns([new TableInstanceRef(Instance, Document, table.Id, 11, Period)]);
            rowStore.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), new PeriodKey(Period), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
                    {
                        [Instance] = new Dictionary<string, long> { [RowKey] = RowId },
                    });

            // Вересень заповнений (жовтню не потрібен); жовтень — порожній, крім палива за прапором.
            var key = new PeriodKey(Period);
            var slice = new List<CellRecord>
            {
                new(new CellAddress(key, RowId, status.Id), table.Id, new CellValueData { ValueString = "1" }),
                new(new CellAddress(key, RowId, hours[8].Id), table.Id, new CellValueData { ValueNumeric = 720m }),
                new(new CellAddress(key, RowId, fuel[8].Id), table.Id, new CellValueData { ValueNumeric = 3m }),
            };
            if (FillOctoberFuel)
            {
                slice.Add(new CellRecord(new CellAddress(key, RowId, fuel[9].Id), table.Id, new CellValueData { ValueNumeric = 4m }));
            }

            var cells = Substitute.For<ICellStore>();
            cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<CellAddress, CellValueData>());
            cells.ReadSliceAsync(Instance, key, Arg.Any<CancellationToken>()).Returns(slice);

            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(11, Arg.Any<CancellationToken>()).Returns(snapshot);

            var profile = new ModuleProfile();
            var row = new RejectedRow(Instance, 3, RowKey, Document, MethodologyId);
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
