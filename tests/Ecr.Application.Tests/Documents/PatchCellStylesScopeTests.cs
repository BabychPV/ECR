using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// CELL-STYLE «область»: <c>scope</c> {рядки × колонки} в тому самому PATCH — розгортання, права (ті самі, що в
/// items), часткова відмова, нерозкриття прихованої колонки, стеля, одна подія аудиту, items сильніші за scope.
/// </summary>
public sealed class PatchCellStylesScopeTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int TableDef = 3;
    private const int Period = 202601;
    private const int Volume = 11;
    private const int Cost = 12;
    private const int Secret = 13;
    private const int UserId = 9;
    private static readonly DateTime Now = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStyleStore _styles = Substitute.For<ICellStyleStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ISheetEditGate _gate = Substitute.For<ISheetEditGate>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IEditAssignmentStore _assignments = Substitute.For<IEditAssignmentStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly List<CellStyle> _added = [];
    private readonly List<CellStyle> _removed = [];
    private readonly List<SecurityEventRecord> _events = [];
    private readonly HashSet<CellAddress> _denied = [];
    private readonly List<CellStyle> _existing = [];
    private readonly List<EditAssignmentFact> _facts = [];
    private int _canClearCalls;

    public PatchCellStylesScopeTests()
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        SetId(sheet, 4);
        var table = new TableDef(4, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDef);
        var byId = new Dictionary<int, ColumnDef>();
        foreach (var (id, code, hidden) in new[] { (Volume, "Volume", false), (Cost, "Cost", false), (Secret, "Secret", true) })
        {
            var column = new ColumnDef(TableDef, EcrCode.Create(code), Text(code), 1, CellDataType.Decimal);
            SetId(column, id);
            column.SetHidden(hidden);
            if (id == Cost)
            {
                // Місячна колонка (травень): під неї можна поставити передачу, що закриває лише її.
                column.SetMonth(true, 5);
            }

            table.AddColumn(column);
            byId[id] = column;
        }

        sheet.AddTable(table);
        var snapshot = new TemplateVersionSnapshot(2, 0, [sheet], byId, new Dictionary<(int, string), RowDef>());
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);

        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
               .Returns(_ => ReadScopes.Everything(snapshot));

        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["R1"] = 1, ["R2"] = 2, ["R3"] = 3 });

        _access.CanClearCellsAsync(
                Arg.Any<AccessProfile>(), TableInstance, Arg.Any<PeriodKey>(),
                Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _canClearCalls++;
                return (IReadOnlyDictionary<CellAddress, EditDecision>)call.ArgAt<IReadOnlyCollection<CellAddress>>(3)
                    .ToDictionary(
                        a => a,
                        a => _denied.Contains(a) ? EditDecision.Deny(EditDenyReason.RowReadOnly) : EditDecision.Allow());
            });

        _gate.EnterStructureAsync(Document, false, Arg.Any<CancellationToken>()).Returns(2);
        _gate.EnterEditAsync(Document, Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(DocumentStatus.Draft);
        _assignments.ReadFactsForGateAsync(Document, UserId, Arg.Any<CancellationToken>())
                    .Returns(_ => new EditAssignmentGateFacts(1, [.. _facts]));

        _styles.LoadForUpdateAsync(Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromResult<IReadOnlyList<CellStyle>>([.. _existing]));
        _styles.When(s => s.Add(Arg.Any<CellStyle>())).Do(c => _added.Add(c.Arg<CellStyle>()));
        _styles.When(s => s.Remove(Arg.Any<CellStyle>())).Do(c => _removed.Add(c.Arg<CellStyle>()));

        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(_events.Add), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(CancellationToken.None));
    }

    [Fact]
    public async Task Уся_таблиця_розгортається_у_видимі_колонки_без_прихованої_і_пише_одну_подію_без_переліку_комірок()
    {
        var response = await HandleAsync(new CellStyleScope(
            AllRows: true, AllColumns: true, BackgroundHex: "#ffe699"));

        // 3 рядки × 2 видимі колонки; прихована Secret у розгортання не входить.
        Assert.Equal(6, response.AppliedCells);
        Assert.Equal(0, response.DeniedCells);
        Assert.Null(response.DeniedByReason);
        Assert.Equal(6, _added.Count);
        Assert.DoesNotContain(_added, s => s.ColumnDefId == Secret);

        var single = Assert.Single(_events);
        Assert.Equal(PatchCellStylesHandler.SetEventType, single.EventType);
        using var json = JsonDocument.Parse(single.DetailsJson!);
        Assert.Equal(6, json.RootElement.GetProperty("applied").GetInt32());
        Assert.Equal("all", json.RootElement.GetProperty("scope").GetProperty("rows").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("cells").GetArrayLength());
        Assert.DoesNotContain("R1", single.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret", single.DetailsJson, StringComparison.Ordinal);
    }

    // ── Гілка «передача змінилась під замком» (blocked.Count > 0): факти приходять ПІСЛЯ рішення про права ──

    private static EditAssignmentFact FilledForMonth(byte month)
        => new(
            SheetCode: null, TableCode: null, MonthNumber: month, Period: null,
            IsExclusive: false, IsValidated: false, DelegatorActive: true,
            DelegatedByUserId: 1, ExpiresAt: null, MyProgress: AssigneeProgress.Filled);

    [Fact]
    public async Task Під_замком_закрита_колонка_області_відмовляється_частково_решта_застосована()
    {
        _facts.Add(FilledForMonth(5));

        var response = await HandleAsync(new CellStyleScope(
            AllRows: true, AllColumns: true, BackgroundHex: "#FFFF00"));

        // Volume застосовано (3 рядки); Cost (місяць 5, Filled) відмовлено під замком — 3 комірки.
        Assert.Equal(3, response.AppliedCells);
        Assert.Equal(3, response.DeniedCells);
        Assert.Equal(3, response.DeniedByReason!["AssignmentFilled"]);
        Assert.All(_added, s => Assert.Equal(Volume, s.ColumnDefId));
        Assert.Single(_events);
    }

    [Fact]
    public async Task Під_замком_item_у_закритій_колонці_це_403_без_запису_навіть_якщо_scope_дозволений()
    {
        _facts.Add(FilledForMonth(5));
        var request = new PatchCellStylesRequest(
            Period,
            [new CellStyleChange("R1", "Cost", "#00FF00")],
            Reset: false,
            Scope: new CellStyleScope(AllRows: true, ColumnCodes: ["Volume"], BackgroundHex: "#FF0000"));

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => HandleAsync(request));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        Assert.Empty(_added);
        Assert.Empty(_events);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Під_замком_уся_область_закрита_це_403_без_запису()
    {
        _facts.Add(FilledForMonth(5));

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => HandleAsync(new CellStyleScope(
            AllRows: true, ColumnCodes: ["Cost"], BackgroundHex: "#FFFF00")));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        Assert.Empty(_added);
        Assert.Empty(_events);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Подія_містить_ключі_рядків_до_ста_і_всі_коди_колонок()
    {
        await HandleAsync(new CellStyleScope(
            RowKeys: ["R1", "R3"], ColumnCodes: ["Volume", "Cost"], BackgroundHex: "#FFFF00"));

        using var json = JsonDocument.Parse(Assert.Single(_events).DetailsJson!);
        var scope = json.RootElement.GetProperty("scope");
        Assert.Equal(["R1", "R3"], scope.GetProperty("rowKeys").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            ["Volume", "Cost"], scope.GetProperty("columnCodes").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(scope.GetProperty("rowKeysTruncated").GetBoolean());
        Assert.Equal(0, json.RootElement.GetProperty("cells").GetArrayLength());
    }

    [Fact]
    public void Перелік_понад_стелю_відхиляється_формою()
    {
        var scope = new CellStyleScope(
            RowKeys: [.. Enumerable.Range(0, PatchCellStylesRequest.ScopeMaxCells + 1).Select(i => $"K{i}")],
            AllColumns: true,
            BackgroundHex: "#FFFF00");

        var ex = Assert.Throws<BusinessRuleException>(scope.EnsureValid);
        Assert.Equal("err.ECR-CELL-4224.tooManyCells", ex.Details!["messageKey"]);
    }

    [Fact]
    public async Task Часткова_відмова_застосовує_дозволене_і_лічить_відмовлене_за_причиною()
    {
        _denied.Add(new CellAddress(new PeriodKey(Period), 2, Volume));
        _denied.Add(new CellAddress(new PeriodKey(Period), 3, Cost));

        var response = await HandleAsync(new CellStyleScope(
            AllRows: true, AllColumns: true, BackgroundHex: "#FFFF00"));

        Assert.Equal(4, response.AppliedCells);
        Assert.Equal(2, response.DeniedCells);
        Assert.Equal(2, response.DeniedByReason!["RowReadOnly"]);
        Assert.DoesNotContain(_added, s => s.TableRowId == 2 && s.ColumnDefId == Volume);
    }

    [Fact]
    public async Task Нуль_дозволених_комірок_області_це_403_без_запису()
    {
        foreach (var row in new long[] { 1, 2, 3 })
        {
            foreach (var column in new[] { Volume, Cost })
            {
                _denied.Add(new CellAddress(new PeriodKey(Period), row, column));
            }
        }

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => HandleAsync(new CellStyleScope(
            AllRows: true, AllColumns: true, BackgroundHex: "#FFFF00")));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        Assert.Empty(_added);
        Assert.Empty(_events);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Явний_код_прихованої_колонки_відмовляє_як_неіснуючий_і_не_називає_причини()
    {
        var hidden = await Assert.ThrowsAsync<BusinessRuleException>(() => HandleAsync(new CellStyleScope(
            AllRows: true, ColumnCodes: ["Secret"], BackgroundHex: "#FFFF00")));
        var missing = await Assert.ThrowsAsync<BusinessRuleException>(() => HandleAsync(new CellStyleScope(
            AllRows: true, ColumnCodes: ["NoSuchColumn"], BackgroundHex: "#FFFF00")));

        Assert.Equal("ECR-CELL-4224", hidden.ErrorCode);
        Assert.Equal(missing.Message, hidden.Message);
        Assert.Equal(missing.Details!["messageKey"], hidden.Details!["messageKey"]);
        Assert.Equal("err.ECR-CELL-4224.unknownCell", hidden.Details["messageKey"]);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Підмножина_рядків_і_колонок_фарбує_лише_перетин()
    {
        var response = await HandleAsync(new CellStyleScope(
            RowKeys: ["R1", "R3"], ColumnCodes: ["Cost"], ForegroundHex: "#112233", IsBold: true));

        Assert.Equal(2, response.AppliedCells);
        Assert.All(_added, s => Assert.Equal(Cost, s.ColumnDefId));
        Assert.Equal([1L, 3L], [.. _added.Select(s => s.TableRowId).Order()]);
    }

    [Fact]
    public async Task Items_сильніші_за_scope_на_спільній_комірці()
    {
        var request = new PatchCellStylesRequest(
            Period,
            [new CellStyleChange("R1", "Volume", "#00FF00")],
            Reset: false,
            Scope: new CellStyleScope(AllRows: true, ColumnCodes: ["Volume"], BackgroundHex: "#FF0000"));

        var response = await HandleAsync(request);

        Assert.Equal(3, response.AppliedCells);
        Assert.Equal("#00FF00", _added.Single(s => s.TableRowId == 1).BackgroundHex);
        Assert.All(_added.Where(s => s.TableRowId != 1), s => Assert.Equal("#FF0000", s.BackgroundHex));
        Assert.Single(_events);
    }

    [Fact]
    public async Task Відмова_комірки_з_items_лишається_повною_навіть_якщо_область_дозволена()
    {
        _denied.Add(new CellAddress(new PeriodKey(Period), 1, Volume));
        var request = new PatchCellStylesRequest(
            Period,
            [new CellStyleChange("R1", "Volume", "#00FF00")],
            Reset: false,
            Scope: new CellStyleScope(AllRows: true, ColumnCodes: ["Cost"], BackgroundHex: "#FF0000"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => HandleAsync(request));

        Assert.Empty(_added);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reset_області_знімає_оформлення_і_пише_подію_скидання()
    {
        foreach (var row in new long[] { 1, 2 })
        {
            var style = new CellStyle(new CellAddress(new PeriodKey(Period), row, Volume), UserId, Now);
            style.Apply("#FFFF00", null, null, UserId, Now);
            _existing.Add(style);
        }

        var response = await HandleAsync(new CellStyleScope(AllRows: true, AllColumns: true, Reset: true));

        Assert.Equal(0, response.AppliedCells);
        Assert.Equal(2, response.ResetCells);
        Assert.Equal(4, response.SkippedCells);
        Assert.Equal(2, _removed.Count);
        Assert.Equal(PatchCellStylesHandler.ResetEventType, Assert.Single(_events).EventType);
    }

    [Fact]
    public async Task Повтор_запиту_ідемпотентний_нічого_не_пише_і_подій_немає()
    {
        foreach (var row in new long[] { 1, 2, 3 })
        {
            var style = new CellStyle(new CellAddress(new PeriodKey(Period), row, Volume), UserId, Now);
            style.Apply("#FFFF00", null, null, UserId, Now);
            _existing.Add(style);
        }

        var response = await HandleAsync(new CellStyleScope(
            AllRows: true, ColumnCodes: ["Volume"], BackgroundHex: "#FFFF00"));

        Assert.Equal(0, response.AppliedCells);
        Assert.Equal(3, response.SkippedCells);
        Assert.Empty(_added);
        Assert.Empty(_events);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Понад_стелю_відхиляється_до_будь_якого_рішення_про_права()
    {
        var many = Enumerable.Range(1, PatchCellStylesRequest.ScopeMaxCells + 1)
            .ToDictionary(i => $"K{i}", i => (long)i);
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(many);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => HandleAsync(new CellStyleScope(
            AllRows: true, ColumnCodes: ["Volume"], BackgroundHex: "#FFFF00")));

        Assert.Equal("err.ECR-CELL-4224.tooManyCells", ex.Details!["messageKey"]);
        Assert.Equal(0, _canClearCalls);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task Рівно_стеля_проходить_і_права_питаються_порціями_по_рядках()
    {
        var many = Enumerable.Range(1, PatchCellStylesRequest.ScopeMaxCells)
            .ToDictionary(i => $"K{i}", i => (long)i);
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(many);

        var response = await HandleAsync(new CellStyleScope(
            AllRows: true, ColumnCodes: ["Volume"], BackgroundHex: "#FFFF00"));

        Assert.Equal(PatchCellStylesRequest.ScopeMaxCells, response.AppliedCells);
        Assert.Equal(PatchCellStylesRequest.ScopeMaxCells / 1000, _canClearCalls);
    }

    [Theory]
    [InlineData(true, false, false, true)]   // обидва «усі» й «список» для рядків
    [InlineData(false, false, false, false)] // жодного для рядків
    public void Форма_області_вимагає_рівно_одне_для_рядків(bool allRows, bool withKeys, bool withCols, bool allCols)
    {
        var scope = new CellStyleScope(
            RowKeys: withKeys || allRows ? ["R1"] : null,
            AllRows: allRows,
            ColumnCodes: withCols ? ["Volume"] : null,
            AllColumns: !withCols || allCols,
            BackgroundHex: "#FFFF00");

        var ex = Assert.Throws<BusinessRuleException>(scope.EnsureValid);
        Assert.Equal("err.ECR-CELL-4224.scopeRows", ex.Details!["messageKey"]);
    }

    [Fact]
    public void Форма_області_вимагає_зміну_або_reset_і_валідний_колір()
    {
        var empty = Assert.Throws<BusinessRuleException>(
            new CellStyleScope(AllRows: true, AllColumns: true).EnsureValid);
        Assert.Equal("err.ECR-CELL-4224.scopeEmptyChange", empty.Details!["messageKey"]);

        var color = Assert.Throws<BusinessRuleException>(
            new CellStyleScope(AllRows: true, AllColumns: true, BackgroundHex: "red").EnsureValid);
        Assert.Equal("err.ECR-CELL-4224.scopeInvalidColor", color.Details!["messageKey"]);

        new CellStyleScope(AllRows: true, AllColumns: true, Reset: true).EnsureValid();
    }

    private Task<PatchCellStylesResponse> HandleAsync(CellStyleScope scope)
        => HandleAsync(new PatchCellStylesRequest(Period, [], false, scope));

    private Task<PatchCellStylesResponse> HandleAsync(PatchCellStylesRequest request)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        var handler = new PatchCellStylesHandler(
            _rows, _styles, _metadata, _access, _gate, _uow, _audit, user, clock, _assignments);
        return handler.HandleAsync(
            Document, new TableInstanceRef(TableInstance, Document, TableDef, 2, Period), request, CancellationToken.None);
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = UserId,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(StringComparer.Ordinal),
        RoleIds = new HashSet<int>(),
    };

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
