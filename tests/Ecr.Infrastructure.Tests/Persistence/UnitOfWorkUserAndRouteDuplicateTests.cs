// tests/Ecr.Infrastructure.Tests/Persistence/UnitOfWorkUserAndRouteDuplicateTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B150: програш гонки двох однакових запитів на <c>UQ_User_Name</c> (<c>POST /users</c>) і <c>UQ_ApprovalRoute</c>
/// (перший <c>PUT /projects/{id}/approval-route</c>) — та сама відмова, що й послідовний/наявний шлях, а не сирий
/// <c>DbUpdateException</c> (500).
/// </summary>
/// <remarks>
/// Мутації: прибрати арм <c>UQ_User_Name</c> або <c>UQ_ApprovalRoute</c> з <c>UnitOfWork.TryMapDuplicateKey</c> —
/// відповідний тест червоний (сирий виняток бази). B161: арм <c>UX_User_Sid</c> називає запис програшного запиту
/// замість власника SID — тест SID червоний на <c>userName</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitOfWorkUserAndRouteDuplicateTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Дубль_імені_користувача_дає_409_userNameTaken_а_не_сирий_виняток_бази()
    {
        var name = $"b150_{Guid.NewGuid():N}"[..20];

        await using (var winner = Context())
        {
            winner.Users.Add(LocalUser(name));
            await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
        }

        await using var loser = Context();
        loser.Users.Add(LocalUser(name));

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-USR-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-USR-0409.userNameTaken", thrown.Details!["messageKey"]);
        Assert.Equal(name, thrown.Details["userName"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Дубль_SID_дає_409_windowsSidTaken_а_не_сирий_виняток_бази()
    {
        // Різні імена, той самий SID — відбиває лише UX_User_Sid (у тесті імені вище першим спрацьовує UQ_User_Name).
        var tag = Guid.NewGuid().ToString("N")[..10];
        var sid = $"S-1-5-21-2099-150-{Random.Shared.Next(100_000, 999_999)}-77";
        var now = new DateTime(2099, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        try
        {
            await using (var winner = Context())
            {
                winner.Users.Add(User.CreateDomain($"B150\\a{tag}", "A", sid, now));
                await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
            }

            await using var loser = Context();
            loser.Users.Add(User.CreateDomain($"B150\\b{tag}", "B", sid, now));

            var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
                () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

            Assert.Equal("ECR-USR-0409", thrown.ErrorCode);
            Assert.Equal("err.ECR-USR-0409.windowsSidTaken", thrown.Details!["messageKey"]);
            Assert.Equal(sid, thrown.Details["sid"]);

            // ⛔ B161: відмова називає ВЛАСНИКА SID (той, хто закомітився першим), як і послідовна перевірка
            // `WindowsSidFormat.EnsureFreeAsync`, — а не запис програшного запиту, якого в базі немає.
            Assert.Equal($"B150\\a{tag}", thrown.Details["userName"]);
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.Users.Where(u => u.WindowsSid == sid).ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B161")]
    public async Task Виправлення_SID_на_щойно_зайнятий_називає_власника_а_не_виправлюваний_запис()
    {
        // `PUT /users/{id}/windows-sid` (Modified, а не Added): перевірку обробника обійшов паралельний запит, що
        // забрав той самий SID. Відмова мусить назвати власника, а не запис, якому SID виправляли.
        var tag = Guid.NewGuid().ToString("N")[..10];
        var n = Random.Shared.Next(100_000, 999_999);
        var sid = $"S-1-5-21-2099-161-{n}-77";
        var otherSid = $"S-1-5-21-2099-161-{n}-78";
        var now = new DateTime(2099, 1, 15, 8, 0, 0, DateTimeKind.Utc);
        try
        {
            int correctedId;
            await using (var setup = Context())
            {
                setup.Users.Add(User.CreateDomain($"B161\\a{tag}", "A", sid, now));
                var corrected = User.CreateDomain($"B161\\b{tag}", "B", otherSid, now);
                setup.Users.Add(corrected);
                await new UnitOfWork(setup).SaveChangesAsync(CancellationToken.None);
                correctedId = corrected.Id;
            }

            await using var loser = Context();
            var user = await loser.Users.SingleAsync(u => u.Id == correctedId);
            user.CorrectUnconfirmedWindowsSid(sid);

            var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
                () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

            Assert.Equal("err.ECR-USR-0409.windowsSidTaken", thrown.Details!["messageKey"]);
            Assert.Equal(sid, thrown.Details["sid"]);
            Assert.Equal($"B161\\a{tag}", thrown.Details["userName"]);
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.Users.Where(u => u.WindowsSid == sid || u.WindowsSid == otherSid).ExecuteDeleteAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Дубль_маршруту_проєкту_дає_409_concurrentChange_а_не_сирий_виняток_бази()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(209912);
        var code = EcrCode.Create($"PRJ_{document.ProjectId}");

        await using (var winner = Context())
        {
            winner.ApprovalRoutes.Add(new ApprovalRoute(code, Name(), document.ProjectId));
            await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);
        }

        await using var loser = Context();
        loser.ApprovalRoutes.Add(new ApprovalRoute(code, Name(), document.ProjectId));

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
    }

    /// <summary>Локальний запис потребує хешу пароля (<c>CK_User_Provider</c>).</summary>
    private static User LocalUser(string name)
    {
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash("B150-Duplicate-2099!"));
        return user;
    }

    private static LocalizedText Name() => new(new Dictionary<string, string> { ["en"] = "B150 route" });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
