// tests/Ecr.Application.Tests/Documents/CalculationRejectionReasonsMonthNameTests.cs
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
/// AN-164c (приймання F13 PR #834, P2-a, doc 96 @202609/@202610): <c>T7b_00 S1_6/S2_7/S3_9 → MissingInputs
/// ECW_C09_02_01 rule=R_C09_02_01_01 cols=null</c>. Таблиця T7b_00 має місячні колонки, названі місяцем
/// (<c>January…December</c>, без прив'язки <c>IsMonthColumn</c>), а профіль актуального прогону аргументів цих рядків
/// не має (аркушний перерахунок іншого аркуша лишив проблеми таблиці від попереднього прогону). Проблема, якій правило
/// підійшло, мусить назвати, що заповнити, — ніколи <c>null</c> (крім сторожа прихованої колонки).
/// </summary>
/// <remarks>
/// Червоні без фіксу (повернути <c>CalculationRejectionReasons.cs</c> до 5811cb091): «вхід поза колонками без прогону»,
/// «рядок нулів» і «порожній рядок жовтня» — <c>Columns = null</c>; «місяць названо один раз» — 12 колонок замість
/// однієї. Доступ — диференціальний Deny-тест: той самий світ, прихована колонка вересня — <c>null</c>.
/// </remarks>
public sealed class CalculationRejectionReasonsMonthNameTests
{
    private const long Document = 96;
    private const int MethodologyId = 9;
    private const long Instance = 7701;
    private const long RowId = 990001;
    private const string Rule = "R_C09_02_01_01";
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Months =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    /// <summary>Формула читає входи, яких серед колонок T7b_00 немає, профілю прогону немає — входи названо кодом.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Вхід_поза_колонками_без_прогону_називається()
    {
        var result = await new World { Formula = "@Land_Concentration * @Land_Volume" }.Handler()
            .HandleAsync(Document, 202609, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(ICalculationIssueStore.MissingInputs, item.Kind);
        Assert.Equal(Rule, item.RuleCode);
        Assert.NotNull(item.Columns);
        Assert.Equal(["Land_Concentration", "Land_Volume"], item.Columns!.Select(c => c.Code));
        Assert.All(item.Columns!, c => Assert.Empty(c.NameL10n.Values));
    }

    /// <summary>Формула читає всі 12 місяців-назв: порожній рядок вересня називає лише «September», один раз.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Місяць_названо_один_раз_лише_місяць_періоду()
    {
        var result = await new World { Formula = string.Join(" + ", Months.Select(m => "@" + m)) + " + @Land_Factor" }
            .Handler()
            .HandleAsync(Document, 202609, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(Rule, item.RuleCode);
        var column = Assert.Single(item.Columns!);
        Assert.Equal("September", column.Code);
        Assert.Equal("September", column.NameL10n.Get("en"));
    }

    /// <summary>
    /// S3_9: усі місяці = 0 (заповнені), прогону немає, формула читає лише колонки — назвати колонку місяця періоду, яку
    /// читає методологія (що перевірити), а не <c>null</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядок_нулів_називає_колонку_місяця_періоду()
    {
        var result = await new World
        {
            Formula = string.Join(" + ", Months.Select(m => "@" + m)),
            Zeros = true,
        }.Handler().HandleAsync(Document, 202610, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(Rule, item.RuleCode);
        Assert.Equal(["October"], item.Columns!.Select(c => c.Code));
    }

    /// <summary>
    /// Прогону немає, формула не читає нічого, що є серед колонок чи бракує (лише константи) — назвати колонку введення
    /// місяця періоду.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Порожній_рядок_жовтня_без_аргументів_називає_колонку_місяця()
    {
        var result = await new World { Formula = "1 + 2" }.Handler()
            .HandleAsync(Document, 202610, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(Rule, item.RuleCode);
        Assert.Equal(["October"], item.Columns!.Select(c => c.Code));
    }

    /// <summary>Прогін записав аргументи — вони й названі (шлях AN-164b не змінився), місяць-назва — лише місяця періоду.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Аргументи_прогону_з_місяцями_назвами()
    {
        var result = await new World
        {
            Formula = "@Land_Concentration",
            RunArguments = [.. Months, "Land_Concentration"],
        }.Handler().HandleAsync(Document, 202609, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(["September", "Land_Concentration"], item.Columns!.Select(c => c.Code));
    }

    /// <summary>⛔ Диференціальний Deny: той самий світ, що «місяць названо один раз», але вересень прихований — нічого.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Прихована_колонка_місяця_не_називається()
    {
        var result = await new World
        {
            Formula = string.Join(" + ", Months.Select(m => "@" + m)) + " + @Land_Factor",
            HideSeptember = true,
        }.Handler().HandleAsync(Document, 202609, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(Rule, item.RuleCode);
        Assert.Null(item.Columns);
    }

    private sealed class World
    {
        public required string Formula { get; init; }

        public bool Zeros { get; init; }

        public bool HideSeptember { get; init; }

        public IReadOnlyList<string>? RunArguments { get; init; }

        public GetCalculationIssuesHandler Handler()
        {
            // Форма T7b_00 шаблону Land: службові колонки рядка, January…December (Decimal, без прив'язки місяця),
            // текстові C_*, обчислюваний вихід ECW_EC_tons.
            var template = new TemplateBuilder { TemplateVersionId = 12 };
            var sheet = template.Sheet("S07B_WATER_MEASUREMENTS");
            var table = template.Table(sheet, "T7b_00", TableRowMode.Fixed, TableLayoutKind.MonthsInColumns);
            var substance = template.Column(table, "SubstanceId", CellDataType.String);
            _ = template.Column(table, "No", CellDataType.Int);
            _ = template.Column(table, "AnnualLimit");
            var months = Months.Select(m => template.Column(table, m)).ToList();
            _ = template.Column(table, "C_R", CellDataType.String);
            _ = template.Column(table, "ECW_EC_tons", CellDataType.Calculated);
            var snapshot = template.Build();

            var builder = new AccessBuilder { UserId = 9 }
                .Permission(GetCalculationIssuesHandler.Permission)
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
            if (HideSeptember)
            {
                builder.Deny(ResourceKind.Column, months[8].Id);
            }

            var access = Substitute.For<IAccessDecisionService>();
            access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
            access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(EditDecision.Allow());
            access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

            var rowKey = Zeros ? "S3_9" : "S1_6";
            var issues = Substitute.For<ICalculationIssueStore>();
            issues.ListAsync(Document, Arg.Any<int>(), null, Arg.Any<CancellationToken>())
                  .Returns([new CalculationIssueRow(
                      table.Id, "T7b_00", null, 1, rowKey, ICalculationIssueStore.MissingInputs, "ECW_C09_02_01", Now,
                      MethodologyId)]);

            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(9);

            var version = new MethodologyVersion(MethodologyId, "V1", CalculationLevel.Configuration, 1, Now);
            typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(version, 4);
            var rules = new List<MethodologyRule>
            {
                version.AddRule(EcrCode.Create(Rule), $$"""{"{{substance.Id}}":"{{rowKey}}"}""", 10),
            };
            var formulas = new List<MethodologyFormula>
            {
                version.AddFormula(EcrCode.Create("ECW_EC_tons"), Formula, FormulaResultType.Number, null),
            };
            version.Publish(publishedByUserId: 2, "тест", new DateOnly(2024, 1, 1), testsPassed: true, Now);

            var methodologies = Substitute.For<IMethodologyStore>();
            methodologies.GetPublishedVersionsOfAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                         .Returns([version]);
            methodologies.GetRulesAsync(4, Arg.Any<CancellationToken>()).Returns(rules);
            methodologies.GetFormulasAsync(4, Arg.Any<CancellationToken>()).Returns(formulas);
            methodologies.GetLibraryContentsAsync(4, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
                         .Returns(Array.Empty<MethodologyLibraryContent>());

            var periods = Substitute.For<IPeriodStore>();
            periods.FindPeriodBoundsAsync(Document, Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(call =>
                   {
                       var key = new PeriodKey(call.ArgAt<int>(1));
                       var start = new DateOnly(key.Year, key.Value % 100, 1);
                       return new PeriodBounds(start, start.AddMonths(1).AddDays(-1));
                   });

            var rowStore = Substitute.For<IRowStore>();
            rowStore.GetTableInstancesAsync(Document, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                    .Returns(call => [new TableInstanceRef(Instance, Document, table.Id, 12, call.Arg<PeriodKey>().Value)]);
            rowStore.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
                    {
                        [Instance] = new Dictionary<string, long> { [rowKey] = RowId },
                    });

            // Логічний зріз рядка: ключ речовини (правило) і, для S3_9, нулі в усіх місяцях.
            var cells = Substitute.For<ICellStore>();
            cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<CellAddress, CellValueData>());
            cells.ReadSliceAsync(Instance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(call =>
                 {
                     var key = call.Arg<PeriodKey>();
                     var records = new List<CellRecord>
                     {
                         new(new CellAddress(key, RowId, substance.Id), table.Id, new CellValueData { ValueString = rowKey }),
                     };
                     if (Zeros)
                     {
                         records.AddRange(months.Select(m => new CellRecord(
                             new CellAddress(key, RowId, m.Id), table.Id, new CellValueData { ValueNumeric = 0m })));
                     }

                     return (IReadOnlyList<CellRecord>)records;
                 });

            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(12, Arg.Any<CancellationToken>()).Returns(snapshot);

            // Профіль актуального прогону: без аргументів цих рядків (проблеми — від попереднього прогону), або з ними.
            var profile = new ModuleProfile();
            if (RunArguments is { } recorded)
            {
                var row = new RejectedRow(Instance, 1, rowKey, Document, MethodologyId);
                profile.RecordMissingInputs([row]);
                profile.RecordMissingArguments(row, recorded);
            }

            var results = Substitute.For<ICalculationResultStore>();
            results.ReadCurrentRunProfileAsync(Document, Arg.Any<int>(), Arg.Any<CancellationToken>())
                   .Returns(new CurrentRunProfile(1, Now, profile.ToJson()));

            return new GetCalculationIssuesHandler(
                issues, access, user, rowStore, cells, metadata, periods, methodologies,
                columnMapper: null, formulaEngine: new RealFormulaEngine(), results: results);
        }
    }
}
