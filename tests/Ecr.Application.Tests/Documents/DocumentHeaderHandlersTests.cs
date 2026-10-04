using System.Reflection;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// <c>GET</c>/<c>PATCH …/documents/{id}/header</c> — читання й запис значень
/// шапки документа. Право на <c>PATCH</c> — грант <c>Write</c> на проєкт
/// (той самий рівень грануляції, що <see cref="ChangeDocumentKeyHandler"/>),
/// БЕЗ окремого функціонального права — той самий підхід, що
/// <c>PatchCellsHandler</c>. Стан, аудит, перерахунок і версія — <c>C2</c>
/// enterprise-аудиту; наскрізно їх же перевіряє <c>DocumentHeaderPatchTests</c>
/// (`Ecr.Api.Tests`, справжній HTTP і SQL Server).
/// </summary>
public sealed class DocumentHeaderHandlersTests
{
    private const long DocumentId = 501;
    private const int ProjectId = 10;
    private const int TemplateVersionId = 1;
    private const int SheetDefId = 20;

    private static readonly DateTime Now = new(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IDocumentKeyStore _documentLock = Substitute.For<IDocumentKeyStore>();
    private readonly IDocumentDeletionStore _workflowFacts = Substitute.For<IDocumentDeletionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly Project _project = NewProject();

    /// <summary>Чи виконується зараз тіло <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>.</summary>
    private bool _inTransaction;

    private readonly HeaderFieldDef _area = new(
        TemplateVersionId, EcrCode.Create("Area"),
        new LocalizedText(new Dictionary<string, string> { ["en"] = "Area" }), 0, CellDataType.String);

    private readonly HeaderFieldDef _count = new(
        TemplateVersionId, EcrCode.Create("Count"),
        new LocalizedText(new Dictionary<string, string> { ["en"] = "Count" }), 1, CellDataType.Int);

    private const int PermitRegistryDefId = 7;

    private readonly HeaderFieldDef _permit = new(
        TemplateVersionId, EcrCode.Create("Permit"),
        new LocalizedText(new Dictionary<string, string> { ["en"] = "Permit" }), 2, CellDataType.Lookup);

    public DocumentHeaderHandlersTests()
    {
        _permit.SetLookup(PermitRegistryDefId);

        // ⚠ Ідентифікатори, як їх дала б база: запис і журнал ключують поле за Id.
        var id = typeof(Entity<int>).GetProperty("Id")!;
        id.SetValue(_area, 1);
        id.SetValue(_count, 2);
        id.SetValue(_permit, 3);

        _user.UserId.Returns(9);
        _user.CorrelationId.Returns("corr-1");
        _clock.UtcNow.Returns(Now);

        _documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummary(
                DocumentId, ProjectId, "FILE-1", DateTime.UtcNow, SheetCount: 1,
                new Dictionary<string, string>(StringComparer.Ordinal)));
        _documents.GetTemplateVersionIdAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(TemplateVersionId);

        _metadataCache.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            TemplateVersionId, 0, [], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>())
        {
            HeaderFields = [_area, _count, _permit],
        });

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Document.View")
                .Grant(ResourceKind.Project, ProjectId, GrantLevel.Write)
                .Build());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentHeaderValueData>());

        _periods.FindProjectAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(_project);
        _workflowFacts.LockWorkflowFactsAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new DocumentWorkflowFacts([], HasWorkflowHistory: false));

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                _inTransaction = true;
                try
                {
                    await call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1));
                }
                finally
                {
                    _inTransaction = false;
                }
            });
    }

    private GetDocumentHeaderHandler Get() => new(_documents, _metadataCache, _headers, _access, _user);

    private PatchDocumentHeaderHandler Patch() => new(
        _documents, _metadataCache, _headers, _access, _user,
        _periods, _documentLock, _workflowFacts, _uow, _audit, _jobs, _clock);

    /// <summary>Версія, яку клієнт отримав би з <c>GET</c> на цьому стані шапки.</summary>
    private async Task<string> VersionOfAsync(Dictionary<int, DocumentHeaderValueData> values)
    {
        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(values);
        return (await Get().HandleAsync(DocumentId, CancellationToken.None)).Version;
    }

    /// <summary>Стан шапки до правки (читання в транзакції) і після неї (відповідь).</summary>
    private void Values(Dictionary<int, DocumentHeaderValueData> before, Dictionary<int, DocumentHeaderValueData> after)
        => _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(before, after);

    private static PatchDocumentHeaderRequest Request(string version, params PatchHeaderField[] fields)
        => new(fields, version);

    private static Project NewProject()
    {
        var project = new Project(
            EcrCode.Create("PRJ"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Project" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            TemplateVersionId, PeriodKind.Monthly, 1, "Asia/Atyrau");
        typeof(Entity<int>).GetProperty("Id")!.SetValue(project, ProjectId);
        return project;
    }

    /// <summary>Додає проєкту період у заданому стані (EF робить це навігацією).</summary>
    private void AddPeriod(int periodKey, PeriodState state)
    {
        var key = new PeriodKey(periodKey);
        var period = new Period(
            ProjectId, key, (byte)key.Sequence,
            new DateOnly(key.Year, key.Sequence, 1),
            new DateOnly(key.Year, key.Sequence, DateTime.DaysInMonth(key.Year, key.Sequence)));
        period.AdvanceTo(state, Now);

        var periods = (List<Period>)typeof(Project)
            .GetField("_periods", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(_project)!;
        periods.Add(period);
    }

    private void SheetIs(DocumentStatus status)
    {
        var state = new ApprovalState(DocumentId, SheetDefId, 202601);
        state.Submit(1, Now);
        if (status == DocumentStatus.Approved)
        {
            state.Approve(1, Now);
        }

        _workflowFacts.LockWorkflowFactsAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new DocumentWorkflowFacts([state], HasWorkflowHistory: true));
    }

    private async Task AssertNothingWrittenAsync()
    {
        await _headers.DidNotReceive().SaveValuesAsync(
            Arg.Any<long>(), Arg.Any<IReadOnlyDictionary<int, DocumentHeaderValueData>>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>());
        await _jobs.DidNotReceive().EnqueueExclusiveAsync<IRecalculationJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GET_повертає_усі_поля_версії_навіть_без_значення()
    {
        var result = await Get().HandleAsync(DocumentId, CancellationToken.None);

        Assert.Equal(3, result.Fields.Count);
        var field = Assert.Single(result.Fields, f => f.Code == "Area");
        Assert.Null(field.Value);
        Assert.False(field.IsRequired);
        Assert.Null(field.LookupRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GET_Lookup_поле_несе_LookupRegistryDefId()
    {
        var result = await Get().HandleAsync(DocumentId, CancellationToken.None);

        var permit = Assert.Single(result.Fields, f => f.Code == "Permit");
        Assert.Equal(PermitRegistryDefId, permit.LookupRegistryDefId);

        var nonLookup = Assert.Single(result.Fields, f => f.Code == "Area");
        Assert.Null(nonLookup.LookupRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GET_повертає_записане_значення()
    {
        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentHeaderValueData>
            {
                [_area.Id] = new() { ValueString = "Kashagan" },
            });

        var result = await Get().HandleAsync(DocumentId, CancellationToken.None);

        Assert.Equal("Kashagan", Assert.Single(result.Fields, f => f.Code == "Area").Value);
    }

    /// <summary>
    /// Версія залежить від ЗНАЧЕННЯ, а не від форми його запису: число з
    /// шістнадцятьма нулями з бази — та сама версія, що й без них.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GET_версія_змінюється_зі_значенням_і_не_залежить_від_масштабу_числа()
    {
        var empty = await VersionOfAsync([]);
        var a = await VersionOfAsync(new() { [_count.Id] = new() { ValueNumeric = 12.5m } });
        var scaled = await VersionOfAsync(new() { [_count.Id] = new() { ValueNumeric = 12.5000000000000000m } });
        var b = await VersionOfAsync(new() { [_count.Id] = new() { ValueNumeric = 13m } });
        var cleared = await VersionOfAsync(new() { [_count.Id] = DocumentHeaderValueData.Empty });

        Assert.Equal(a, scaled);
        Assert.Equal(4, new[] { empty, a, b, cleared }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GET_без_доступу_до_документа_відхиляється()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        // ⛔ B-08: невидимий документ — 404, як і відсутній, а не 403.
        var notFound = await Assert.ThrowsAsync<NotFoundException>(() => Get().HandleAsync(DocumentId, CancellationToken.None));
        Assert.Equal("ECR-DOC-0404", notFound.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_записує_значення_і_повертає_оновлений_стан()
    {
        var version = await VersionOfAsync([]);
        Values([], new() { [_area.Id] = new() { ValueString = "Kashagan" } });

        var result = await Patch().HandleAsync(
            DocumentId, Request(version, new PatchHeaderField("Area", "Kashagan", false)), CancellationToken.None);

        await _headers.Received(1).SaveValuesAsync(
            DocumentId,
            Arg.Is<IReadOnlyDictionary<int, DocumentHeaderValueData>>(
                d => d.Count == 1 && d[_area.Id].ValueString == "Kashagan"),
            Arg.Any<CancellationToken>());

        Assert.Equal("Kashagan", Assert.Single(result.Fields, f => f.Code == "Area").Value);
        Assert.Equal(PermitRegistryDefId, Assert.Single(result.Fields, f => f.Code == "Permit").LookupRegistryDefId);
        Assert.NotEqual(version, result.Version);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_невідомий_код_поля_дає_ECR_HDR_0404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(() => Patch().HandleAsync(
            DocumentId, Request("v", new PatchHeaderField("NoSuchField", "x", false)), CancellationToken.None));

        Assert.Equal("ECR-HDR-0404", error.ErrorCode);
        Assert.Equal("err.ECR-HDR-0404.headerField", error.Details!["messageKey"]);

        await AssertNothingWrittenAsync();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_типова_невідповідність_дає_ECR_HDR_0422()
    {
        // Count — Int; нечисловий рядок не парситься як число взагалі
        // (на відміну від String-поля, яке через HeaderValueReader.Text
        // приймає будь-яке значення через ToString — тому мішень тут саме
        // числове поле, а не Area).
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Patch().HandleAsync(
            DocumentId, Request("v", new PatchHeaderField("Count", "not-a-number", false)), CancellationToken.None));

        Assert.Equal("ECR-HDR-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_явна_порожнеча_стирає_значення()
    {
        var before = new Dictionary<int, DocumentHeaderValueData> { [_area.Id] = new() { ValueString = "Kashagan" } };
        var version = await VersionOfAsync(before);
        Values(before, new() { [_area.Id] = DocumentHeaderValueData.Empty });

        await Patch().HandleAsync(
            DocumentId, Request(version, new PatchHeaderField("Area", null, true)), CancellationToken.None);

        await _headers.Received(1).SaveValuesAsync(
            DocumentId,
            Arg.Is<IReadOnlyDictionary<int, DocumentHeaderValueData>>(d => d[_area.Id].IsEmpty),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_без_гранта_Write_на_проєкт_відхиляється()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Grant(ResourceKind.Project, ProjectId, GrantLevel.Read).Build());

        await Assert.ThrowsAsync<AccessDeniedException>(() => Patch().HandleAsync(
            DocumentId, Request("v", new PatchHeaderField("Area", "x", false)), CancellationToken.None));

        await AssertNothingWrittenAsync();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_чужого_документа_дає_404_а_не_403()
    {
        _documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
            .Returns((DocumentSummary?)null);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => Patch().HandleAsync(
            DocumentId, Request("v", new PatchHeaderField("Area", "x", false)), CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", error.ErrorCode);
    }

    [Theory]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Approved)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_шапки_поданого_чи_затвердженого_документа_відхиляється_як_комірка(DocumentStatus status)
    {
        var version = await VersionOfAsync([]);
        SheetIs(status);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => Patch().HandleAsync(
            DocumentId, Request(version, new PatchHeaderField("Area", "x", false)), CancellationToken.None));

        Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.headerLocked", error.Details!["messageKey"]);
        Assert.Equal($"Document{status}", error.Details["reason"]);
        await AssertNothingWrittenAsync();
    }

    /// <summary>
    /// Симуляція відмовляє ДО блокувань — правило <see cref="EditRules"/> питається
    /// першим разом без стану аркушів.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_у_сеансі_симуляції_відхиляється_без_блокувань()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Grant(ResourceKind.Project, ProjectId, GrantLevel.Write)
                .Build(simulation: true, simulatedFor: 44));

        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => Patch().HandleAsync(
            DocumentId, Request("v", new PatchHeaderField("Area", "x", false)), CancellationToken.None));

        Assert.Equal(nameof(EditDenyReason.SimulationReadOnly), error.Details!["reason"]);
        await _workflowFacts.DidNotReceive().LockWorkflowFactsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await AssertNothingWrittenAsync();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_коли_закрито_всі_періоди_відхиляється_а_один_закритий_не_заважає()
    {
        AddPeriod(202601, PeriodState.Closed);

        var closed = await Assert.ThrowsAsync<AccessDeniedException>(() => Patch().HandleAsync(
            DocumentId, Request("v", new PatchHeaderField("Area", "x", false)), CancellationToken.None));
        Assert.Equal(nameof(EditDenyReason.PeriodClosed), closed.Details!["reason"]);

        AddPeriod(202602, PeriodState.Open);
        var version = await VersionOfAsync([]);
        Values([], new() { [_area.Id] = new() { ValueString = "x" } });

        await Patch().HandleAsync(
            DocumentId, Request(version, new PatchHeaderField("Area", "x", false)), CancellationToken.None);

        await _headers.Received(1).SaveValuesAsync(
            DocumentId, Arg.Any<IReadOnlyDictionary<int, DocumentHeaderValueData>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_із_застарілою_версією_дає_409_і_нічого_не_пише()
    {
        var stale = await VersionOfAsync([]);
        var current = new Dictionary<int, DocumentHeaderValueData> { [_area.Id] = new() { ValueString = "Tengiz" } };
        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(current);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Patch().HandleAsync(
            DocumentId, Request(stale, new PatchHeaderField("Area", "Kashagan", false)), CancellationToken.None));

        Assert.Equal("ECR-DOC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-DOC-0409.headerStale", error.Details!["messageKey"]);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_без_baseVersion_дає_422()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Patch().HandleAsync(
            DocumentId, Request(" ", new PatchHeaderField("Area", "x", false)), CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.headerBaseVersion", error.Details!["messageKey"]);
        await AssertNothingWrittenAsync();
    }

    /// <summary>
    /// Подія журналу — у ТІЙ САМІЙ транзакції, що й запис, зі старим і новим значенням.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_пише_подію_журналу_всередині_транзакції()
    {
        var before = new Dictionary<int, DocumentHeaderValueData> { [_count.Id] = new() { ValueNumeric = 12.5000000000000000m } };
        var version = await VersionOfAsync(before);
        Values(before, new() { [_count.Id] = new() { ValueNumeric = 13m } });

        SecurityEventRecord? written = null;
        var writtenInTransaction = false;
        await _audit.WriteSecurityEventAsync(
            Arg.Do<SecurityEventRecord>(e =>
            {
                written = e;
                writtenInTransaction = _inTransaction;
            }),
            Arg.Any<CancellationToken>());

        await Patch().HandleAsync(
            DocumentId, Request(version, new PatchHeaderField("Count", 13, false)), CancellationToken.None);

        Assert.NotNull(written);
        Assert.True(writtenInTransaction, "Подія журналу записана поза транзакцією запису.");
        Assert.Equal(PatchDocumentHeaderHandler.EventType, written.EventType);
        Assert.Equal(9, written.ChangedByUserId);
        Assert.Equal("corr-1", written.CorrelationId);

        var details = System.Text.Json.JsonDocument.Parse(written.DetailsJson!).RootElement;
        Assert.Equal(DocumentId, details.GetProperty("documentId").GetInt64());
        var field = Assert.Single(details.GetProperty("fields").EnumerateArray());
        Assert.Equal("Count", field.GetProperty("code").GetString());
        Assert.Equal("12.5", field.GetProperty("oldValue").GetString());
        Assert.Equal("13", field.GetProperty("newValue").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_того_самого_значення_не_пише_журналу_й_не_ставить_перерахунку()
    {
        AddPeriod(202601, PeriodState.Open);
        var before = new Dictionary<int, DocumentHeaderValueData> { [_area.Id] = new() { ValueString = "Kashagan" } };
        var version = await VersionOfAsync(before);
        Values(before, before);

        var result = await Patch().HandleAsync(
            DocumentId, Request(version, new PatchHeaderField("Area", "Kashagan", false)), CancellationToken.None);

        Assert.Equal(version, result.Version);
        await AssertNothingWrittenAsync();
    }

    /// <summary>
    /// L1-17: PATCH шапки з грантом Write, але БЕЗ <c>Calculation.Recalculate</c>, ставить перерахунок без витіснення
    /// (Coalesced). До виправлення завжди Exclusive — обхід права на витіснення чужого виконуваного перерахунку.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task L1_17_PATCH_без_Calculation_Recalculate_ставить_перерахунок_без_витіснення()
    {
        AddPeriod(202603, PeriodState.Open);

        var version = await VersionOfAsync([]);
        Values([], new() { [_area.Id] = new() { ValueString = "Tengiz" } });

        await Patch().HandleAsync(
            DocumentId, Request(version, new PatchHeaderField("Area", "Tengiz", false)), CancellationToken.None);

        await _jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRecalculationJob>(
            default!, default, default, default);
        await _jobs.Received(1).EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(DocumentId, new PeriodKey(202603)), Arg.Any<object?>(), Arg.Any<CancellationToken>(), 9);
    }

    /// <summary>
    /// <c>HDR.*</c> читають формули — після зміни шапки перераховуються всі
    /// періоди, куди перерахунок має право писати, і лише вони.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_ставить_повний_перерахунок_на_кожен_відкритий_період_і_лише_на_них()
    {
        // L1-17: витіснення (Exclusive) — лише для власника Calculation.Recalculate.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Document.View")
                .Permission("Calculation.Recalculate")
                .Grant(ResourceKind.Project, ProjectId, GrantLevel.Write)
                .Build());

        AddPeriod(202601, PeriodState.Closed);
        AddPeriod(202602, PeriodState.Grace);
        AddPeriod(202603, PeriodState.Open);
        AddPeriod(202604, PeriodState.Scheduled);

        var version = await VersionOfAsync([]);
        Values([], new() { [_area.Id] = new() { ValueString = "Tengiz" } });

        var enqueuedInTransaction = false;
        var targets = new List<string>();
        _jobs.EnqueueExclusiveAsync<IRecalculationJob>(
                Arg.Do<string>(t =>
                {
                    targets.Add(t);
                    enqueuedInTransaction |= _inTransaction;
                }),
                Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("IRecalculationJob#1");

        await Patch().HandleAsync(
            DocumentId, Request(version, new PatchHeaderField("Area", "Tengiz", false)), CancellationToken.None);

        Assert.Equal(
            [
                RecalculateDocumentHandler.TargetOf(DocumentId, new PeriodKey(202602)),
                RecalculateDocumentHandler.TargetOf(DocumentId, new PeriodKey(202603)),
            ],
            targets);
        Assert.False(enqueuedInTransaction, "Перерахунок поставлено до коміту — задача прочитала б стару шапку.");
        await _jobs.Received(2).EnqueueExclusiveAsync<IRecalculationJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), 9);
    }
}
