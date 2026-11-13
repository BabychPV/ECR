// tests/Ecr.Infrastructure.Tests/Security/AccessWindowWorkflowTests.cs
using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// B100 (рішення людини 13.11, «варіант 1»): вікно доступу (ФВ-2.16, <c>EditablePeriodOnly</c>) діє ЛИШЕ на
/// введення/правку. Подання, затвердження й повернення в роботу його не враховують. Справжні база й
/// <c>AccessDecisionService</c>.
/// </summary>
/// <remarks>
/// Поведінка до й після прибирання мертвого обчислення вікна в <c>BuildContextAsync</c> однакова: ці тести зелені на
/// обох (CanSubmit/CanApprove/CanReopen ознаки <c>OutOfAccessWindow</c> не читали). Вікно на правку тримає
/// <c>PeriodAccessRules.Evaluate</c> у <c>AccessDecisionService.Decide</c>.
/// Мутація (для AU): у <c>PeriodAccessRules.Evaluate</c> вид <c>EditablePeriodOnly</c> завжди <c>Allowed</c> →
/// червоніє <see cref="Правка_поза_вікном_відмовлена_а_подання_того_самого_аркуша_дозволене"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class AccessWindowWorkflowTests(SqlServerFixture sql) : IDisposable
{
    /// <summary>Період (номер 3) у 2099 — далеко від реальних періодів (ретро 382).</summary>
    private const int PeriodKeyValue = 209903;

    private static readonly DateTime Now = new(2099, 3, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    [Trait("Decision", "B100")]
    public async Task Правка_поза_вікном_відмовлена_а_подання_того_самого_аркуша_дозволене()
    {
        var doc = await ArrangeAsync();
        var cell = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]);
        var profile = Profile(doc);

        try
        {
            // Контроль: без правила вікна правка дозволена — відмова нижче саме від вікна.
            await using (var db = Context())
            {
                var before = await Service(db).CanEditCellAsync(profile, doc.DocumentId, cell, CancellationToken.None);
                Assert.True(before.IsAllowed, $"Без правила вікна правка мала проходити: {before.Reason} {before.Detail}");
            }

            await AddWindowOutsidePeriodAsync(doc);

            await using (var db = Context())
            {
                var service = Service(db);

                // Deny: правка комірки поза вікном — OutOfAccessWindow.
                var edit = await service.CanEditCellAsync(profile, doc.DocumentId, cell, CancellationToken.None);
                Assert.False(edit.IsAllowed);
                Assert.Equal(EditDenyReason.OutOfAccessWindow, edit.Reason);

                // Positive: подання того самого аркуша в тому самому періоді — дозволене.
                var submit = await service.CanSubmitAsync(
                    profile, doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);
                Assert.True(submit.IsAllowed, $"Подання поза вікном мало проходити: {submit.Reason} {submit.Detail}");
            }
        }
        finally
        {
            await CleanupAsync(doc);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    [Trait("Decision", "B100")]
    public async Task Затвердження_і_повернення_в_роботу_поза_вікном_дозволені()
    {
        var doc = await ArrangeAsync();
        var profile = Profile(doc);

        try
        {
            await AddWindowOutsidePeriodAsync(doc);

            await using (var db = Context())
            {
                var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, PeriodKeyValue);
                state.Submit(userId: 1, Now);
                db.ApprovalStates.Add(state);
                await db.SaveChangesAsync();
            }

            await using (var db = Context())
            {
                var service = Service(db);

                var approve = await service.CanApproveAsync(
                    profile, doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);
                Assert.True(approve.IsAllowed, $"Затвердження поза вікном мало проходити: {approve.Reason} {approve.Detail}");

                var reopenSubmitted = await service.CanReopenAsync(
                    profile, doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);
                Assert.True(
                    reopenSubmitted.IsAllowed,
                    $"Повернення поданого поза вікном мало проходити: {reopenSubmitted.Reason} {reopenSubmitted.Detail}");
            }

            await using (var db = Context())
            {
                var state = await db.ApprovalStates.SingleAsync(
                    a => a.DocumentId == doc.DocumentId && a.SheetDefId == doc.SheetDefId && a.PeriodKey == PeriodKeyValue);
                state.Approve(userId: 2, Now);
                await db.SaveChangesAsync();
            }

            await using (var db = Context())
            {
                var reopenApproved = await Service(db).CanReopenAsync(
                    profile, doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);
                Assert.True(
                    reopenApproved.IsAllowed,
                    $"Повернення затвердженого поза вікном мало проходити: {reopenApproved.Reason} {reopenApproved.Detail}");
            }
        }
        finally
        {
            await CleanupAsync(doc);
        }
    }

    /// <summary>Документ будівника з відкритим періодом (номер 3).</summary>
    private async Task<TestDocument> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, rowCount: 1);
        await builder.OpenPeriodAsync(doc);
        return doc;
    }

    /// <summary>Вікно введення аркуша документа — періоди 7…8; період документа (3) поза ним.</summary>
    private async Task AddWindowOutsidePeriodAsync(TestDocument doc)
    {
        await using var db = Context();
        db.PeriodAccessRules.Add(PeriodAccessRuleDef.EditablePeriodOnly(
            doc.TemplateVersionId, OutOfWindowBehavior.ReadOnly, fromSequence: 7, toSequence: 8)
            .ForSheet(doc.SheetDefId));
        await db.SaveChangesAsync();
    }

    /// <summary>Прибирає лише власні рядки тесту: правила вікна версії й стани аркуша документа.</summary>
    private async Task CleanupAsync(TestDocument doc)
    {
        await using var db = Context();
        await db.ApprovalStates.Where(a => a.DocumentId == doc.DocumentId).ExecuteDeleteAsync();
        await db.PeriodAccessRules.Where(r => r.TemplateVersionId == doc.TemplateVersionId).ExecuteDeleteAsync();
    }

    /// <summary>Рівень Approve на проєкт: досить і на правку (Write), і на подання, затвердження й повернення.</summary>
    private static AccessProfile Profile(TestDocument doc)
        => new AccessBuilder().Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Approve).Build();

    private EcrDbContext Context() => new TestDocumentBuilder(sql.ConnectionString).CreateContext();

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));
}
