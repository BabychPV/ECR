// tests/Ecr.Infrastructure.Tests/Jobs/DocumentSourceStateSwitchTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.SourceBindings;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B80 (N1): явне перемикання стану-воріт зв'язки (<see cref="SwitchDocumentSourceStateHandler"/>) на справжній базі,
/// справжній черзі задач і справжньому журналі безпеки: перевірка «проєкт джерела місячний», подія
/// <c>DocumentSourceBindingChanged</c>, злита задача перерахунку залежних і те, що вона НЕ чіпає затверджений місяць Land.
/// </summary>
/// <remarks>
/// ⚠ Рік — поточний за годинником обробника (<see cref="Now"/>); документи-джерела будуються в ньому ж.
/// ⚠ Правило 394: власний шаблон, довідник і код зв'язки на тест — робоча зв'язка HSE400 не зачіпається.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B80")]
public sealed class DocumentSourceStateSwitchTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private const int Year = 2026;
    private static readonly DateTime Now = new(Year, 11, 17, 10, 0, 0, DateTimeKind.Utc);

    /// <remarks>Мутація (B80-M6, перевірка): прибрати <c>PeriodKind != Monthly</c> — річне джерело перемикається, червоний.</remarks>
    [Fact]
    public async Task ApprovedOnly_відмовляє_доки_джерело_не_в_місячному_проєкті_і_нічого_не_пише()
    {
        var a = await ArrangeAsync(kind: PeriodKind.Yearly);

        var refusal = await Assert.ThrowsAsync<DomainException>(() => SwitchAsync(a, "approvedOnly"));

        Assert.Equal("ECR-CALC-0409", refusal.ErrorCode);
        Assert.Equal("err.ECR-CALC-0409.sourceBindingNotMonthly", refusal.Details!["messageKey"]);
        Assert.Equal(DocumentSourceState.Any, await StateAsync(a.BindingId));
        Assert.Equal(0, await EventsAsync(a.BindingId));
        Assert.Equal(0, await QueuedAsync(a));
    }

    [Fact]
    public async Task ApprovedOnly_відмовляє_без_документа_поточного_року_і_для_джерела_без_погодження()
    {
        var noDocument = await ArrangeAsync(documentYear: Year - 1);
        var none = await Assert.ThrowsAsync<DomainException>(() => SwitchAsync(noDocument, "approvedOnly"));
        Assert.Equal("err.ECR-CALC-0409.sourceBindingNoDocument", none.Details!["messageKey"]);

        var withoutWorkflow = await ArrangeAsync(workflow: WorkflowMode.None);
        var final = await Assert.ThrowsAsync<DomainException>(() => SwitchAsync(withoutWorkflow, "approvedOnly"));
        Assert.Equal("err.ECR-CALC-0409.sourceBindingWorkflowNone", final.Details!["messageKey"]);

        Assert.Equal(DocumentSourceState.Any, await StateAsync(withoutWorkflow.BindingId));
    }

    /// <remarks>
    /// Мутація (B80-M9, подія): прибрати запис <c>WriteSecurityEventAsync</c> — подія відсутня, червоний.
    /// </remarks>
    [Fact]
    public async Task Місячне_джерело_перемикається_подія_в_журналі_і_одна_злита_задача_року_повтор_нічого_не_пише()
    {
        var a = await ArrangeAsync();

        var on = await SwitchAsync(a, "approvedOnly");

        Assert.True(on.Changed);
        Assert.Equal("approvedOnly", on.Binding.SourceState);
        Assert.Equal([Year], on.QueuedYears);
        Assert.Equal(DocumentSourceState.ApprovedOnly, await StateAsync(a.BindingId));

        await using (var db = Sql.CreateContext())
        {
            var evt = Assert.Single(await EventRowsAsync(db, a.BindingId));
            Assert.Equal(a.UserId, evt.ChangedByUserId);
            Assert.Contains("\"oldState\":\"any\"", evt.DetailsJson, StringComparison.Ordinal);
            Assert.Contains("\"newState\":\"approvedOnly\"", evt.DetailsJson, StringComparison.Ordinal);
            var binding = await db.DocumentSourceBindings.AsNoTracking().SingleAsync(b => b.Id == a.BindingId);
            Assert.Equal(a.UserId, binding.UpdatedByUserId);
        }

        Assert.Equal(1, await QueuedAsync(a));

        // Той самий стан удруге — нічого: ні події, ні задачі.
        var again = await SwitchAsync(a, "approvedOnly");
        Assert.False(again.Changed);
        Assert.Empty(again.QueuedYears);
        Assert.Equal(1, await EventsAsync(a.BindingId));

        // Назад на Any — завжди дозволено (R4.6), друга подія; задача року та сама (злиття).
        var off = await SwitchAsync(a, "any");
        Assert.True(off.Changed);
        Assert.Equal(DocumentSourceState.Any, await StateAsync(a.BindingId));
        Assert.Equal(2, await EventsAsync(a.BindingId));
        Assert.Equal(1, await QueuedAsync(a));
    }

    [Fact]
    public async Task Невідомий_стан_422_неіснуюча_зв_язка_404()
    {
        var a = await ArrangeAsync();

        var invalid = await Assert.ThrowsAsync<BusinessRuleException>(() => SwitchAsync(a, "ApprovedOnly"));
        Assert.Equal("err.ECR-REQ-0422.sourceStateInvalid", invalid.Details!["messageKey"]);

        var missing = await Assert.ThrowsAsync<NotFoundException>(() => SwitchAsync(a with { BindingId = int.MaxValue }, "any"));
        Assert.Equal("err.ECR-CALC-0404.sourceBinding", missing.Details!["messageKey"]);
    }

    [Fact]
    public async Task Читання_з_документа_вимкнене_явним_ключем_перемикання_не_ставить_перерахунку()
    {
        var a = await ArrangeAsync();

        var on = await SwitchAsync(a, "approvedOnly", readOverride: false);

        Assert.True(on.Changed);
        Assert.Empty(on.QueuedYears);
        Assert.False(on.Binding.ReadFromDocumentEffective);
        Assert.Equal("configuration", on.Binding.ReadFromDocumentSource);
        Assert.Equal(0, await QueuedAsync(a));
    }

    /// <summary>
    /// ⛔ B79/D-540: затверджений місяць Land не змінюється. Задача, яку ставить перемикання, перераховує чернетку й
    /// поданий залежний, а затверджений — ні.
    /// </summary>
    [Fact]
    public async Task Задача_перемикання_не_чіпає_затверджений_місяць_Land()
    {
        var a = await ArrangeAsync();
        var draft = await ArrangeDependentAsync(a.RegistryId, status: null);
        var approved = await ArrangeDependentAsync(a.RegistryId, DocumentStatus.Approved);

        await SwitchAsync(a, "approvedOnly");
        string payload;
        await using (var db = Sql.CreateContext())
        {
            var target = $"{nameof(IRegistryImpactRecalculationJob)}~{RegistryImpactRecalculationTarget.ForSourceChange(a.BindingId, Year, null)}";
            payload = (await db.JobProgresses.AsNoTracking().SingleAsync(p => p.TargetKey == target)).Payload!;
        }

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var queued = new List<string>();
        jobs.EnqueueCoalescedAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                queued.Add(call.ArgAt<string>(0));
                return "recalc";
            });

        await using (var db = Sql.CreateContext())
        {
            var job = new RegistryImpactRecalculationJob(
                new RegistryImpactStore(db),
                new CalculationTrigger(
                    jobs, new PeriodStore(db), new WorkflowStore(db), new DocumentStore(db), new DocumentSheetCopyStore(db)),
                graph: SourceDependencyWorld.Graph(db));
            await job.ExecuteAsync(
                RegistryImpactRecalculationRequest.Parse(payload), Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        Assert.Contains(CalculationTrigger.DependentsTargetOf(draft.DocumentId, draft.PeriodKey), queued);
        Assert.DoesNotContain(CalculationTrigger.DependentsTargetOf(approved.DocumentId, approved.PeriodKey), queued);
    }

    private sealed record Arranged(int BindingId, int RegistryId, int UserId);

    /// <summary>
    /// B179 L2 perf: перемикач скидає процесний кеш воріт хука джерела — ApprovedOnly діє одразу (зв'язка зникає з
    /// воріт без очікування TTL), назад на Any — повертається; повтор того самого стану кеш не скидає.
    /// </summary>
    /// <remarks>Мутація: прибрати <c>gate?.Invalidate()</c> в обробнику — зв'язка лишається у воротах, червоний.</remarks>
    [Fact]
    public async Task Перемикання_стану_скидає_кеш_воріт_хука_джерела_одразу()
    {
        var a = await ArrangeAsync();
        var cache = new SourceBindingGateCache();

        Assert.True(await InGateAsync(cache, a.BindingId));
        var loads = cache.Loads;

        await SwitchAsync(a, "approvedOnly", gate: cache);
        Assert.False(await InGateAsync(cache, a.BindingId));
        Assert.Equal(loads + 1, cache.Loads);

        // Той самий стан — нічого не змінилось, кеш не скидається.
        await SwitchAsync(a, "approvedOnly", gate: cache);
        Assert.False(await InGateAsync(cache, a.BindingId));
        Assert.Equal(loads + 1, cache.Loads);

        await SwitchAsync(a, "any", gate: cache);
        Assert.True(await InGateAsync(cache, a.BindingId));
        Assert.Equal(loads + 2, cache.Loads);
    }

    private async Task<bool> InGateAsync(SourceBindingGateCache cache, int bindingId)
    {
        await using var db = Sql.CreateContext();
        return (await cache.GetAsync(db, CancellationToken.None)).Bindings.Any(b => b.Id == bindingId);
    }

    private async Task<SwitchDocumentSourceStateResponse> SwitchAsync(
        Arranged a, string state, bool? readOverride = true, ISourceBindingGateInvalidation? gate = null)
    {
        await using var host = NewHost();
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(a.UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = a.UserId }.Permission(DocumentSourceBindings.Permission).Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(a.UserId);
        var overrides = Substitute.For<IDocumentSourceFlagOverrides>();
        overrides.ReadFromDocument.Returns(readOverride);

        var handler = new SwitchDocumentSourceStateHandler(
            new DocumentSourceBindingStore(host.Db),
            overrides,
            access,
            user,
            new UnitOfWork(host.Db),
            new AuditWriter(host.Db),
            new DbBackgroundJobScheduler(
                host.Queue,
                new QuartzJobScheduler(null, new JobProgressStore(host.Db), new SystemClock()),
                new JobQueueSignal()),
            new TestClock(Now),
            gate);

        return await handler.HandleAsync(a.BindingId, new SwitchDocumentSourceStateRequest(state), CancellationToken.None);
    }

    /// <summary>Джерело: документ шаблону з таблицею-потоком <c>FG_Internal</c> за рік; зв'язка на Any; користувач.</summary>
    private async Task<Arranged> ArrangeAsync(
        PeriodKind kind = PeriodKind.Monthly, WorkflowMode workflow = WorkflowMode.SubmitApprove, int documentYear = Year)
    {
        var periodKey = (documentYear * 100) + 1;
        var doc = await new TestDocumentBuilder(Sql.ConnectionString).BuildAsync(periodKey: periodKey);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Sql.CreateContext();

        var table = new TableDef(
            doc.SheetDefId, EcrCode.Create("FG_Internal"), Text("FG_Internal"), 50,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();
        var instanceId = await new BulkCellLoader(Sql.ConnectionString, 1000)
            .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(new PeriodKey(periodKey), instanceId, doc.DocumentId, table.Id, Now));

        var registry = new RegistryDef(EcrCode.Create($"HC{tag}"), Text("Composition"), isTemporal: false);
        db.RegistryDefs.Add(registry);

        var user = new User($"test_b80_{tag}", $"test_b80_{tag}", AuthProvider.Local);
        user.SetPassword("not-a-real-hash-b80-test");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var binding = new DocumentSourceBinding($"B80S_{tag}", doc.TemplateId, registry.Id);
        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync();

        await db.Projects.Where(p => p.Id == doc.ProjectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.PeriodKind, kind));
        await db.TemplateVersions.Where(v => v.Id == doc.TemplateVersionId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.WorkflowMode, workflow));

        return new Arranged(binding.Id, registry.Id, user.Id);
    }

    /// <summary>Залежний документ Land: відкритий період поточного року, прогін, методологія читає довідник.</summary>
    private async Task<(long DocumentId, PeriodKey PeriodKey)> ArrangeDependentAsync(int registryId, DocumentStatus? status)
    {
        var builder = new TestDocumentBuilder(Sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: (Year * 100) + 3);
        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        if (status is { } sheet)
        {
            var approval = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            approval.Submit(userId: 5, Now);
            if (sheet == DocumentStatus.Approved)
            {
                approval.Approve(userId: 6, Now);
            }

            db.ApprovalStates.Add(approval);
        }

        // ✎ B179 L2: залежність — формула шаблону, що читає довідник зв'язки (граф), а не «є актуальний результат».
        var registryCode = await db.RegistryDefs.Where(r => r.Id == registryId).Select(r => r.Code).SingleAsync();
        var formula = new FormulaDef(
            document.TableDefId, FormulaScope.Column,
            $"REGFIELD(REGFIND('{registryCode}', [Period].Year, [Period].Sequence, 'S', 'I'), 'VALUE')",
            ExpressionDialect.Template);
        formula.AssignColumn(document.ColumnDefIds[1]);
        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync();
        return (document.DocumentId, document.PeriodKey);
    }

    private async Task<DocumentSourceState> StateAsync(int bindingId)
    {
        await using var db = Sql.CreateContext();
        return await db.DocumentSourceBindings.AsNoTracking().Where(b => b.Id == bindingId).Select(b => b.SourceState).SingleAsync();
    }

    private async Task<int> EventsAsync(int bindingId)
    {
        await using var db = Sql.CreateContext();
        return (await EventRowsAsync(db, bindingId)).Count;
    }

    private static Task<List<EventRow>> EventRowsAsync(EcrDbContext db, int bindingId)
    {
        var marker = $"%\"bindingId\":{bindingId},%";
        return db.Database.SqlQuery<EventRow>(
                $"SELECT ChangedByUserId, DetailsJson FROM aud.SecurityEvent WHERE EventType = {DocumentSourceBindings.ChangedEventType} AND DetailsJson LIKE {marker}")
            .ToListAsync();
    }

    private sealed class EventRow
    {
        public int ChangedByUserId { get; set; }

        public string DetailsJson { get; set; } = string.Empty;
    }

    private async Task<int> QueuedAsync(Arranged a)
    {
        // ✎ B179 L2: ціль «зв'язка × рік» (споживачі — з графа), а не «довідник × рік».
        var target = $"{nameof(IRegistryImpactRecalculationJob)}~{RegistryImpactRecalculationTarget.ForSourceChange(a.BindingId, Year, null)}";
        await using var db = Sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().CountAsync(p => p.TargetKey == target);
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
