// tests/Ecr.Application.Tests/Documents/HiddenValidationIssuesTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Validation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// S6 (ФВ-6.6): зауваження про таблицю під забороною читача не зникають
/// мовчки, а стають ОДНИМ знеособленим зауваженням <c>ECR-SUB-4221</c> —
/// інакше читач, чиї зауваження всі приховані, бачить «зауважень немає» при
/// заблокованому поданні.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <see cref="HiddenValidationIssues.ForViewer"/> не
/// додавати <see cref="HiddenValidationIssues.Placeholder"/> (лише фільтр, як
/// було після S6) — червоніють тести з <c>reader</c>, де приховано помилку;
/// регресійні (<c>plain</c>, приховане попередження) лишаються зеленими.
/// </remarks>
public sealed class HiddenValidationIssuesTests
{
    private const long DocumentId = 710;
    private const int Period = 202601;
    private const int TemplateVersionId = 2;
    private const int SheetId = 40;
    private const int VisibleTable = 41;
    private const int HiddenTable = 42;

    // Подробиці прихованого, які не можуть випадково збігтися ні з чим.
    private const string HiddenRule = "HIDRULE";
    private const string HiddenText = "Secret-limit-exceeded-777";
    private const string HiddenRowKey = "row-secret-913";
    private const string HiddenColumn = "COLSECRET";

    private const string VisibleRule = "VISRULE";

    private readonly IValidationResultStore _results = Substitute.For<IValidationResultStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly TemplateVersionSnapshot _snapshot = Snapshot();

    public HiddenValidationIssuesTests()
    {
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.View").Build());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _documents.GetTemplateVersionIdAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(TemplateVersionId);
        _metadata.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(_snapshot);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Єдине_зауваження_під_забороною_дає_рівно_знеособлене_а_не_порожньо()
    {
        Stored(HiddenError());
        Reader(denyHiddenTable: true);

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        Assert.NotNull(result);
        var only = Assert.Single(result!);

        Assert.Equal(ValidationSeverity.Error, only.Severity);
        Assert.Equal("ECR-SUB-4221", only.RuleCode);
        Assert.Equal(0, only.TableDefId);
        Assert.Null(only.RowKey);
        Assert.Null(only.ColumnCode);
        Assert.True(HiddenValidationIssues.IsPlaceholder(only));
        Assert.Equal("err.ECR-SUB-4221.hiddenIssues", HiddenValidationIssues.MessageKey);

        // Жодної подробиці прихованого — ні коду правила, ні тексту, ні адреси
        // (таблицю вже доведено `TableDefId == 0` вище).
        var json = JsonSerializer.Serialize(result);
        foreach (var secret in new[] { HiddenRule, HiddenText, HiddenRowKey, HiddenColumn })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Кілька_прихованих_і_видиме_дають_видиме_і_одне_знеособлене_без_числа()
    {
        Stored(
            new ValidationMessage(ValidationSeverity.Error, VisibleRule, "visible", VisibleTable, null, null, BlocksSave: false),
            HiddenError(),
            HiddenError() with { RowKey = "row-secret-914" });
        Reader(denyHiddenTable: true);

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Collection(
            result!,
            m => Assert.Equal(VisibleRule, m.RuleCode),
            m => Assert.True(HiddenValidationIssues.IsPlaceholder(m)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Без_заборони_видно_справжнє_зауваження_регресія()
    {
        Stored(HiddenError());
        Reader(denyHiddenTable: false);

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        Assert.NotNull(result);
        var only = Assert.Single(result!);
        Assert.Equal(HiddenRule, only.RuleCode);
        Assert.Equal(HiddenTable, only.TableDefId);
        Assert.Equal(HiddenRowKey, only.RowKey);
        Assert.False(HiddenValidationIssues.IsPlaceholder(only));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Приховане_попередження_подання_не_блокує_і_знеособленого_не_дає()
    {
        Stored(HiddenError() with { Severity = ValidationSeverity.Warning });
        Reader(denyHiddenTable: true);

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!);
    }

    // T1-01: Check видимого приймача з прихованим ДЖЕРЕЛОМ несе значення джерела в тексті.
    private const string LeakedText = "Check: QTY = 777.5 does not match QTY = 55: deviation 722.5";

    private static ValidationMessage CheckOnVisibleTarget()
        => new(ValidationSeverity.Error, "REL-CHK1", LeakedText, VisibleTable, "R1", "COLVIS", BlocksSave: false,
            SourceTableDefId: HiddenTable, SourceColumnCode: HiddenColumn);

    [Theory]
    [InlineData(ResourceKind.Column, 52)]
    [InlineData(ResourceKind.Table, HiddenTable)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Check_з_прихованим_джерелом_віддається_знеособленим_без_значень(ResourceKind kind, int id)
    {
        Stored(CheckOnVisibleTarget());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(DocumentReadScope.For(
                new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read).Deny(kind, id).Build(),
                AccessBuilder.ProjectId, _snapshot));

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        var only = Assert.Single(result!);
        Assert.True(HiddenValidationIssues.IsPlaceholder(only));
        var json = JsonSerializer.Serialize(result);
        foreach (var leak in new[] { "777.5", "55", "722.5", "REL-CHK1", "COLVIS" })
        {
            Assert.DoesNotContain(leak, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Check_з_видимим_джерелом_віддається_зі_значеннями_регресія()
    {
        Stored(CheckOnVisibleTarget());
        Reader(denyHiddenTable: false);

        var result = await Handler().HandleAsync(DocumentId, new PeriodKey(Period), CancellationToken.None);

        var only = Assert.Single(result!);
        Assert.Equal(LeakedText, only.Message);
    }

    private GetValidationResultHandler Handler() => new(_results, _documents, _metadata, _access, _user);

    private static ValidationMessage HiddenError()
        => new(ValidationSeverity.Error, HiddenRule, HiddenText, HiddenTable, HiddenRowKey, HiddenColumn, BlocksSave: true);

    private void Stored(params ValidationMessage[] messages)
        => _results.GetLatestAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(new ValidationSummary(
                DocumentId, Period, DateTime.UtcNow,
                messages.Count(m => m.Severity == ValidationSeverity.Error),
                messages.Count(m => m.Severity == ValidationSeverity.Warning),
                0,
                JsonSerializer.Serialize(messages.ToList())));

    /// <summary>Межі читання: <c>Read</c> на проєкт і, за потреби, заборона на таблицю.</summary>
    private void Reader(bool denyHiddenTable)
    {
        var builder = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read);
        if (denyHiddenTable)
        {
            builder = builder.Deny(ResourceKind.Table, HiddenTable);
        }

        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(DocumentReadScope.For(builder.Build(), AccessBuilder.ProjectId, _snapshot));
    }

    private static TemplateVersionSnapshot Snapshot()
    {
        var sheet = new SheetDef(1, EcrCode.Create("SH"), Text("SH"), SheetId);
        SetId(sheet, SheetId);

        var columns = new Dictionary<int, ColumnDef>();
        foreach (var (tableId, rule, columnId, columnCode) in new[]
                 {
                     (VisibleTable, VisibleRule, 51, "COLVIS"),
                     (HiddenTable, HiddenRule, 52, HiddenColumn),
                 })
        {
            var table = new TableDef(
                SheetId, EcrCode.Create($"T{tableId}"), Text("T"), tableId,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            SetId(table, tableId);

            var column = new ColumnDef(tableId, EcrCode.Create(columnCode), Text("C"), 1, CellDataType.Decimal);
            SetId(column, columnId);
            table.AddColumn(column);
            columns[columnId] = column;

            table.AddValidationRule(new ValidationRule(
                tableId, EcrCode.Create(rule), ValidationSeverity.Error, scope: 1, "true", Text(rule)));
            sheet.AddTable(table);
        }

        return new TemplateVersionSnapshot(
            TemplateVersionId, 0, [sheet], columns, new Dictionary<(int, string), RowDef>());
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);
}
