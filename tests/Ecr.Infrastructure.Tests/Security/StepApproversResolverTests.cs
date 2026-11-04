// tests/Ecr.Infrastructure.Tests/Security/StepApproversResolverTests.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
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
/// AN-59 (варіант C, D-466): «хто може діяти на поточному кроці» картки документа
/// (<see cref="StepApproversResolver"/> + <see cref="StepApproverStore"/>) — те саме рішення, що й затвердження.
/// </summary>
/// <remarks>
/// <para>⛔ Мутаційні докази (прогнані, не закомічені):</para>
/// <list type="bullet">
/// <item>прибрати <c>approveGrants.Any(...)</c> у <c>StepApproverStore</c> → червоніє
/// <c>Носій_ролі_без_Approve_гранта_на_проєкт_не_кандидат_і_не_займає_місце_в_ліміті</c> (кандидати не порожні) і
/// <c>Велика_роль_ліміт_кандидатів_100_і_до_трьох_звернень</c> (кандидатів 100 замість 0 для чужого проєкту);</item>
/// <item>пропустити <c>CanApproveOfAsync</c> у <c>StepApproversResolver</c> (додавати кожного кандидата) → червоніють
/// <c>Роль_області_іншого_проєкту_не_проходить_рішення_затвердження</c> і <c>Кожне_ім_я_у_списку_проходить_рішення_затвердження</c>.</item>
/// </list>
/// <para>
/// ⚠ D-278 («подавач не затверджує») скасоване <c>D-440</c>: подавач у списку, коли рішення затвердження його пропускає
/// (тест <c>Подавач_у_списку_рівно_тоді_коли_рішення_затвердження_його_пропускає</c>) — список не має власних правил.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class StepApproversResolverTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 1, 20, 10, 0, 0, DateTimeKind.Utc);

    private sealed record World(
        TestDocumentBuilder Builder, TestDocument Doc, int StepRoleId, int StepId, int SubmitterId, string Tag);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static User NewUser(string displayName)
    {
        var user = new User($"sa_{Guid.NewGuid():N}"[..20], displayName, AuthProvider.Local);
        user.SetPassword("not-a-real-hash");

        return user;
    }

    /// <summary>
    /// Документ з відкритим періодом, маршрут проєкту з одним кроком (роль кроку), аркуш поданий на цей крок.
    /// Грант ролі кроку на проєкт — <paramref name="grantLevel"/>.
    /// </summary>
    private async Task<World> ArrangeAsync(GrantLevel grantLevel = GrantLevel.Approve, bool submitted = true)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1, ct: CancellationToken.None);
        await builder.OpenPeriodAsync(doc);
        var tag = Guid.NewGuid().ToString("N")[..6];

        await using var db = builder.CreateContext();
        var submitter = NewUser($"Submitter {tag}");
        db.Users.Add(submitter);
        var stepRole = new Role(EcrCode.Create($"SAR_{Guid.NewGuid():N}"[..20]), Text("Step role"));
        db.Roles.Add(stepRole);
        await db.SaveChangesAsync(CancellationToken.None);

        db.ResourceGrants.Add(new ResourceGrant(stepRole.Id, ResourceKind.Project, doc.ProjectId, grantLevel));
        var route = new ApprovalRoute(EcrCode.Create($"SARR_{Guid.NewGuid():N}"[..20]), Text("Step approvers route"), doc.ProjectId);
        route.AddStep(stepRole.Id);
        db.ApprovalRoutes.Add(route);
        await db.SaveChangesAsync(CancellationToken.None);
        var stepId = route.Steps[0].Id;

        if (submitted)
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
            state.Submit(submitter.Id, Now, stepId);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return new World(builder, doc, stepRole.Id, stepId, submitter.Id, tag);
    }

    /// <summary>Користувач із ролею (за замовчуванням — роль кроку) і, за потреби, областю призначення.</summary>
    private static async Task<int> AddHolderAsync(
        World w, string displayName, int? roleId = null, RoleAssignmentScope? scope = null)
    {
        await using var db = w.Builder.CreateContext();
        var user = NewUser(displayName);
        db.Users.Add(user);
        await db.SaveChangesAsync(CancellationToken.None);

        var assignment = new RoleAssignment(roleId ?? w.StepRoleId, user.Id, null);
        if (scope is not null)
        {
            assignment.SetScope(scope);
        }

        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync(CancellationToken.None);

        return user.Id;
    }

    private static async Task DeactivateAsync(World w, int userId)
    {
        await using var db = w.Builder.CreateContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE sec.[User] SET IsActive = 0 WHERE Id = {0}", userId);
    }

    private static AccessDecisionService Service(EcrDbContext db, MemoryCache memory)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(1);
        currentUser.GroupSids.Returns([]);

        return new AccessDecisionService(
            db, new MetadataCache(memory, db), new AccessProfileCache(memory), new TestClock(Now), currentUser,
            new WorkflowStore(db));
    }

    private static async Task<(IReadOnlyList<string>? Names, bool Truncated)> ResolveAsync(World w)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        await using var db = w.Builder.CreateContext();
        var resolver = new StepApproversResolver(
            new StepApproverStore(db, new TestClock(Now), new WorkflowStore(db)), Service(db, memory));

        return await resolver.ResolveAsync(w.Doc.DocumentId, w.Doc.PeriodKey.Value, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Носій_ролі_кроку_з_грантом_у_списку_а_вимкнений_ні_імена_за_абеткою()
    {
        var w = await ArrangeAsync();
        await AddHolderAsync(w, $"Bravo {w.Tag}");
        await AddHolderAsync(w, $"alpha {w.Tag}");
        var off = await AddHolderAsync(w, $"Disabled {w.Tag}");
        await DeactivateAsync(w, off);

        var (names, truncated) = await ResolveAsync(w);

        Assert.False(truncated);
        Assert.Equal([$"alpha {w.Tag}", $"Bravo {w.Tag}"], names);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Роль_без_Approve_гранта_не_дає_імен_а_без_поданих_аркушів_null()
    {
        var read = await ArrangeAsync(GrantLevel.Read);
        await AddHolderAsync(read, $"Reader {read.Tag}");
        var none = await ArrangeAsync(submitted: false);
        await AddHolderAsync(none, $"Idle {none.Tag}");

        Assert.Equal(([], false), await ResolveAsync(read));
        Assert.Equal((null, false), await ResolveAsync(none));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Носій_ролі_без_Approve_гранта_на_проєкт_не_кандидат_і_не_займає_місце_в_ліміті()
    {
        // Роль кроку має на цьому проєкті лише Read (рівень нижче Approve): її носії — не кандидати (передфільтр), тож не
        // витісняють справжніх погоджувачів зі 100 місць.
        var w = await ArrangeAsync(GrantLevel.Read);
        await AddHolderAsync(w, $"Holder {w.Tag}");

        await using var db = w.Builder.CreateContext();
        var facts = await new StepApproverStore(db, new TestClock(Now), new WorkflowStore(db))
            .LoadAsync(w.Doc.DocumentId, w.Doc.PeriodKey.Value, 100, CancellationToken.None);

        Assert.NotNull(facts);
        Assert.Empty(facts.Candidates);
        Assert.False(facts.Capped);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Роль_області_іншого_проєкту_не_проходить_рішення_затвердження()
    {
        // Грант є, роль кроку є, призначення є — але область призначення — інший проєкт: передфільтр (SQL) цього не знає,
        // відсікає лише рішення про затвердження.
        var w = await ArrangeAsync();
        await AddHolderAsync(w, $"Inside {w.Tag}");
        await AddHolderAsync(w, $"Elsewhere {w.Tag}", scope: RoleAssignmentScope.Create([w.Doc.ProjectId + 900_000]));

        var (names, _) = await ResolveAsync(w);

        Assert.Equal([$"Inside {w.Tag}"], names);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Подавач_у_списку_рівно_тоді_коли_рішення_затвердження_його_пропускає()
    {
        // D-440: «чотири очі» скасовано — подавач із роллю кроку й грантом затверджує сам, тож і в списку.
        var w = await ArrangeAsync();
        await using (var db = w.Builder.CreateContext())
        {
            db.RoleAssignments.Add(new RoleAssignment(w.StepRoleId, w.SubmitterId, null));
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var (names, _) = await ResolveAsync(w);

        using var memory = new MemoryCache(new MemoryCacheOptions());
        await using var check = w.Builder.CreateContext();
        var service = Service(check, memory);
        var gate = await service.CanApproveAsync(
            await service.BuildProfileAsync(w.SubmitterId, CancellationToken.None),
            w.Doc.DocumentId, w.Doc.SheetDefId, w.Doc.PeriodKey, CancellationToken.None);

        Assert.True(gate.IsAllowed, gate.Reason.ToString());
        Assert.Equal([$"Submitter {w.Tag}"], names);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Кожне_ім_я_у_списку_проходить_рішення_затвердження()
    {
        var w = await ArrangeAsync();
        var ids = new Dictionary<string, int>();
        foreach (var n in new[] { "One", "Two", "Three" })
        {
            ids[$"{n} {w.Tag}"] = await AddHolderAsync(w, $"{n} {w.Tag}");
        }

        ids[$"Away {w.Tag}"] = await AddHolderAsync(
            w, $"Away {w.Tag}", scope: RoleAssignmentScope.Create([w.Doc.ProjectId + 900_000]));

        var (names, _) = await ResolveAsync(w);

        Assert.NotNull(names);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        await using var db = w.Builder.CreateContext();
        var service = Service(db, memory);
        foreach (var (name, id) in ids)
        {
            var gate = await service.CanApproveAsync(
                await service.BuildProfileAsync(id, CancellationToken.None),
                w.Doc.DocumentId, w.Doc.SheetDefId, w.Doc.PeriodKey, CancellationToken.None);

            // Паритет у обидва боки: у списку рівно ті, кого пропускає рішення.
            Assert.Equal(gate.IsAllowed, names.Contains(name));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Копія_аркуша_подана_на_свій_крок_входить_до_списку()
    {
        // Оригінал вже затверджено (не подано), подана лише копія — погоджувач її кроку в списку.
        var w = await ArrangeAsync(submitted: false);
        await AddHolderAsync(w, $"CopyApprover {w.Tag}");
        await using (var db = w.Builder.CreateContext())
        {
            var original = new DocumentSheet(w.Doc.DocumentId, w.Doc.SheetDefId);
            db.DocumentSheets.Add(original);
            await db.SaveChangesAsync(CancellationToken.None);
            var copy = original.CreateCopy(2);
            db.DocumentSheets.Add(copy);
            await db.SaveChangesAsync(CancellationToken.None);

            var state = new ApprovalState(w.Doc.DocumentId, w.Doc.SheetDefId, w.Doc.PeriodKey.Value, copy.Id);
            state.Submit(w.SubmitterId, Now, w.StepId);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var (names, _) = await ResolveAsync(w);

        Assert.Equal([$"CopyApprover {w.Tag}"], names);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Понад_двадцять_імен_обрізано_до_двадцяти_за_абеткою_з_ознакою()
    {
        var w = await ArrangeAsync();
        for (var i = 0; i < 25; i++)
        {
            await AddHolderAsync(w, string.Create(CultureInfo.InvariantCulture, $"N{i:D2} {w.Tag}"));
        }

        var (names, truncated) = await ResolveAsync(w);

        Assert.True(truncated);
        Assert.NotNull(names);
        Assert.Equal(20, names.Count);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"N00 {w.Tag}"), names[0]);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"N19 {w.Tag}"), names[^1]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Велика_роль_ліміт_кандидатів_100_і_до_трьох_звернень()
    {
        // ~2 000 носіїв ролі кроку: у рішення йде не більше 100 кандидатів, а сам передфільтр — три звернення до бази
        // (стан+проєкт+версія, маршрут, кандидати з іменами) незалежно від кількості користувачів.
        const int count = 2_000;
        const int chunk = 500;
        var w = await ArrangeAsync();
        var prefix = $"BIG{w.Tag}";
        await using var seed = w.Builder.CreateContext();
        var userIds = new List<int>(count);

        try
        {
            for (var start = 0; start < count; start += chunk)
            {
                var users = Enumerable.Range(start, chunk)
                    .Select(i => NewUser(string.Create(CultureInfo.InvariantCulture, $"{prefix} {i:D4}")))
                    .ToList();
                seed.Users.AddRange(users);
                await seed.SaveChangesAsync(CancellationToken.None);
                seed.RoleAssignments.AddRange(users.Select(u => new RoleAssignment(w.StepRoleId, u.Id, null)));
                await seed.SaveChangesAsync(CancellationToken.None);
                userIds.AddRange(users.Select(u => u.Id));
                seed.ChangeTracker.Clear();
                await seed.Database.ExecuteSqlRawAsync("CHECKPOINT");
            }

            var counter = new DbCommandCounter();
            await using var counted = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>()
                .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
                .AddInterceptors(counter).Options);
            var store = new StepApproverStore(counted, new TestClock(Now), new WorkflowStore(counted));

            // Розігрів (метадані, плани) — окремо від заміру.
            await store.LoadAsync(w.Doc.DocumentId, w.Doc.PeriodKey.Value, StepApproversResolver.MaxCandidates, CancellationToken.None);
            counter.Tally.Reset();
            var facts = await store.LoadAsync(
                w.Doc.DocumentId, w.Doc.PeriodKey.Value, StepApproversResolver.MaxCandidates, CancellationToken.None);
            var seen = counter.Tally.Snapshot();

            Assert.NotNull(facts);
            Assert.Equal(StepApproversResolver.MaxCandidates, facts.Candidates.Count);
            Assert.True(facts.Capped);
            Assert.True(seen.Total <= 3, seen.Format());

            // Ціле поле (передфільтр + профілі + рішення), холодний кеш профілів: не більше 8 звернень НЕЗАЛЕЖНО від числа
            // кандидатів (раніше — ~8 на кандидата).
            using var fieldMemory = new MemoryCache(new MemoryCacheOptions());
            var resolver = new StepApproversResolver(store, Service(counted, fieldMemory));
            counter.Tally.Reset();
            var (names, truncated) = await resolver.ResolveAsync(
                w.Doc.DocumentId, w.Doc.PeriodKey.Value, CancellationToken.None);
            var field = counter.Tally.Snapshot();
            Assert.True(field.Total <= 8, field.Format());

            Assert.True(truncated);
            Assert.NotNull(names);
            Assert.Equal(StepApproversResolver.MaxNames, names.Count);
            Assert.All(names, n => Assert.StartsWith(prefix, n, StringComparison.Ordinal));

            // Паритет: кожне ім'я з відповіді проходить рішення затвердження.
            using var memory = new MemoryCache(new MemoryCacheOptions());
            await using var db = w.Builder.CreateContext();
            var service = Service(db, memory);
            var wanted = names.ToList();
            var byName = await db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id) && wanted.Contains(u.DisplayName))
                .Select(u => new { u.Id, u.DisplayName }).ToListAsync();
            Assert.Equal(names.Count, byName.Count);
            foreach (var user in byName)
            {
                var gate = await service.CanApproveAsync(
                    await service.BuildProfileAsync(user.Id, CancellationToken.None),
                    w.Doc.DocumentId, w.Doc.SheetDefId, w.Doc.PeriodKey, CancellationToken.None);
                Assert.True(gate.IsAllowed, $"{user.DisplayName}: {gate.Reason}");
            }
        }
        finally
        {
            // Порціями з CHECKPOINT — журнал тестової бази не розростається (TestDatabaseSizeTests).
            await using var cleanup = w.Builder.CreateContext();
            foreach (var part in userIds.Chunk(chunk))
            {
                var ids = part.ToList();
                await cleanup.RoleAssignments.Where(a => a.UserId != null && ids.Contains(a.UserId.Value)).ExecuteDeleteAsync();
                await cleanup.Users.Where(u => ids.Contains(u.Id)).ExecuteDeleteAsync();
                await cleanup.Database.ExecuteSqlRawAsync("CHECKPOINT");
            }
        }
    }
}
