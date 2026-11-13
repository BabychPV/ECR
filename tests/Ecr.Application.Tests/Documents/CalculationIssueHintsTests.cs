// tests/Ecr.Application.Tests/Documents/CalculationIssueHintsTests.cs
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
/// UAT 08.11 b3: <c>GET /documents/{id}/calculation-issues</c> разом із рядками без правила віддає, що приймають
/// правила методології таблиці (колонки й допустимі значення), і поточне значення рядка в тих колонках.
/// </summary>
/// <remarks>
/// Мутації (перевірено вручну, див. звіт lane): прибрати перевірку «ключ — колонка ЦІЄЇ таблиці» — червоніє
/// «правило з колонкою іншої таблиці не потрапляє»; прибрати перевірку <c>canReadColumn</c> — червоніє «прихована
/// колонка»; брати першу версію замість чинної на кінець періоду — червоніє «версія, чинна на кінець періоду».
/// </remarks>
public sealed class CalculationIssueHintsTests
{
    private const long Document = 900;
    private const int Period = 202601;
    private const int MethodologyId = 25;
    private const long Instance = 5001;
    private const long RowId = 70001;
    private static readonly DateTime Now = new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Підказка_називає_колонку_допустимі_значення_і_поточне_значення_рядка()
    {
        var world = new World();

        var result = await world.Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        var table = Assert.Single(result.Tables!);
        Assert.Equal("T9_01", table.TableCode);
        var methodology = Assert.Single(table.Methodologies);
        var column = Assert.Single(table.Columns);
        Assert.Equal("IDCodeNAEProject", column.Code);
        Assert.Equal([["0903"], ["0905"]], table.Options.Select(o => o.ToArray()).ToList());

        var item = Assert.Single(result.Items);
        Assert.Equal(["Sample A-0"], item.CurrentValues);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Правило_з_колонкою_іншої_таблиці_не_потрапляє_у_підказку()
    {
        var world = new World { ForeignRule = true };

        var result = await world.Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        var table = Assert.Single(result.Tables!);
        Assert.DoesNotContain(table.Options, o => o.Contains("9999"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Прихована_колонка_правила_прибирає_підказку_і_поточні_значення()
    {
        var world = new World { HideCodeColumn = true };

        var result = await world.Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        Assert.Null(result.Tables);
        Assert.Null(Assert.Single(result.Items).CurrentValues);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Версія_чинна_на_кінець_періоду_а_не_пізніша()
    {
        var world = new World { LaterVersion = true };

        var result = await world.Handler().HandleAsync(Document, Period, null, CancellationToken.None);

        var table = Assert.Single(result.Tables!);
        Assert.DoesNotContain(table.Options, o => o.Contains("LATER"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_джерел_підказки_відповідь_як_доти()
    {
        var world = new World();

        var result = await world.Handler(withHints: false).HandleAsync(Document, Period, null, CancellationToken.None);

        Assert.Null(result.Tables);
        Assert.Null(Assert.Single(result.Items).CurrentValues);
    }

    private sealed class World
    {
        public bool ForeignRule { get; init; }

        public bool HideCodeColumn { get; init; }

        public bool LaterVersion { get; init; }

        public GetCalculationIssuesHandler Handler(bool withHints = true)
        {
            var template = new TemplateBuilder { TemplateVersionId = 2 };
            var sheet = template.Sheet("S09_UTILITY");
            var table = template.Table(sheet, "T9_01");
            var status = template.Column(table, "Status", CellDataType.String);
            var code = template.Column(table, "IDCodeNAEProject", CellDataType.String);
            var otherTable = template.Table(sheet, "T9_03");
            var otherCode = template.Column(otherTable, "IDCodeNAEProject", CellDataType.String);
            var snapshot = template.Build();
            _ = status;

            var builder = new AccessBuilder { UserId = 9 }
                .Permission(GetCalculationIssuesHandler.Permission)
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
            if (HideCodeColumn)
            {
                builder.Deny(ResourceKind.Column, code.Id);
            }

            var access = Substitute.For<IAccessDecisionService>();
            access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
            access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(EditDecision.Allow());
            access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
                  .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

            var issues = Substitute.For<ICalculationIssueStore>();
            issues.ListAsync(Document, Period, null, Arg.Any<CancellationToken>())
                  .Returns([new CalculationIssueRow(table.Id, "T9_01", null, 1, "S01", "NoMatchingRule", "ECW_C11_11_02", Now)]);

            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(9);

            if (!withHints)
            {
                return new GetCalculationIssuesHandler(issues, access, user);
            }

            var version = new MethodologyVersion(MethodologyId, "1.0", CalculationLevel.Configuration, 1, Now);
            SetId(version, 11);
            var rules = new List<MethodologyRule>
            {
                version.AddRule(EcrCode.Create("R2"), $$"""{"{{code.Id}}":"0905"}""", 2),
                version.AddRule(EcrCode.Create("R1"), $$"""{"{{code.Id}}":"0903"}""", 1),
                version.AddRule(EcrCode.Create("R1DUP"), $$"""{"{{code.Id}}":"0903"}""", 3),
                version.AddRule(EcrCode.Create("ALL"), "{}", 100),
            };
            if (ForeignRule)
            {
                rules.Add(version.AddRule(EcrCode.Create("RX"), $$"""{"{{otherCode.Id}}":"9999"}""", 4));
            }

            version.Publish(publishedByUserId: 2, "тест", new DateOnly(2026, 1, 1), testsPassed: true, Now);

            var versions = new List<MethodologyVersion> { version };
            var later = new MethodologyVersion(MethodologyId, "2.0", CalculationLevel.Configuration, 1, Now);
            SetId(later, 12);
            var laterRule = later.AddRule(EcrCode.Create("RL"), $$"""{"{{code.Id}}":"LATER"}""", 1);
            later.Publish(publishedByUserId: 2, "тест", new DateOnly(2026, 6, 1), testsPassed: true, Now);
            if (LaterVersion)
            {
                versions.Add(later);
            }

            var methodologies = Substitute.For<IMethodologyStore>();
            methodologies.GetMethodologyIdsBoundToTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                         .Returns([MethodologyId]);
            methodologies.GetPublishedVersionsOfAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                         .Returns(versions);
            methodologies.GetRulesOfVersionsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             var ids = call.Arg<IReadOnlyCollection<int>>();
                             var result = new List<MethodologyRule>();
                             if (ids.Contains(11))
                             {
                                 result.AddRange(rules);
                             }

                             if (ids.Contains(12))
                             {
                                 result.Add(laterRule);
                             }

                             return (IReadOnlyList<MethodologyRule>)result;
                         });
            methodologies.GetVersionLabelsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                         .Returns(new Dictionary<int, MethodologyVersionLabel>
                         {
                             [11] = new(11, "ECW_C11_11_02", "1.0", new LocalizedText(new Dictionary<string, string> { ["en"] = "Cooling medium" })),
                             [12] = new(12, "ECW_C11_11_02", "2.0"),
                         });

            var periods = Substitute.For<IPeriodStore>();
            periods.FindPeriodBoundsAsync(Document, Period, Arg.Any<CancellationToken>())
                   .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));

            var rowStore = Substitute.For<IRowStore>();
            rowStore.GetTableInstancesAsync(Document, new PeriodKey(Period), Arg.Any<CancellationToken>())
                    .Returns([new TableInstanceRef(Instance, Document, table.Id, 2, Period)]);
            rowStore.GetRowIdsBatchAsync(Arg.Any<IReadOnlyList<long>>(), new PeriodKey(Period), Arg.Any<CancellationToken>())
                    .Returns(new Dictionary<long, IReadOnlyDictionary<string, long>>
                    {
                        [Instance] = new Dictionary<string, long> { ["S01"] = RowId },
                    });

            var cells = Substitute.For<ICellStore>();
            // B10: поточні значення — логічний зріз періоду (ReadSlicesAsync), не фізичне ReadCellsAsync.
            cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), new PeriodKey(Period), Arg.Any<CancellationToken>())
                 .Returns(new Dictionary<long, IReadOnlyList<CellRecord>>
                 {
                     [Instance] =
                     [
                         new CellRecord(new CellAddress(new PeriodKey(Period), RowId, code.Id), table.Id, new CellValueData { ValueString = "Sample A-0" }),
                         new CellRecord(new CellAddress(new PeriodKey(Period), RowId + 1, code.Id), table.Id, new CellValueData { ValueString = "other row" }),
                     ],
                 });

            var metadata = Substitute.For<IMetadataCache>();
            metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);

            return new GetCalculationIssuesHandler(issues, access, user, rowStore, cells, metadata, periods, methodologies);
        }

        private static void SetId(MethodologyVersion version, int id)
            => typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(version, id);
    }
}
