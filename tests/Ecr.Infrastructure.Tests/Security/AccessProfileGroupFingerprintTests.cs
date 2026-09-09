// tests/Ecr.Infrastructure.Tests/Security/AccessProfileGroupFingerprintTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
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
/// `Q-187`: реальний ключ <see cref="IMemoryCache"/> мусить включати відбиток
/// груп, інакше власна сесія користувача й перегляд/симуляція адміністратором
/// того самого <c>userId</c> діляться ОДНИМ записом кешу протягом
/// <c>AccessProfileCache.Lifetime</c> (30 хв).
/// </summary>
/// <remarks>
/// ⛔ Атака (не гіпотеза, `Q-187`): `RoleAssignment` на AD-групу дає право
/// лише тоді, коли профіль будує ВЛАСНА сесія користувача (у неї є токен із
/// групами). Перегляд адміністратором чи симуляція того самого
/// <c>userId</c> будує профіль БЕЗ груп (`P-02`) — і має отримати СПРАВЖНІЙ
/// профіль без групового права, а не той, що вже лежить у кеші під тим самим
/// ключем.
/// </remarks>
[Collection("SqlServer")]
public sealed class AccessProfileGroupFingerprintTests(SqlServerFixture sql) : IDisposable
{
    private const string GroupSid = "S-1-5-21-000111-222333-9001";

    /// <summary>
    /// Право із seed (`09-seed.sql`) — потрібне лише як позначка «профіль
    /// узяв групову роль», FK на `sec.Permission` вимагає існуючого коду.
    /// </summary>
    private const string GrantedPermission = "Template.View";

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Q-187")]
    public async Task Власна_сесія_і_перегляд_адміністратором_того_самого_userId_не_ділять_запис_кешу()
    {
        int subjectId;
        string stamp;

        await using (var setup = sql.CreateContext())
        {
            var subject = User.CreateDomain(
                "q187.subject", "Q-187 Subject", "S-1-5-21-000111-222333-1001", DateTime.UtcNow);
            setup.Users.Add(subject);

            var role = new Role(EcrCode.Create("Q187GROUPROLE"), Text("Q-187 group role"));
            setup.Roles.Add(role);
            await setup.SaveChangesAsync(CancellationToken.None);

            // Право видається ЛИШЕ через групове призначення — жодного
            // RoleAssignment на самого subject-а немає.
            setup.RolePermissions.Add(new RolePermission(role.Id, GrantedPermission));
            setup.RoleAssignments.Add(new RoleAssignment(role.Id, userId: null, principalSid: GroupSid));
            await setup.SaveChangesAsync(CancellationToken.None);

            subjectId = subject.Id;
            stamp = subject.SecurityStamp;
        }

        // Виклик 1 — сам subject у ВЛАСНІЙ сесії: токен несе групу, тож
        // RoleAssignment на групу підмішує групову роль і її право.
        var ownSession = Substitute.For<ICurrentUser>();
        ownSession.UserId.Returns(subjectId);
        ownSession.GroupSids.Returns([GroupSid]);

        await using (var db = sql.CreateContext())
        {
            var own = await Service(db, ownSession).BuildProfileAsync(subjectId, CancellationToken.None);

            Assert.Equal(stamp, own.SecurityStamp);
            Assert.Contains(GrantedPermission, own.Permissions);
        }

        // Виклик 2 — адміністратор переглядає / симулює ТОГО САМОГО subject:
        // currentUser.UserId інший, тому LoadAsync свідомо не бере групи
        // («токена в нас немає» — P-02), і групова роль права не дає.
        var adminViewer = Substitute.For<ICurrentUser>();
        adminViewer.UserId.Returns(subjectId + 1);
        adminViewer.GroupSids.Returns([]);

        await using (var db = sql.CreateContext())
        {
            var viewed = await Service(db, adminViewer).BuildProfileAsync(subjectId, CancellationToken.None);

            // ⛔ Це і є Q-187: якби реальний ключ IMemoryCache ігнорував
            // відбиток груп (як до фіксу), другий виклик з ІНШИМ
            // ICurrentUser отримав би профіль ПЕРШОГО виклику з кешу разом
            // із груповим правом — цей рядок падав би.
            Assert.DoesNotContain(GrantedPermission, viewed.Permissions);
        }

        // Симетрична половина атаки з Q-187: перегляд адміністратора НЕ
        // повинен був переписати запис власної сесії. Повторний вхід тим
        // самим subject-ом і далі бачить своє групове право.
        await using (var db = sql.CreateContext())
        {
            var ownAgain = await Service(db, ownSession).BuildProfileAsync(subjectId, CancellationToken.None);

            Assert.Contains(GrantedPermission, ownAgain.Permissions);
        }
    }

    private AccessDecisionService Service(EcrDbContext db, ICurrentUser currentUser)
        => new(
            db,
            Substitute.For<IMetadataCache>(),
            new AccessProfileCache(_memory),
            new TestClock(DateTime.UtcNow),
            currentUser,
            Substitute.For<IWorkflowStore>());

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
