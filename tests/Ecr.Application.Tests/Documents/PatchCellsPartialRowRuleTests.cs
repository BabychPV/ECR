
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
/// D-10 / L-6: часткова валідація PATCH не оцінює правила за комірками, яких
/// у надісланому зрізі немає.
/// </summary>
/// <remarks>
/// ⛔ Правило `[S] = '' OR [S] = 'ok'` над ненадісланою `S` — це `Null = ''`
/// → <c>false</c> (не Null, тож A2-02 його не ловив) → хибний Error у
/// відповіді PATCH (L-6), а для правила РІВНЯ КОМІРКИ (бачить лише свою
/// колонку) — хибне блокування запису <c>ECR-CELL-0422</c> (джерельний запис
/// падає, D-10). Правило, що прочитало ненадіслану комірку й дало <c>false</c>,
/// у частковій валідації — «не оцінено»; повну оцінку дає «Перевірити».
/// Коли всі входи надіслані або комірку СТЕРТО свідомо — Error лишається.
/// </remarks>
public sealed class PatchCellsPartialRowRuleTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const long ExistingRowId = 1001;
    private const string ExistingRowKey = "7001001";
    private const string TextRule = "[S] = '' OR [S] = 'ok'";

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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "L-6")]
    public async Task Правило_рядка_Error_над_ненадісланою_текстовою_колонкою_не_дає_хибного_Error()
    {
        Arrange(TextRule, scope: 1);

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", "20")])), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.Empty(response.Validation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "D-10")]
    public async Task Послідовні_записи_двох_комірок_рядка_обидва_проходять_із_правилом_на_колонку_якої_ще_немає()
    {
        Arrange(TextRule, scope: 1);

        var first = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", "20")])), CancellationToken.None);
        var second = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("N", "100")])), CancellationToken.None);

        Assert.Equal(1, first.AppliedCells);
        Assert.Empty(first.Validation);
        Assert.Equal(1, second.AppliedCells);
        Assert.Empty(second.Validation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "D-10")]
    public async Task Коміркове_правило_Error_з_чужою_текстовою_колонкою_не_блокує_запис()
    {
        // Коміркове правило бачить лише колонку A; `[S] = ''` там — Null = '' → false.
        Arrange("[S] = ''", scope: 0);

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", "20")])), CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);
        Assert.Empty(response.Validation);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "D-10")]
    public async Task Порушення_при_надісланій_колонці_лишається_Error()
    {
        Arrange(TextRule, scope: 1);

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("S", "bad")])), CancellationToken.None);

        var message = Assert.Single(response.Validation);
        Assert.Equal("ROWRULE", message.RuleCode);
        Assert.Equal(nameof(ValidationSeverity.Error), message.Severity);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "D-10")]
    public async Task Порушення_коміркового_правила_своєї_колонки_і_далі_блокує_запис()
    {
        Arrange("[A] >= 0", scope: 0);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("A", "-1")])), CancellationToken.None));

        Assert.Equal("ECR-CELL-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "D-10")]
    public async Task Свідомо_стерта_колонка_правила_лишається_відомою_і_порушення_повідомляється()
    {
        // `[S] = 'ok'` над стертою S: комірку надіслано (порожню) — це не «невідома».
        Arrange("[S] = 'ok'", scope: 1);

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("S", null)])), CancellationToken.None);

        var message = Assert.Single(response.Validation);
        Assert.Equal("ROWRULE", message.RuleCode);
    }

    private void Arrange(string expression, byte scope)
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("Water"), "Main", TableRowMode.Dynamic);
        var a = builder.Column(table, "A");
        var n = builder.Column(table, "N");
        var s = builder.Column(table, "S", CellDataType.String);

        table.AddValidationRule(new ValidationRule(
            table.Id, EcrCode.Create("ROWRULE"), ValidationSeverity.Error, scope, expression,
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Rule violated" })));

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

        _ = (a, n, s);
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
