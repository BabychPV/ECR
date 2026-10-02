// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Grants.cs
using System.Net;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Infrastructure.Caching;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Перенос версії й гранти на аркуш/таблицю/колонку (аудит ent6 A1): Id цих ресурсів свої в кожної
/// версії, тож без копіювання заборона на старому Id після перенесення проєкту мовчки не діє.
/// </summary>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Перенос_копіює_заборону_на_колонку_на_нові_id_і_лишає_старі_для_інших_проєктів()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var c2Old = s.Doc.ColumnDefIds[1];
        var (restricted, roleId) = await AddRestrictedUserAsync(s, (ResourceKind.Column, c2Old, true)).ConfigureAwait(true);

        // Є і наявний «дозвіл» на новому Id: заборона його перекриває, а не дублюється.
        await using (var seed = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            seed.ResourceGrants.Add(new ResourceGrant(roleId, ResourceKind.Column, s.TargetColumns["C2"], GrantLevel.Write));
            await seed.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        Assert.Equal(GrantLevel.None, await EffectiveAsync(app, restricted, s, s.Doc.SheetDefId, s.Doc.TableDefId, c2Old).ConfigureAwait(true));

        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.True(dry.GetProperty("canApply").GetBoolean(), dry.ToString());

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync().ConfigureAwait(true));

        // ⛔ Предмет тесту: на НОВИХ Id заборона діє, і лише на ній.
        using var after = new EcrApiFactory(sql);
        Assert.Equal(GrantLevel.None, await EffectiveAsync(after, restricted, s, s.TargetSheetDefId, s.TargetTableDefId, s.TargetColumns["C2"]).ConfigureAwait(true));
        Assert.True(
            await EffectiveAsync(after, restricted, s, s.TargetSheetDefId, s.TargetTableDefId, s.TargetColumns["C1"]).ConfigureAwait(true) > GrantLevel.None,
            "заборона не повинна поширитися на сусідню колонку");

        // Старі Id (інші проєкти на старій версії) не зачеплені.
        Assert.Equal(GrantLevel.None, await EffectiveAsync(after, restricted, s, s.Doc.SheetDefId, s.Doc.TableDefId, c2Old).ConfigureAwait(true));

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var grants = await db.ResourceGrants.AsNoTracking().Where(g => g.RoleId == roleId && g.ResourceKind == ResourceKind.Column).ToListAsync().ConfigureAwait(true);
        Assert.Equal(2, grants.Count);
        Assert.All(grants, g => Assert.True(g.IsDeny));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Перенос_копіює_гранти_аркуша_і_таблиці_ідемпотентно_для_наявних()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var (_, roleId) = await AddRestrictedUserAsync(
            s, (ResourceKind.Sheet, s.Doc.SheetDefId, true), (ResourceKind.Table, s.Doc.TableDefId, false)).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync().ConfigureAwait(true));

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var grants = await db.ResourceGrants.AsNoTracking()
            .Where(g => g.RoleId == roleId && g.ResourceKind != ResourceKind.Project).ToListAsync().ConfigureAwait(true);
        Assert.Equal(4, grants.Count);
        Assert.Contains(grants, g => g.ResourceKind == ResourceKind.Sheet && g.ResourceId == s.TargetSheetDefId && g.IsDeny);
        Assert.Contains(grants, g => g.ResourceKind == ResourceKind.Table && g.ResourceId == s.TargetTableDefId && !g.IsDeny);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Заборона_на_колонку_якої_нема_в_новій_версії_блокує_перенос_і_показана_в_сухому_прогоні()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        await AddRestrictedUserAsync(s, (ResourceKind.Column, s.Doc.ColumnDefIds[2], true)).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var dry = JsonDocument.Parse(await (await PostAsync(client, s, "Safe", dryRun: true).ConfigureAwait(true))
            .Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.False(dry.GetProperty("canApply").GetBoolean());
        Assert.Contains("grantsNotMapped", dry.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()));

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateGrantsNotMapped").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Профіль_у_кеші_того_самого_застосунку_бачить_заборону_на_новому_id_одразу_після_переносу()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var c2Old = s.Doc.ColumnDefIds[1];
        var (restricted, roleId) = await AddRestrictedUserAsync(s, (ResourceKind.Column, c2Old, true)).ConfigureAwait(true);
        await using (var seed = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            seed.ResourceGrants.Add(new ResourceGrant(roleId, ResourceKind.Column, s.TargetColumns["C2"], GrantLevel.Write));
            await seed.SaveChangesAsync().ConfigureAwait(true);
        }

        // Один і той самий застосунок (один кеш профілів) до й після переносу.
        using var app = new EcrApiFactory(sql);
        Assert.Equal(GrantLevel.None, await EffectiveAsync(app, restricted, s, s.Doc.SheetDefId, s.Doc.TableDefId, c2Old).ConfigureAwait(true));
        // Прогріваємо профіль: на новому Id дозвіл Write, заборони ще нема.
        Assert.True(await EffectiveAsync(app, restricted, s, s.TargetSheetDefId, s.TargetTableDefId, s.TargetColumns["C2"]).ConfigureAwait(true) > GrantLevel.None);

        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync().ConfigureAwait(true));

        Assert.Equal(GrantLevel.None, await EffectiveAsync(app, restricted, s, s.TargetSheetDefId, s.TargetTableDefId, s.TargetColumns["C2"]).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Профіль_користувача_з_ролі_через_групу_в_кеші_бачить_заборону_на_новому_id_одразу_після_переносу()
    {
        const string groupSid = "S-1-5-21-000111-222333-9777";
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var c2Old = s.Doc.ColumnDefIds[1];

        int userId;
        await using (var seed = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var user = Ecr.Domain.Entities.Security.User.CreateDomain(
                $"grp_{Guid.NewGuid():N}"[..20], "Group user", $"S-1-5-21-000111-222333-{Random.Shared.Next(10000, 99999)}", DateTime.UtcNow);
            seed.Users.Add(user);
            var role = new Role(
                EcrCode.Create($"GRPR_{Guid.NewGuid():N}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Group entry" }));
            seed.Roles.Add(role);
            await seed.SaveChangesAsync().ConfigureAwait(true);

            // Роль лише через групу: прямого призначення на користувача немає.
            seed.RoleAssignments.Add(new RoleAssignment(role.Id, userId: null, principalSid: groupSid));
            seed.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Doc.ProjectId, GrantLevel.Write));
            seed.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Column, c2Old, GrantLevel.None, isDeny: true));
            seed.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Column, s.TargetColumns["C2"], GrantLevel.Write));
            await seed.SaveChangesAsync().ConfigureAwait(true);
            userId = user.Id;
        }

        var session = Substitute.For<ICurrentUser>();
        session.UserId.Returns(userId);
        session.GroupSids.Returns([groupSid]);
        using var memory = new MemoryCache(new MemoryCacheOptions());

        async Task<GrantLevel> LevelAsync(int sheet, int table, int column)
        {
            await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
            var access = new AccessDecisionService(
                db, Substitute.For<IMetadataCache>(), new AccessProfileCache(memory),
                new TestClock(DateTime.UtcNow), session, Substitute.For<IWorkflowStore>());
            var profile = await access.BuildProfileAsync(userId, CancellationToken.None).ConfigureAwait(false);
            return EditRules.Effective(profile, default(CellAccessContext) with
            {
                ProjectId = s.Doc.ProjectId,
                SheetDefId = sheet,
                TableDefId = table,
                ColumnDefId = column,
                SheetCode = s.Doc.SheetCode,
            });
        }

        // Прогріваємо кеш: на новому Id поки дозвіл Write.
        Assert.Equal(GrantLevel.None, await LevelAsync(s.Doc.SheetDefId, s.Doc.TableDefId, c2Old).ConfigureAwait(true));
        Assert.True(await LevelAsync(s.TargetSheetDefId, s.TargetTableDefId, s.TargetColumns["C2"]).ConfigureAwait(true) > GrantLevel.None);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync().ConfigureAwait(true));

        // Той самий кеш: відбиток груп мусить змінитися, і deny діє одразу.
        Assert.Equal(GrantLevel.None, await LevelAsync(s.TargetSheetDefId, s.TargetTableDefId, s.TargetColumns["C2"]).ConfigureAwait(true));
    }


    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Пряма_роль_плюс_групи_профіль_у_кеші_застосунку_бачить_заборону_одразу_після_переносу()
    {
        const string groupSid = "S-1-5-21-000111-222333-9888";
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var c2Old = s.Doc.ColumnDefIds[1];
        var (userId, roleId) = await AddRestrictedUserAsync(s, (ResourceKind.Column, c2Old, true)).ConfigureAwait(true);
        await using (var seed = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            seed.ResourceGrants.Add(new ResourceGrant(roleId, ResourceKind.Column, s.TargetColumns["C2"], GrantLevel.Write));
            await seed.SaveChangesAsync().ConfigureAwait(true);
        }

        var session = Substitute.For<ICurrentUser>();
        session.UserId.Returns(userId);
        session.GroupSids.Returns([groupSid]); // відбиток груп непорожній, а ключ запису його містить

        using var app = new EcrApiFactory(sql);
        var cache = app.Services.GetRequiredService<AccessProfileCache>();

        async Task<GrantLevel> LevelAsync(int sheet, int table, int column)
        {
            await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
            var access = new AccessDecisionService(
                db, Substitute.For<IMetadataCache>(), cache, new TestClock(DateTime.UtcNow), session, Substitute.For<IWorkflowStore>());
            var profile = await access.BuildProfileAsync(userId, CancellationToken.None).ConfigureAwait(false);
            return EditRules.Effective(profile, default(CellAccessContext) with
            {
                ProjectId = s.Doc.ProjectId, SheetDefId = sheet, TableDefId = table, ColumnDefId = column, SheetCode = s.Doc.SheetCode,
            });
        }

        Assert.True(await LevelAsync(s.TargetSheetDefId, s.TargetTableDefId, s.TargetColumns["C2"]).ConfigureAwait(true) > GrantLevel.None);

        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync().ConfigureAwait(true));

        // Профіль із групами прогріто ДО переносу: скидання за ключем із порожнім відбитком його не зачепило б.
        Assert.Equal(GrantLevel.None, await LevelAsync(s.TargetSheetDefId, s.TargetTableDefId, s.TargetColumns["C2"]).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Перелік_користувачів_для_скидання_кешу_сигналізує_переповнення_стелі()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        await AddRestrictedUserAsync(s, (ResourceKind.Sheet, s.Doc.SheetDefId, true)).ConfigureAwait(true);
        await AddRestrictedUserAsync(s, (ResourceKind.Sheet, s.Doc.SheetDefId, true)).ConfigureAwait(true);

        var plan = new VersionMigrationPlan(
            new Dictionary<int, int> { [s.Doc.SheetDefId] = s.TargetSheetDefId },
            new Dictionary<int, int>(), [], new Dictionary<int, int>(), new Dictionary<int, int>(), [], [], 0, 0, 0);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var store = new Ecr.Infrastructure.Persistence.DocumentVersionMigrationStore(db);

        var capped = await store.ListUsersWithGrantsAsync(plan, 1, CancellationToken.None).ConfigureAwait(true);
        Assert.True(capped.Overflow);
        Assert.Single(capped.Ids);

        var enough = await store.ListUsersWithGrantsAsync(plan, 10, CancellationToken.None).ConfigureAwait(true);
        Assert.False(enough.Overflow);
        Assert.Equal(2, enough.Ids.Count);
    }

    private async Task<(int UserId, int RoleId)> AddRestrictedUserAsync(
        Scenario s, params (ResourceKind Kind, int Id, bool Deny)[] grants)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var name = $"grnt_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(
            EcrCode.Create($"GRNT_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Entry" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Doc.ProjectId, GrantLevel.Write));
        foreach (var (kind, id, deny) in grants)
        {
            db.ResourceGrants.Add(deny
                ? new ResourceGrant(role.Id, kind, id, GrantLevel.None, isDeny: true)
                : new ResourceGrant(role.Id, kind, id, GrantLevel.Read));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return (user.Id, role.Id);
    }

    /// <summary>Оракул — те саме рішення, що вирішує доступ до комірки.</summary>
    private static async Task<GrantLevel> EffectiveAsync(
        EcrApiFactory app, int userId, Scenario s, int sheetId, int tableId, int columnId)
    {
        using var scope = app.Services.CreateScope();
        var access = scope.ServiceProvider.GetRequiredService<IAccessDecisionService>();
        var profile = await access.BuildProfileAsync(userId, CancellationToken.None).ConfigureAwait(false);
        return EditRules.Effective(profile, default(CellAccessContext) with
        {
            ProjectId = s.Doc.ProjectId,
            SheetDefId = sheetId,
            TableDefId = tableId,
            ColumnDefId = columnId,
            SheetCode = s.Doc.SheetCode,
        });
    }
}
