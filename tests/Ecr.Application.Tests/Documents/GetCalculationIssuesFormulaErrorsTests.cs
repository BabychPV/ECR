// tests/Ecr.Application.Tests/Documents/GetCalculationIssuesFormulaErrorsTests.cs
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
/// ✎ C-1 (D-475): група «Помилки формул» у <c>GET …/calculation-issues</c> — формула, причина, входи, джерело; S6:
/// диференційно (той самий стан, справжній <see cref="DocumentReadScope"/> з <c>Deny</c> на аркуш і без нього) — комірка
/// схованої таблиці відсутня, формула з закритим входом — <c>null</c>, закритий вхід відсутній, закрите джерело — <c>null</c>.
/// </summary>
public sealed class GetCalculationIssuesFormulaErrorsTests
{
    private const long Document = 900;
    private const int Period = 202601;
    private static readonly DateTime Since = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_обмежень_група_несе_формулу_входи_і_джерело()
    {
        var result = await Arrange(deny: false).HandleAsync(Document, Period, null, CancellationToken.None);

        var errors = Assert.IsAssignableFrom<IReadOnlyList<Ecr.Application.Calculations.Dto.FormulaErrorIssueDto>>(result.FormulaErrors);
        Assert.Equal(3, errors.Count);
        var own = Assert.Single(errors, e => e.ColumnCode == "C");
        Assert.Equal(FormulaError.ErrorKind, own.Kind);
        Assert.Equal("#DIV/0", own.ErrorCode);
        Assert.Equal("[A] / [B] + [T2].[R1].[X]", own.Formula);
        Assert.Equal(["A=10", "B=0", "X=5"], own.Inputs!.Select(i => $"{i.ColumnCode}={i.Value}").ToList());
        Assert.Equal(7, own.RowNumber);

        var blocked = Assert.Single(errors, e => e.ColumnCode == "D");
        Assert.Equal(FormulaError.BlockedKind, blocked.Kind);
        Assert.Null(blocked.Inputs);
        Assert.Equal("T2", blocked.Source!.TableCode);
        Assert.Equal("X", blocked.Source.ColumnCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Під_забороною_аркуша_закрите_не_витікає()
    {
        var result = await Arrange(deny: true).HandleAsync(Document, Period, null, CancellationToken.None);

        var errors = Assert.IsAssignableFrom<IReadOnlyList<Ecr.Application.Calculations.Dto.FormulaErrorIssueDto>>(result.FormulaErrors);

        // Комірка схованої таблиці — відсутня.
        Assert.DoesNotContain(errors, e => e.TableCode == "T2");
        Assert.Equal(2, errors.Count);

        // Формула читає закриту колонку — її текст не віддається, закритий вхід відсутній.
        var own = Assert.Single(errors, e => e.ColumnCode == "C");
        Assert.Null(own.Formula);
        Assert.Equal(["A=10", "B=0"], own.Inputs!.Select(i => $"{i.ColumnCode}={i.Value}").ToList());

        // Джерело «не оновлено» в закритій таблиці — null.
        Assert.Null(Assert.Single(errors, e => e.ColumnCode == "D").Source);
    }

    /// <summary>
    /// Payload: одна помилка, яку читає колонкова формула кожного рядка великої таблиці, дає тисячі «не оновлено» —
    /// у відповіді не більше 500, корінь (власна помилка) першим, `formulaErrorTotal` — усі видимі.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Група_обмежена_стелею_корінь_першим_усього_названо()
    {
        var handler = Arrange(deny: false, blockedRows: 2_000);

        var result = await handler.HandleAsync(Document, Period, null, CancellationToken.None);

        Assert.Equal(Ecr.Application.Calculations.Dto.CalculationIssuesDto.MaxFormulaErrors, result.FormulaErrors!.Count);
        Assert.Equal(FormulaError.ErrorKind, result.FormulaErrors[0].Kind);
        Assert.Equal(2_003, result.FormulaErrorTotal);
    }

    private static GetCalculationIssuesHandler Arrange(bool deny, int blockedRows = 0)
    {
        var template = new TemplateBuilder();
        var visibleSheet = template.Sheet("S1");
        var t1 = template.Table(visibleSheet, "T1");
        var a = template.Column(t1, "A");
        var b = template.Column(t1, "B");
        var c = template.Column(t1, "C", CellDataType.Formula);
        var d = template.Column(t1, "D", CellDataType.Formula);
        var hiddenSheet = template.Sheet("S2");
        var t2 = template.Table(hiddenSheet, "T2");
        var x = template.Column(t2, "X");
        var snapshot = template.Build();

        var builder = new AccessBuilder { UserId = 9 }
            .Permission(GetCalculationIssuesHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (deny)
        {
            builder.Deny(ResourceKind.Sheet, hiddenSheet.Id);
        }

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(call => DocumentReadScope.For(call.Arg<AccessProfile>(), AccessBuilder.ProjectId, snapshot));

        var issues = Substitute.For<ICalculationIssueStore>();
        issues.ListAsync(Document, Period, null, Arg.Any<CancellationToken>()).Returns([]);

        var inputs = $$"""
            [{"tableDefId":{{t1.Id}},"tableCode":"T1","rowKey":"R7","columnDefId":{{a.Id}},"columnCode":"A","periodOffset":0,"value":"10"},
             {"tableDefId":{{t1.Id}},"tableCode":"T1","rowKey":"R7","columnDefId":{{b.Id}},"columnCode":"B","periodOffset":0,"value":"0"},
             {"tableDefId":{{t2.Id}},"tableCode":"T2","rowKey":"R1","columnDefId":{{x.Id}},"columnCode":"X","periodOffset":0,"value":"5"}]
            """;
        var errors = Substitute.For<IFormulaErrorStore>();
        IReadOnlyList<FormulaErrorView> views =
        [
            .. Enumerable.Range(0, blockedRows).Select(_ =>
                View(Row(t1.Id, d.Id, FormulaError.BlockedKind, null, t1.Id, c.Id), "T1", "D", "[C] * 2", "T1", "C")),
            View(Row(t1.Id, c.Id, FormulaError.ErrorKind, inputs, null, null), "T1", "C", "[A] / [B] + [T2].[R1].[X]", null, null),
            View(Row(t1.Id, d.Id, FormulaError.BlockedKind, null, t2.Id, x.Id), "T1", "D", "[C] * 2", "T2", "X"),
            View(Row(t2.Id, x.Id, FormulaError.ErrorKind, null, null, null), "T2", "X", "1/0", null, null),
        ];
        errors.ListAsync(Document, Period, null, Arg.Any<CancellationToken>()).Returns(views);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);

        return new GetCalculationIssuesHandler(issues, access, user, formulaErrors: errors);
    }

    private static FormulaErrorRow Row(int table, int column, string kind, string? inputs, int? sourceTable, int? sourceColumn)
        => new(Document, Period, 500, table, null, 1001, "R7", column, 1, kind, "#DIV/0", inputs,
            sourceTable is null ? null : Period, sourceTable, sourceTable is null ? null : "R1", sourceColumn, Since);

    private static FormulaErrorView View(
        FormulaErrorRow row, string table, string column, string formula, string? sourceTable, string? sourceColumn)
        => new(row, table, column, new LocalizedText(new Dictionary<string, string> { ["en"] = column }), 7, formula,
            sourceTable, sourceColumn);
}
