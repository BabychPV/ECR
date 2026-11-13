// tests/Ecr.Infrastructure.Tests/Security/RecallScopedGrantTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Security;
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
/// C-7 (аудит 2026-11): відкликання (BE-31, D-285) вимагає від автора рівно того, що подання, — у ПОВНОМУ контексті
/// рішення (код аркуша, період, участь). Справжні база й <c>AccessDecisionService</c>.
/// </summary>
/// <remarks>
/// Роль, звужена аркушем чи періодом (D-214), діє лише з кодом аркуша й періодом у контексті
/// (<c>NarrowedAccess.AppliesTo</c>): старий <c>RecallSheetHandler.HasSubmitGrant</c> будував контекст без них — шар
/// не діяв, рівень <c>None</c>, автор не міг відкликати власне подання.
/// Мутація: у <c>AccessDecisionService.HasSubmitGrantAsync</c> прибрати <c>WithSheetCodeAsync</c> → червоніє кейс
/// «аркушем»; повернути контекст без <c>Period</c> (<c>context with { Period = null }</c>) → червоніє кейс «періодом».
/// </remarks>
[Collection("SqlServer")]
public sealed class RecallScopedGrantTests(SqlServerFixture sql) : IDisposable
{
    private static readonly DateTime Now = new(2026, 2, 10, 6, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Автор_зі_звуженою_аркушем_роллю_Submit_відкликає_своє_подання()
    {
        var (builder, doc, userId) = await ArrangeSubmittedAsync();
        var profile = Narrowed(userId, doc, sheetCodes: [doc.SheetCode], period: null, GrantLevel.Submit);

        await using (var db = builder.CreateContext())
        {
            Assert.True(await Service(db).HasSubmitGrantAsync(
                profile, doc.DocumentId, doc.SheetDefId, doc.PeriodKey, null, CancellationToken.None));

            // Кнопка — те саме рішення, що й дія.
            Assert.True((await Handler(db, doc, userId, profile)
                    .CanRecallAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None))
                .CanRecall);
        }

        await using (var db = builder.CreateContext())
        {
            await Handler(db, doc, userId, profile)
                .HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, "не той місяць", CancellationToken.None);
        }

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(builder, doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Автор_зі_звуженою_періодом_роллю_Submit_відкликає_своє_подання()
    {
        var (builder, doc, userId) = await ArrangeSubmittedAsync();
        var profile = Narrowed(userId, doc, sheetCodes: null, period: doc.PeriodKey, GrantLevel.Submit);

        await using (var db = builder.CreateContext())
        {
            await Handler(db, doc, userId, profile)
                .HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, "не той місяць", CancellationToken.None);
        }

        Assert.Equal(DocumentStatus.Draft, await StatusAsync(builder, doc));
    }

    /// <remarks>
    /// Заперечні кейси (безпека): (1) звужена роль лише з Write і без <c>Document.Submit</c> — відмова
    /// <c>recallDenied</c>, аркуш лишається поданим; (2) шар іншого аркуша чи іншого періоду — рівня немає;
    /// (3) симуляція — ніколи.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-285")]
    public async Task Без_рівня_Submit_в_області_рішення_відкликання_відхилено()
    {
        var (builder, doc, userId) = await ArrangeSubmittedAsync();
        var writer = Narrowed(userId, doc, sheetCodes: [doc.SheetCode], period: null, GrantLevel.Write);

        await using (var db = builder.CreateContext())
        {
            var error = await Assert.ThrowsAsync<AccessDeniedException>(
                () => Handler(db, doc, userId, writer)
                    .HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, "спроба", CancellationToken.None));
            Assert.Equal("err.ECR-ACCS-0403.recallDenied", error.Details!["messageKey"]);

            var service = Service(db);
            var otherSheet = Narrowed(userId, doc, sheetCodes: ["NO_SUCH_SHEET"], period: null, GrantLevel.Submit);
            Assert.False(await service.HasSubmitGrantAsync(
                otherSheet, doc.DocumentId, doc.SheetDefId, doc.PeriodKey, null, CancellationToken.None));

            var otherPeriod = Narrowed(userId, doc, sheetCodes: null, period: new PeriodKey(doc.PeriodKey.Value + 1), GrantLevel.Submit);
            Assert.False(await service.HasSubmitGrantAsync(
                otherPeriod, doc.DocumentId, doc.SheetDefId, doc.PeriodKey, null, CancellationToken.None));

            var simulation = new AccessBuilder { UserId = userId }
                .Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Submit)
                .Build(simulation: true, simulatedFor: userId);
            Assert.False(await service.HasSubmitGrantAsync(
                simulation, doc.DocumentId, doc.SheetDefId, doc.PeriodKey, null, CancellationToken.None));
        }

        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(builder, doc));
    }

    // ── Підготовка ──────────────────────────────────────────────────────

    /// <summary>Документ з аркушем, поданим користувачем (без маршруту — відкликати можна).</summary>
    private async Task<(TestDocumentBuilder Builder, TestDocument Doc, int UserId)> ArrangeSubmittedAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(202601, rowCount: 1);

        await using var db = builder.CreateContext();
        var name = $"c7_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, null);
        state.Submit(user.Id, Now.AddHours(-1));
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();

        return (builder, doc, user.Id);
    }

    /// <summary>Профіль лише з роллю, звуженою аркушами чи періодом (D-214), з грантом на проєкт.</summary>
    private static AccessProfile Narrowed(
        int userId, TestDocument doc, IReadOnlyCollection<string>? sheetCodes, PeriodKey? period, GrantLevel level)
    {
        const int roleId = 9_701;
        var layer = new NarrowedAccess(
            sheetCodes is null ? null : new HashSet<string>(sheetCodes, StringComparer.Ordinal),
            period,
            period,
            new Dictionary<string, GrantLevel> { [$"{ResourceKind.Project}:{doc.ProjectId}"] = level },
            new HashSet<string>(),
            new HashSet<int> { roleId },
            new HashSet<string>());

        return new AccessProfile
        {
            CacheKey = $"c7-{userId}-{level}-{Guid.NewGuid():N}",
            UserId = userId,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(),
            Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int> { roleId },
            UnscopedRoleIds = new HashSet<int>(),
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [doc.ProjectId] = new(
                    new Dictionary<string, GrantLevel>(), new HashSet<string>(), new HashSet<int>(), new HashSet<string>())
                {
                    Narrowed = [layer],
                },
            },
        };
    }

    private AccessDecisionService Service(EcrDbContext db)
        => new(db, new MetadataCache(_memory, db), new AccessProfileCache(_memory), new TestClock(Now),
            Substitute.For<ICurrentUser>(), new WorkflowStore(db));

    /// <summary>
    /// Обробник зі справжніми рішеннями служби; підмінено лише побудову профілю (звужений профіль задано явно, без
    /// призначень у базі) і порт документа (склад, проєкт — без маршруту).
    /// </summary>
    private RecallSheetHandler Handler(EcrDbContext db, TestDocument doc, int userId, AccessProfile profile)
    {
        var real = Service(db);
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(call => real.CanReadDocumentAsync(call.ArgAt<AccessProfile>(0), call.ArgAt<long>(1), CancellationToken.None));
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(call => real.ReadScopeAsync(call.ArgAt<AccessProfile>(0), call.ArgAt<long>(1), CancellationToken.None));
        access.HasSubmitGrantAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<long?>(),
                  Arg.Any<CancellationToken>())
              .Returns(call => real.HasSubmitGrantAsync(
                  call.ArgAt<AccessProfile>(0), call.ArgAt<long>(1), call.ArgAt<int>(2), call.ArgAt<PeriodKey>(3),
                  call.ArgAt<long?>(4), CancellationToken.None));

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.ProjectId);
        documents.GetTemplateVersionIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.TemplateVersionId);

        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);

        return new RecallSheetHandler(
            new WorkflowStore(db), documents, access, new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), user, new TestClock(Now));
    }

    private static async Task<DocumentStatus> StatusAsync(TestDocumentBuilder builder, TestDocument doc)
    {
        await using var db = builder.CreateContext();
        return await db.ApprovalStates.AsNoTracking()
            .Where(s => s.DocumentId == doc.DocumentId && s.SheetDefId == doc.SheetDefId && s.SheetCopyId == null)
            .Select(s => s.Status)
            .SingleAsync();
    }
}
