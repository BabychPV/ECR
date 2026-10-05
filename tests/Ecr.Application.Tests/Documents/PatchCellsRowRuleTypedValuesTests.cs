using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Validation;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Правила РІВНЯ РЯДКА на шляху збереження (<c>PATCH</c>) бачать значення
/// комірок ТИПІЗОВАНИМИ — так само, як «Перевірити».
/// </summary>
/// <remarks>
/// ⛔ Клієнт шле Decimal РЯДКОМ (<c>edits.ts</c>, <c>decimalTextOf</c>: точність
/// понад 15 знаків не вміщується в JS-число). <c>PatchRowValidationContext</c>
/// віддавав рядок як є, <c>ScopeContext.FromObject</c> робив із нього Text, і
/// <c>[A] &gt; 0</c> давало <c>#VALUE</c> → Warning <c>ECR-VAL-RULE</c> на
/// КОРЕКТНИХ даних у кожній збереженій комірці з Row-правилом (A1-07,
/// приймальний прохід 2026-10-05). «Перевірити» читає типізований зріз з БД і
/// цього не мало.
/// </remarks>
public sealed class PatchCellsRowRuleTypedValuesTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const long ExistingRowId = 1001;
    private const string ExistingRowKey = "7001001";

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    [Theory]
    [InlineData("12.5", "[A] > 0")]
    [InlineData("12.5", "[A] > 12")]
    [InlineData("0.0000000000000001", "[A] > 0")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "A1-07")]
    public async Task Decimal_рядком_у_правилі_рядка_є_числом_а_не_текстом(string text, string rule)
    {
        Arrange(rule);

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", text)])), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.Empty(response.Validation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "A1-07")]
    public async Task Порушення_правила_рядка_для_Decimal_рядком_повідомляється_як_порушення_а_не_як_зламане_правило()
    {
        Arrange("[A] > 20");

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", "12.5")])), CancellationToken.None);

        var message = Assert.Single(response.Validation);
        Assert.Equal("ROWRULE", message.RuleCode);
        Assert.Equal(nameof(ValidationSeverity.Warning), message.Severity);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "A1-07")]
    public async Task Int_рядком_і_Decimal_рядком_в_одному_правилі_рядка_порівнюються_як_числа()
    {
        Arrange("[N] <= [A]");

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", "12.5"), new PatchCell("N", "7")])),
            CancellationToken.None);

        Assert.Empty(response.Validation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "A1-07")]
    public async Task Bool_рядком_у_правилі_рядка_є_логічним_значенням()
    {
        Arrange("[F] = true");

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("F", "true")])), CancellationToken.None);

        Assert.Empty(response.Validation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "A1-07")]
    public async Task Дата_рядком_у_рядку_з_правилом_не_ламає_збереження()
    {
        Arrange("[A] > 0");

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", "1"), new PatchCell("D", "2026-01-15")])),
            CancellationToken.None);

        Assert.Equal(2, response.AppliedCells);
        Assert.Empty(response.Validation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "A1-07")]
    public async Task Порожнє_значення_лишається_Null_і_дає_ECR_VAL_RULE_як_і_раніше()
    {
        Arrange("[A] > 0");

        // Порожнє = «стерти» (R-B4): комірки в правилі немає, Null поширюється.
        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", null)])), CancellationToken.None);

        var message = Assert.Single(response.Validation);
        Assert.Equal(ValidationEngine.BrokenRuleCode, message.RuleCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "A1-07")]
    public async Task Некоректний_рядок_числа_відхиляється_як_і_раніше_ECR_CELL_0422()
    {
        Arrange("[A] > 0");

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", "abc")])), CancellationToken.None));

        Assert.Equal("ECR-CELL-0422", error.ErrorCode);
    }

    private void Arrange(string expression)
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("Water"), "Main", TableRowMode.Dynamic);
        var a = builder.Column(table, "A");
        var n = builder.Column(table, "N", CellDataType.Int);
        var f = builder.Column(table, "F", CellDataType.Bool);
        var d = builder.Column(table, "D", CellDataType.Date);

        table.AddValidationRule(new ValidationRule(
            table.Id, EcrCode.Create("ROWRULE"), ValidationSeverity.Warning, scope: 1, expression,
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Row rule violated" })));

        var snapshot = builder.Build();
        var templateVersionId = snapshot.TemplateVersionId;

        _clock.UtcNow.Returns(new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(
                 TableInstance, DocumentId: 700, TableDefId: table.Id, TemplateVersionId: templateVersionId,
                 PeriodKey: Period));
        _metadata.GetAsync(templateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        _methodologies.GetMethodologyIdsBoundToTableAsync(table.Id, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _rows.GetRowsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns((IReadOnlyList<RowState>)[new RowState(ExistingRowKey, ExistingRowId, "0x0A", IsOrphaned: false)]);
        _headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, ExpressionValue>());

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        });
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanEditCellsAsync(
                   Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                   Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<IReadOnlyCollection<CellAddress>>(3)
                   .ToDictionary(x => x, _ => EditDecision.Allow()));

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _cells.ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<long, string> { [ExistingRowId] = "0x0B" });

        _ = (a, n, f, d);
    }

    private static PatchCellsRequest Request(params PatchRow[] rows)
        => new(TableInstance, Period, "UserEdit", rows);

    private PatchCellsHandler Handler()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal)));

        return new(_cells, _rows, Substitute.For<IDocumentStore>(), Substitute.For<IPeriodStore>(), _metadata, _access,
            new ValidationEngine(new RealFormulaEngine()),
            _methodologies, _registries, _headers, Substitute.For<IAuditWriter>(), Substitute.For<IAuditReader>(),
            Substitute.For<IBackgroundJobScheduler>(), _uow, _user, _clock,
            Substitute.For<ISheetEditGate>(), units);
    }
}
