// tests/Ecr.Infrastructure.Tests/Security/SheetCopyAccessTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.Infrastructure.Tests.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// RS (рішення людини 03.11): копія аркуша має ВЛАСНИЙ стан за період — затвердження оригіналу її не замикає; і власне
/// блокування — подання копії не чекає на правки оригіналу й навпаки.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ (локально): прибрати <c>&amp;&amp; a.SheetCopyId == sheetCopyId</c> зі стану аркуша в
/// <c>AccessDecisionService.BuildContextAsync</c> — червоніє <see cref="Затвердження_оригіналу_не_замикає_копію"/>
/// (копія бачить <c>Approved</c> оригіналу); прибрати суфікс <c>:c{copy}</c> у <c>SheetEditGate.ResourceOf</c> —
/// червоніє <see cref="Подання_копії_не_блокує_оригінал_і_блокує_саму_копію"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetCopyAccessTests(SqlServerFixture sql) : IDisposable
{
    /// <summary>Період; не 2026-05…07 — їх архівують <c>ArchiveJobTests</c>.</summary>
    private const int PeriodKeyValue = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Затвердження_оригіналу_не_замикає_копію()
    {
        var (doc, copyId, copyInstance) = await ArrangeAsync();

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, PeriodKeyValue);
            state.Submit(userId: 1, Now);
            state.Approve(userId: 2, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        await using var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var service = Service(check);
        var profile = new AccessBuilder().Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Write).Build();

        var original = await service.CanEditSliceAsync(profile, doc.TableInstanceId, CancellationToken.None);
        var copy = await service.CanEditSliceAsync(profile, copyInstance, CancellationToken.None);

        Assert.NotEmpty(original);
        Assert.All(original.Values, d => Assert.False(d.IsAllowed));
        Assert.NotEmpty(copy);
        Assert.All(copy.Values, d => Assert.True(d.IsAllowed, d.Reason.ToString()));

        // Та сама відповідь під блокуванням (стан читається після applock).
        await using var tx = await check.Database.BeginTransactionAsync();
        var gate = new SheetEditGate(check);
        Assert.Equal(
            DocumentStatus.Approved,
            await gate.EnterEditAsync(doc.DocumentId, doc.SheetDefId, new PeriodKey(PeriodKeyValue), CancellationToken.None));
        Assert.Equal(
            DocumentStatus.Draft,
            await gate.EnterCopyEditAsync(doc.DocumentId, doc.SheetDefId, new PeriodKey(PeriodKeyValue), copyId, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "RS-1103")]
    public async Task Подання_копії_не_блокує_оригінал_і_блокує_саму_копію()
    {
        var (doc, copyId, _) = await ArrangeAsync();
        var period = new PeriodKey(PeriodKeyValue);
        var policy = new SheetEditGatePolicy(TimeSpan.FromSeconds(1));
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        // ⚠ Ім'я ресурсу оригіналу не змінилося (його беруть і писарі повз SheetEditGate).
        Assert.Equal(
            $"ecr:sheet-edit:{doc.DocumentId}:{doc.SheetDefId}:{PeriodKeyValue}",
            SheetEditGate.ResourceOf(doc.DocumentId, doc.SheetDefId, PeriodKeyValue));

        await using var submitter = builder.CreateContext();
        await using var submitTx = await submitter.Database.BeginTransactionAsync();
        await new SheetEditGate(submitter, policy)
            .EnterCopySubmitAsync(doc.DocumentId, doc.SheetDefId, period, copyId, CancellationToken.None);

        // Оригінал: правка йде одразу.
        await using (var editor = builder.CreateContext())
        {
            await using var tx = await editor.Database.BeginTransactionAsync();
            Assert.Equal(
                DocumentStatus.Draft,
                await new SheetEditGate(editor, policy).EnterEditAsync(doc.DocumentId, doc.SheetDefId, period, CancellationToken.None));
        }

        // Та сама копія: правка чекає на подання копії і відмовляє «зайнято».
        await using (var editor = builder.CreateContext())
        {
            await using var tx = await editor.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => new SheetEditGate(editor, policy)
                .EnterCopyEditAsync(doc.DocumentId, doc.SheetDefId, period, copyId, CancellationToken.None));
        }
    }

    /// <summary>Документ із відкритим періодом, копією аркуша і екземпляром копії за період.</summary>
    private async Task<(TestDocument Doc, long CopyId, long CopyInstance)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, ct: CancellationToken.None);
        await builder.OpenPeriodAsync(doc);
        var copyId = await SheetCopyInstanceTests.AddCopyAsync(builder, doc);

        await using var db = builder.CreateContext();
        await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(Now))
            .EnsureTableInstancesAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);
        var copyInstance = await db.TableInstances
            .Where(t => t.PeriodKeyValue == PeriodKeyValue && t.SheetCopyId == copyId)
            .Select(t => t.Id)
            .SingleAsync();

        return (doc, copyId, copyInstance);
    }

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));
}
