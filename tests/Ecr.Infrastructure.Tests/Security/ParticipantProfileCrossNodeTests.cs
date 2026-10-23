// tests/Ecr.Infrastructure.Tests/Security/ParticipantProfileCrossNodeTests.cs
using Ecr.Application.Common;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
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
/// D-443 debt: ознака учасника документа (<c>AccessProfile.HasDocumentParticipations</c>) у кешованому профілі не
/// застаріває на ІНШОМУ вузлі. Вузол — окремий <see cref="MemoryCache"/> + <see cref="AccessProfileCache"/> (кеш
/// профілів — пам'ять процесу; спільного кешу між вузлами немає).
/// </summary>
/// <remarks>
/// <para>Факт до виправлення: ознака рахувалась лише при побудові профілю, а видача надання скидала профіль
/// одержувача лише в пам'яті вузла, що обробив видачу (<c>AccessProfileCache.InvalidateUser</c>). На іншому вузлі
/// профіль із <c>false</c> жив до сплину (<c>Cache:AccessProfileSlidingMinutes</c>, 60 хв) — документ був схований.</para>
/// <para>Мутаційний доказ (локально): у <c>AccessDecisionService.BuildProfileAsync</c> замінити
/// <c>var cacheVariant = ParticipationKeyed(groupsFingerprint, participates);</c> на
/// <c>var cacheVariant = groupsFingerprint;</c> — червоніють обидва тести (вузол А віддає профіль, закешований до
/// видачі/зняття).</para>
/// </remarks>
[Collection("SqlServer")]
public sealed class ParticipantProfileCrossNodeTests(SqlServerFixture sql) : IDisposable
{
    private const int PeriodKeyValue = 202610;
    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _nodeA = new(new MemoryCacheOptions());
    private readonly MemoryCache _nodeB = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose()
    {
        _nodeA.Dispose();
        _nodeB.Dispose();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Надання_видане_на_іншому_вузлі_відкриває_документ_на_наступному_запиті_без_скидання_тут()
    {
        var (builder, doc, delegatorId, participantId) = await ArrangeAsync();

        // Вузол А: профіль учасника закешовано ДО видачі — документа не видно.
        await using (var db = builder.CreateContext())
        {
            var service = Service(db, _nodeA);
            var before = await service.BuildProfileAsync(participantId, CancellationToken.None);
            Assert.False(before.HasDocumentParticipations);
            Assert.Same(before, await service.BuildProfileAsync(participantId, CancellationToken.None));
            Assert.False((await service.CanReadDocumentAsync(before, doc.DocumentId, CancellationToken.None)).IsAllowed);
        }

        // Вузол Б: видача (коміт) і скидання — лише в ЙОГО пам'яті, як робить UpsertEditAssignmentHandler.
        await using (var db = builder.CreateContext())
        {
            var assignment = new EditAssignment(
                doc.DocumentId,
                EditAssignmentSlice.Create(sheetCode: null, tableCode: null, monthNumber: null, periodKey: null),
                isExclusive: false, delegatedByUserId: delegatorId, handoffId: Guid.NewGuid(),
                createdByUserId: delegatorId, utcNow: Now, level: GrantLevel.Read);
            assignment.AddRecipient(participantId, delegatorId, Now);
            new EditAssignmentStore(db).Add(assignment);
            await db.SaveChangesAsync(CancellationToken.None);

            await Service(db, _nodeB).InvalidateProfileAsync(participantId, CancellationToken.None);
        }

        // ⛔ Вузол А, свіжий запит без жодного скидання тут: учасник бачить документ.
        await using (var db = builder.CreateContext())
        {
            var service = Service(db, _nodeA);
            var after = await service.BuildProfileAsync(participantId, CancellationToken.None);
            Assert.True(after.HasDocumentParticipations);
            Assert.True((await service.CanReadDocumentAsync(after, doc.DocumentId, CancellationToken.None)).IsAllowed);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Зняття_останнього_надання_на_іншому_вузлі_знімає_ознаку_учасника_на_наступному_запиті()
    {
        var (builder, doc, delegatorId, participantId) = await ArrangeAsync();
        long assignmentId;
        await using (var db = builder.CreateContext())
        {
            var assignment = new EditAssignment(
                doc.DocumentId,
                EditAssignmentSlice.Create(sheetCode: null, tableCode: null, monthNumber: null, periodKey: null),
                isExclusive: false, delegatedByUserId: delegatorId, handoffId: Guid.NewGuid(),
                createdByUserId: delegatorId, utcNow: Now, level: GrantLevel.Read);
            assignment.AddRecipient(participantId, delegatorId, Now);
            new EditAssignmentStore(db).Add(assignment);
            await db.SaveChangesAsync(CancellationToken.None);
            assignmentId = assignment.Id;
        }

        await using (var db = builder.CreateContext())
        {
            var cached = await Service(db, _nodeA).BuildProfileAsync(participantId, CancellationToken.None);
            Assert.True(cached.HasDocumentParticipations);
        }

        // Вузол Б знімає надання (шляхом сховища).
        await using (var db = builder.CreateContext())
        {
            var store = new EditAssignmentStore(db);
            store.Remove((await store.FindAsync(doc.DocumentId, assignmentId, CancellationToken.None))!);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = builder.CreateContext())
        {
            var service = Service(db, _nodeA);
            var after = await service.BuildProfileAsync(participantId, CancellationToken.None);
            Assert.False(after.HasDocumentParticipations);
            Assert.False((await service.CanReadDocumentAsync(after, doc.DocumentId, CancellationToken.None)).IsAllowed);
        }
    }

    private async Task<(TestDocumentBuilder Builder, TestDocument Doc, int DelegatorId, int ParticipantId)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 1, rowCount: 1, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var delegator = NewUser();
        var participant = NewUser();
        db.Users.AddRange(delegator, participant);
        await db.SaveChangesAsync(CancellationToken.None);

        return (builder, doc, delegator.Id, participant.Id);
    }

    private static AccessDecisionService Service(EcrDbContext db, MemoryCache node)
        => new(
            db,
            new MetadataCache(node, db),
            // ⚠ Один кеш профілів на «вузол»: як у проді — Singleton на процес над спільним IMemoryCache.
            new AccessProfileCache(node),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));

    private static User NewUser()
    {
        var user = new User($"pxn_{Guid.NewGuid():N}"[..20], "D-443 cross-node test user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        return user;
    }
}
