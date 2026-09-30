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
/// <c>REGFIELD</c> у правилах валідації на шляху ЗБЕРЕЖЕННЯ (<c>PATCH</c>,
/// D16-04): <see cref="PatchCellsHandler"/> будує знімок полів довідника тим
/// самим кодом, що «Перевірити» й подання (<c>TableValidation</c>).
/// </summary>
/// <remarks>
/// ⛔ Доти <c>ValidateCell</c>/<c>ValidateScope</c> у обробнику кликались без
/// знімка: <c>REGFIELD</c> давав <c>#REF</c>, правило деградувало у Warning
/// <c>ECR-VAL-RULE</c>, і коміркове <c>Error</c>-правило збереження не блокувало.
///
/// Мутаційний доказ (прогнано руками): передати <c>null</c> замість
/// <c>registryFields</c> у <c>ValidateCell</c> і <c>ValidateScope</c> обробника —
/// 5 із 6 червоні: обидва випадки
/// <see cref="Коміркове_правило_з_REGFIELD_блокує_PATCH_порушеним_значенням"/>,
/// <see cref="Коміркове_правило_з_REGFIELD_не_блокує_PATCH_коли_поле_довідника_годиться"/>,
/// <see cref="Новий_рядок_з_тимчасовим_id_теж_перевіряється_за_довідником"/>
/// (через <c>ValidateCell</c>) і
/// <see cref="Правило_рядка_з_REGFIELD_на_PATCH_читає_довідник_і_не_блокує"/>
/// (через <c>ValidateScope</c>).
/// </remarks>
public sealed class PatchCellsRegFieldRuleTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int RegistryDefId = 900;
    private const long EntryId = 5001;
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

    private ColumnDef _volume = null!;
    private ColumnDef _permit = null!;

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.8")]
    [Trait("Finding", "D16-04")]
    public async Task Коміркове_правило_з_REGFIELD_блокує_PATCH_порушеним_значенням(int limit)
    {
        Arrange("REGFIELD([Permit], 'Limit') > 0", onPermitColumn: true);
        RegistryTestData.PermitLimit(_registries, RegistryDefId, EntryId, limit);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("Permit", Json(EntryId))])),
            CancellationToken.None));

        // ⛔ Відмова — порушенням САМОГО правила, а не «правило зламане» (#REF).
        Assert.Equal("ECR-CELL-0422", error.ErrorCode);
        var cells = JsonSerializer.Serialize(error.Details!["cells"]);
        Assert.Contains("\"RuleCode\":\"PERMIT\"", cells, StringComparison.Ordinal);
        Assert.Contains($"\"RowKey\":\"{ExistingRowKey}\"", cells, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidationEngine.BrokenRuleCode, cells, StringComparison.Ordinal);

        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.8")]
    [Trait("Finding", "D16-04")]
    public async Task Коміркове_правило_з_REGFIELD_не_блокує_PATCH_коли_поле_довідника_годиться()
    {
        Arrange("REGFIELD([Permit], 'Limit') > 0", onPermitColumn: true);
        RegistryTestData.PermitLimit(_registries, RegistryDefId, EntryId, limit: 5);

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("Permit", Json(EntryId))])),
            CancellationToken.None);

        Assert.Equal(1, response.AppliedCells);

        // ⚠ Без знімка тут стояв би Warning `ECR-VAL-RULE` (#REF): запис
        // проходив би й тоді, тож доказ — саме відсутність повідомлення.
        Assert.Empty(response.Validation);

        // Одне визначення довідника й один запис — не по запиту на комірку.
        await _registries.Received(1).FindDefinitionByIdAsync(RegistryDefId, Arg.Any<CancellationToken>());
        await _registries.Received(1).ListValuesAsync(EntryId, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.8")]
    [Trait("Finding", "D16-04")]
    public async Task Новий_рядок_з_тимчасовим_id_теж_перевіряється_за_довідником()
    {
        // ⚠ `DAT-04`: до запису комірки нового рядка мають від'ємну тимчасову
        // адресу. Знімок ключується id ЗАПИСУ довідника, не рядка, тож
        // перевірка однакова для нового й наявного рядка.
        Arrange("REGFIELD([Permit], 'Limit') > 0", onPermitColumn: true);
        RegistryTestData.PermitLimit(_registries, RegistryDefId, EntryId, limit: 0);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            Request(new PatchRow("NEW-1", BaseVersion: null, [new PatchCell("Permit", Json(EntryId))])),
            CancellationToken.None));

        Assert.Equal("ECR-CELL-0422", error.ErrorCode);
        var cells = JsonSerializer.Serialize(error.Details!["cells"]);
        Assert.Contains("\"RuleCode\":\"PERMIT\"", cells, StringComparison.Ordinal);
        Assert.Contains("\"RowKey\":\"NEW-1\"", cells, StringComparison.Ordinal);

        await _rows.DidNotReceive().CreateRowsAsync(
            Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.1")]
    [Trait("Finding", "D16-04")]
    public async Task Правило_рядка_з_REGFIELD_на_PATCH_читає_довідник_і_не_блокує()
    {
        // Правило рядка на PATCH бачить лише надіслане (`PatchRowValidationContext`)
        // і запис не блокує (R-B3) — але мусить повідомити порушення, а не #REF.
        Arrange("REGFIELD([Permit], 'Limit') > 0", onPermitColumn: false, scope: 1);
        RegistryTestData.PermitLimit(_registries, RegistryDefId, EntryId, limit: 0);

        var response = await Handler().HandleAsync(
            Request(new PatchRow(ExistingRowKey, "0x0A", [new PatchCell("Permit", Json(EntryId))])),
            CancellationToken.None);

        var message = Assert.Single(response.Validation);
        Assert.Equal("PERMIT", message.RuleCode);
        Assert.Equal(nameof(ValidationSeverity.Error), message.Severity);
        Assert.Equal(ExistingRowKey, message.RowKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "D16-04")]
    [Trait("Requirement", "WR-04")]
    public async Task Шаблон_без_REGFIELD_у_правилах_не_звертається_до_довідника()
    {
        // ⚠ Храповик звернень WR-04 (`PatchCellsQueryCountTests`) не зсувається:
        // Lookup-комірка в батчі є, але жодне правило не читає REGFIELD.
        Arrange("[Volume] >= 0", onPermitColumn: false);

        await Handler().HandleAsync(
            Request(new PatchRow(
                ExistingRowKey, "0x0A", [new PatchCell("Permit", Json(EntryId)), new PatchCell("Volume", 1m)])),
            CancellationToken.None);

        await _registries.DidNotReceive().FindDefinitionByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _registries.DidNotReceive().ListValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Таблиця (Dynamic) з колонками <c>Volume</c> і Lookup <c>Permit</c> та
    /// одним <c>Error</c>-правилом рівня <paramref name="scope"/>.
    /// </summary>
    private void Arrange(string expression, bool onPermitColumn, byte scope = 0)
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("Water"), "Main", TableRowMode.Dynamic);
        _volume = builder.Column(table, "Volume");
        _permit = builder.Column(table, "Permit", CellDataType.Lookup);
        _permit.SetLookup(RegistryDefId);

        table.AddValidationRule(new ValidationRule(
            table.Id, EcrCode.Create("PERMIT"), ValidationSeverity.Error, scope, expression,
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Permit limit is zero" }),
            columnDefId: onPermitColumn ? _permit.Id : null));

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
                   .ToDictionary(a => a, _ => EditDecision.Allow()));
        _access.CanCreateRowsAsync(
                   Arg.Any<AccessProfile>(), TableInstance,
                   Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<IReadOnlyCollection<string>>(2).ToDictionary(
                   k => k,
                   _ => new NewRowAccess(EditDecision.Allow(), new Dictionary<int, EditDecision>
                   {
                       [_volume.Id] = EditDecision.Allow(),
                       [_permit.Id] = EditDecision.Allow(),
                   }),
                   StringComparer.Ordinal));

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0)
                       .Select(id => new RegistryEntryStanding(id, RegistryDefId, true, false, null, null))
                       .ToList());
        _cells.ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<long, string> { [ExistingRowId] = "0x0B" });
    }

    /// <summary>Значення так, як його приносить HTTP-запит — <see cref="JsonElement"/>.</summary>
    private static JsonElement Json(long value)
        => JsonDocument.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).RootElement;

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
