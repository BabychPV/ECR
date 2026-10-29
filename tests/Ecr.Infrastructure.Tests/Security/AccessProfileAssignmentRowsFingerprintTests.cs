// tests/Ecr.Infrastructure.Tests/Security/AccessProfileAssignmentRowsFingerprintTests.cs
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
/// AN-138: ключ кешу профілю доступу мусить бачити ПРЯМУ SQL-правку рядків призначень (ScopeJson, межі дії,
/// вставка/видалення), а не лише лічильники/Id. Без виклику інвалідації з застосунку.
/// </summary>
[Collection("SqlServer")]
public sealed class AccessProfileAssignmentRowsFingerprintTests(SqlServerFixture sql) : IDisposable
{
    private const string Permission = "Template.View";

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "AN-138")]
    public async Task Без_змін_рядків_профіль_береться_з_кешу()
    {
        var (userId, _, _) = await SeedAsync(groupSid: null);
        var session = Session(userId, []);

        var first = await BuildAsync(session, userId);
        var second = await BuildAsync(session, userId);

        Assert.Same(first, second);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "AN-138")]
    public async Task Пряма_SQL_правка_ScopeJson_діє_на_наступному_запиті_без_інвалідації()
    {
        var (userId, assignmentId, _) = await SeedAsync(groupSid: null);
        var session = Session(userId, []);

        var before = await BuildAsync(session, userId);
        Assert.Contains(Permission, before.Permissions);

        var scope = RoleAssignmentScope.Create([987654]).ToJson();
        await SqlAsync($"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignmentId}");

        var after = await BuildAsync(session, userId);
        Assert.NotSame(before, after);
        Assert.DoesNotContain(Permission, after.Permissions); // роль звужена до проєкту: глобального права немає
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "AN-138")]
    public async Task Пряма_SQL_правка_ValidTo_діє_на_наступному_запиті()
    {
        var (userId, assignmentId, _) = await SeedAsync(groupSid: null);
        var session = Session(userId, []);

        var before = await BuildAsync(session, userId);
        Assert.Contains(Permission, before.Permissions);

        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2).ToDateTime(TimeOnly.MinValue);
        await SqlAsync($"UPDATE sec.RoleAssignment SET ValidTo = {yesterday} WHERE Id = {assignmentId}");

        var after = await BuildAsync(session, userId);
        Assert.DoesNotContain(Permission, after.Permissions);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "AN-138")]
    public async Task Пряма_SQL_вставка_і_видалення_рядка_міняють_профіль()
    {
        var (userId, assignmentId, _) = await SeedAsync(groupSid: null);
        var session = Session(userId, []);
        var before = await BuildAsync(session, userId);

        int secondRole;
        await using (var db = sql.CreateContext())
        {
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Text("AN-138 second"));
            db.Roles.Add(role);
            await db.SaveChangesAsync(CancellationToken.None);
            secondRole = role.Id;
        }

        await SqlAsync($"INSERT INTO sec.RoleAssignment (RoleId, UserId) VALUES ({secondRole}, {userId})");
        var inserted = await BuildAsync(session, userId);
        Assert.NotSame(before, inserted);
        Assert.Contains(secondRole, inserted.RoleIds);

        await SqlAsync($"DELETE FROM sec.RoleAssignment WHERE Id = {assignmentId}");
        var deleted = await BuildAsync(session, userId);
        Assert.NotSame(inserted, deleted);
        Assert.DoesNotContain(Permission, deleted.Permissions);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "AN-138")]
    public async Task Групове_призначення_пряма_SQL_правка_ScopeJson_діє_на_наступному_запиті()
    {
        var groupSid = $"S-1-5-21-{Random.Shared.Next(1, int.MaxValue)}-138-9003";
        var (userId, _, groupAssignmentId) = await SeedAsync(groupSid, userAssignment: false);
        var session = Session(userId, [groupSid]);

        var before = await BuildAsync(session, userId);
        Assert.Contains(Permission, before.Permissions);

        var scope = RoleAssignmentScope.Create([987654]).ToJson();
        await SqlAsync($"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {groupAssignmentId}");

        var after = await BuildAsync(session, userId);
        Assert.NotSame(before, after);
        Assert.DoesNotContain(Permission, after.Permissions);
    }

    private async Task<(int UserId, int UserAssignmentId, int GroupAssignmentId)> SeedAsync(
        string? groupSid, bool userAssignment = true)
    {
        await using var db = sql.CreateContext();
        var user = User.CreateDomain(
            $"an138.{Guid.NewGuid():N}"[..20], "AN-138", $"S-1-5-21-{Guid.NewGuid():N}"[..40], DateTime.UtcNow);
        var role = new Role(EcrCode.Create($"A{Guid.NewGuid():N}"[..12]), Text("AN-138 role"));
        db.Users.Add(user);
        db.Roles.Add(role);
        await db.SaveChangesAsync(CancellationToken.None);

        db.RolePermissions.Add(new RolePermission(role.Id, Permission));
        var own = new RoleAssignment(role.Id, user.Id, null);
        var group = groupSid is null ? null : new RoleAssignment(role.Id, null, groupSid);
        if (userAssignment)
        {
            db.RoleAssignments.Add(own);
        }

        if (group is not null)
        {
            db.RoleAssignments.Add(group);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return (user.Id, userAssignment ? own.Id : 0, group?.Id ?? 0);
    }

    private static ICurrentUser Session(int userId, string[] groups)
    {
        var session = Substitute.For<ICurrentUser>();
        session.UserId.Returns(userId);
        session.GroupSids.Returns(groups);
        return session;
    }

    private async Task<Ecr.Application.Security.AccessProfile> BuildAsync(ICurrentUser session, int userId)
    {
        await using var db = sql.CreateContext();
        return await new AccessDecisionService(
                db,
                Substitute.For<IMetadataCache>(),
                new AccessProfileCache(_memory),
                new TestClock(DateTime.UtcNow),
                session,
                Substitute.For<IWorkflowStore>())
            .BuildProfileAsync(userId, CancellationToken.None);
    }

    private async Task SqlAsync(FormattableString statement)
    {
        await using var db = sql.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync(statement, CancellationToken.None);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
