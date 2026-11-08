using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Rule (h), human 08.11: a row bound to a permit may hold data only in months the permit covers. Shape of the live case:
/// Water Report (months in columns), row «Sea Water Intake», instance period January 2026, permit valid 01.03–30.11.2026.
/// </summary>
/// <remarks>
/// Red proof: with <c>EnsurePermitValidityCoversDataAsync</c> removed from <c>HandleAsync</c> the refusal tests write
/// (<c>ApplyAsync</c> is called); with the date exemption removed from <c>CheckLookupStandings</c> the «data only in
/// March» pick is refused as <c>entryNotValidOnDate</c>.
/// </remarks>
public sealed class PatchCellsPermitValidityTests
{
    private const long TableInstance = 500;
    private const long DocumentId = 700;
    private const int Period = 202601;
    private const int PermitColumnId = 12;
    private const int JanuaryId = 21;
    private const int FebruaryId = 22;
    private const int MarchId = 23;
    private const int PermitHeaderRegistry = 57;
    private const int PermitGroupRegistry = 65;
    private const long RowId = 1001;
    private const long PermitId = 7407;
    private const string RowKey = "R7001000";

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IPermitTypeStore _permitTypes = Substitute.For<IPermitTypeStore>();
    private readonly FakePermitGroupResolver _groups = new();
    private readonly ColumnDef _permitColumn;
    private readonly Dictionary<int, CellValueData> _stored = [];
    private AccessProfile? _readerProfile;

    public PatchCellsPermitValidityTests()
    {
        _permitColumn = Column("PermitNumber", PermitColumnId, CellDataType.Lookup);
        _permitColumn.SetLookup(registryDefId: PermitHeaderRegistry);
        var january = Month("January", JanuaryId, 1);
        var february = Month("February", FebruaryId, 2);
        var march = Month("March", MarchId, 3);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("S07_WATER_REPORT"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("T7_00"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water Report" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(_permitColumn);
        table.AddColumn(january);
        table.AddColumn(february);
        table.AddColumn(march);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>
            {
                [PermitColumnId] = _permitColumn, [JanuaryId] = january, [FebruaryId] = february, [MarchId] = march,
            },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        _clock.UtcNow.Returns(new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc));
        _user.UserId.Returns(9);
        _user.Language.Returns("en");
        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, DocumentId, TableDefId: 3, TemplateVersionId: 2, PeriodKey: Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);
        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _rows.GetRowsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new List<RowState> { new(RowKey, RowId, "0x0A", IsOrphaned: false) });
        _headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<string, ExpressionValue>());

        var profile = new AccessProfile
        {
            CacheKey = "p1", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string> { "Registry.View" },
            Grants = new Dictionary<string, GrantLevel> { ["Project:10"] = GrantLevel.Write },
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        };
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(profile);
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
               .Returns(_ => DocumentReadScope.For(_readerProfile ?? profile, 10, snapshot));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.CanEditCellsAsync(
                   Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                   Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<IReadOnlyCollection<CellAddress>>(3)
                   .ToDictionary(a => a, _ => EditDecision.Allow()));

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _cells.ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<long, string> { [RowId] = "0x0B" });

        // Stored values: the previous-value read (addresses) and the logical slice both come from `_stored`.
        _cells.ReadCellsAsync(Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(call => (IReadOnlyDictionary<CellAddress, CellValueData>)call.ArgAt<IReadOnlyCollection<CellAddress>>(0)
                  .Where(a => a.TableRowId == RowId && _stored.ContainsKey(a.ColumnDefId))
                  .ToDictionary(a => a, a => _stored[a.ColumnDefId]));
        _cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(_ => (IReadOnlyList<CellRecord>)[.. _stored.Select(p =>
                  new CellRecord(new CellAddress(PeriodKey.Parse(Period), RowId, p.Key), 3, p.Value))]);

        _periods.FindPeriodBoundsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
                .Returns(new PeriodBounds(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)));
        _permitTypes.ListPublishedHeaderRegistryIdsAsync(Arg.Any<CancellationToken>())
                    .Returns(new HashSet<int> { PermitHeaderRegistry });

        Permit(validFrom: new DateOnly(2026, 3, 1), validTo: new DateOnly(2026, 12, 1));
    }

    private static ColumnDef Column(string code, int id, CellDataType type)
    {
        var column = new ColumnDef(
            tableDefId: 3, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), id, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private static ColumnDef Month(string code, int id, byte month)
    {
        var column = Column(code, id, CellDataType.Decimal);
        column.SetMonth(isMonthColumn: true, monthNumber: month);
        return column;
    }

    private void Permit(DateOnly? validFrom, DateOnly? validTo, int registryDefId = PermitHeaderRegistry)
    {
        var entry = new RegistryEntry(
            registryDefId, EcrCode.Create("P7407"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "KZ35VTE00340032" }));
        typeof(Entity<long>).GetProperty("Id")!.SetValue(entry, PermitId);
        entry.SetValidity(validFrom, validTo);

        _registries.FindEntryStandingsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntryStanding> { new(PermitId, registryDefId, true, false, validFrom, validTo) });
        _registries.FindEntriesByIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryEntry> { entry });
    }

    private PatchCellsHandler Handler(bool withRule = true)
        => new(_cells, _rows, Substitute.For<IDocumentStore>(), _periods, _metadata, _access,
               new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
               _methodologies, _registries, _headers, Substitute.For<IAuditWriter>(),
               Substitute.For<IAuditReader>(), Substitute.For<IBackgroundJobScheduler>(), _uow, _user, _clock,
               Substitute.For<ISheetEditGate>(), Substitute.For<IUnitCatalog>(), groups: _groups,
               permitTypes: withRule ? _permitTypes : null);

    private Task<PatchCellsResponse> Write(params PatchCell[] cells)
        => Handler().HandleAsync(
            new PatchCellsRequest(TableInstance, Period, "UserEdit", [new PatchRow(RowKey, "0x0A", cells)]),
            CancellationToken.None);

    private static PatchCell PickPermit() => new("PermitNumber", PermitId);

    private void StoredPermit() => _stored[PermitColumnId] = new CellValueData { ValueRegistryEntryId = PermitId };

    private void StoredValue(int columnId, decimal value) => _stored[columnId] = new CellValueData { ValueNumeric = value };

    private async Task<BusinessRuleException> AssertRefused(params PatchCell[] cells)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Write(cells));

        Assert.Equal("ECR-CELL-4223", error.ErrorCode);
        Assert.Equal("err.ECR-CELL-4223.permitNotCoveringMonths", error.Details!["messageKey"]);
        Assert.Equal("KZ35VTE00340032", error.Details["permit"]);
        Assert.Equal("01.03.2026 – 30.11.2026", error.Details["validity"]);
        Assert.Equal(RowKey, error.Details["rowKey"]);
        Assert.Equal("PermitNumber", error.Details["columnCode"]);
        await _cells.DidNotReceive().ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
        return error;
    }

    private async Task AssertWritten(params PatchCell[] cells)
    {
        await Write(cells);
        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }

    private static string MarkedColumns(BusinessRuleException error)
        => string.Join(",", ((IEnumerable<object>)error.Details!["cells"]!)
            .Select(c => (string)c.GetType().GetProperty("ColumnCode")!.GetValue(c)!));

    // ── h1/h2/h5: the permit is picked or changed ─────────────────────────

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h2")]
    public async Task Picking_a_permit_that_does_not_cover_a_month_with_data_is_refused_and_names_it()
    {
        StoredValue(JanuaryId, 120m);
        StoredValue(MarchId, 80m);

        var error = await AssertRefused(PickPermit());

        Assert.Equal("Jan 2026", error.Details!["months"]);
        Assert.Equal("PermitNumber", MarkedColumns(error));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h2")]
    public async Task Changing_to_another_permit_checks_the_months_that_already_hold_data()
    {
        StoredPermit();
        _stored[PermitColumnId] = new CellValueData { ValueRegistryEntryId = PermitId + 1 };
        StoredValue(FebruaryId, 5m);

        var error = await AssertRefused(PickPermit());

        Assert.Equal("Feb 2026", error.Details!["months"]);
    }

    /// <summary>
    /// The instance period is January, the permit starts in March: the old «valid on the last day of the period» check
    /// would refuse it; the months with data (March only) are covered, so the pick is accepted.
    /// </summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h2")]
    public async Task A_permit_covering_every_month_with_data_is_accepted_even_if_not_valid_on_the_period_end()
    {
        StoredValue(MarchId, 80m);

        await AssertWritten(PickPermit());
    }

    /// <summary>S6: a month column the author cannot read still counts, but its month is not named in the refusal.</summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h2")]
    public async Task A_hidden_month_is_refused_without_being_named()
    {
        _readerProfile = new AccessProfile
        {
            CacheKey = "p-deny", UserId = 9, SecurityStamp = "s",
            Permissions = new HashSet<string> { "Registry.View" },
            Grants = new Dictionary<string, GrantLevel> { ["Project:10"] = GrantLevel.Write },
            Denies = new HashSet<string> { "Column:" + JanuaryId }, RoleIds = new HashSet<int>(),
        };
        StoredValue(JanuaryId, 120m);

        var error = await AssertRefused(PickPermit());

        Assert.Equal("…", error.Details!["months"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h2")]
    public async Task Picking_the_permit_and_clearing_the_uncovered_month_in_one_save_is_accepted()
    {
        StoredValue(JanuaryId, 120m);

        await AssertWritten(PickPermit(), new PatchCell("January", null));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h2")]
    public async Task A_permit_valid_in_no_month_of_the_year_is_refused_even_without_data()
    {
        Permit(validFrom: new DateOnly(2027, 1, 1), validTo: null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Write(PickPermit()));

        Assert.Equal("err.ECR-CELL-4223.permitNotCoveringMonths", error.Details!["messageKey"]);
        Assert.StartsWith("Jan 2026, Feb 2026", (string)error.Details["months"]!);
        Assert.Equal("01.01.2027 – …", error.Details["validity"]);
    }

    /// <summary>h5: the column targets a permit GROUP (registry 65), the permit lives in a member header (57).</summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h5")]
    public async Task A_column_targeting_a_permit_group_follows_the_same_rule()
    {
        _permitColumn.SetLookup(registryDefId: PermitGroupRegistry);
        _groups.WithGroup(PermitGroupRegistry, [PermitHeaderRegistry]);
        StoredValue(JanuaryId, 120m);

        var error = await AssertRefused(PickPermit());

        Assert.Equal("Jan 2026", error.Details!["months"]);
    }

    /// <summary>A Lookup column that is not permit-type keeps the old rule (valid on the last day of the period).</summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h5")]
    public async Task A_non_permit_lookup_keeps_the_period_end_rule()
    {
        _permitTypes.ListPublishedHeaderRegistryIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<int>());
        StoredValue(MarchId, 80m);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Write(PickPermit()));

        Assert.Equal("err.ECR-CELL-4223.entryNotValidOnDate", error.Details!["messageKey"]);
    }

    // ── h3: a value is entered ────────────────────────────────────────────

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h3")]
    public async Task Entering_a_value_in_a_month_the_permit_does_not_cover_is_refused_and_marks_that_cell()
    {
        StoredPermit();

        var error = await AssertRefused(new PatchCell("February", 7m));

        Assert.Equal("Feb 2026", error.Details!["months"]);
        Assert.Equal("February", MarkedColumns(error));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h3")]
    public async Task Entering_a_value_in_a_covered_month_is_accepted()
    {
        StoredPermit();

        await AssertWritten(new PatchCell("March", 7m));
    }

    /// <summary>An existing violation (January) does not block fixing the row: March is entered, January is cleared.</summary>
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h4")]
    public async Task A_row_that_already_violates_stays_fixable()
    {
        StoredPermit();
        StoredValue(JanuaryId, 120m);

        await AssertWritten(new PatchCell("March", 7m), new PatchCell("January", null));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h3")]
    public async Task A_row_without_a_permit_takes_values_in_any_month()
    {
        await AssertWritten(new PatchCell("January", 7m));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h3")]
    public async Task Re_sending_the_value_already_stored_is_not_a_new_entry()
    {
        StoredPermit();
        StoredValue(JanuaryId, 120m);

        await AssertWritten(new PatchCell("January", 120m));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h3")]
    public async Task A_permit_without_validity_window_covers_every_month()
    {
        Permit(validFrom: null, validTo: null);
        StoredPermit();

        await AssertWritten(new PatchCell("January", 7m));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)] [Trait("Finding", "ACC-1108-h3")]
    public async Task Without_the_permit_type_port_the_rule_is_off()
    {
        StoredPermit();

        await Handler(withRule: false).HandleAsync(
            new PatchCellsRequest(TableInstance, Period, "UserEdit", [new PatchRow(RowKey, "0x0A", [new PatchCell("February", 7m)])]),
            CancellationToken.None);

        await _cells.Received(1).ApplyAsync(Arg.Any<CellChangeSet>(), Arg.Any<CancellationToken>());
    }
}
