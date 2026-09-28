using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// P8 (перф-аудит): пакетний <c>CanEditSlicesAsync</c> дає ТІ САМІ рішення,
/// що й поштучний <c>CanEditSliceAsync</c>, — для кожного зрізу, кожної
/// комірки й кожного профілю.
/// </summary>
/// <remarks>
/// ⛔ Безпека тут головна, швидкість — друга. Пакетний шлях ділить між
/// зрізами спільне (знімок, умови доступу, правила періоду, рядки), і саме
/// там легко «поділити» зайве: стан ПЕРШОГО аркуша на всі, таблицю ПЕРШОГО
/// зрізу на всі, вікна чинності одного екземпляра на всі. Тому набір навмисно
/// строкатий: два документи (відкритий і закритий період), чотири аркуші
/// (чернетка, поданий, затверджений), правило <c>SourceWindow</c> на таблиці,
/// що не є першою, і вісім профілів — без грантів, <c>Write</c>, <c>Read</c>,
/// <c>Deny</c> на таблицю, на колонку, на проєкт, грант лише на таблицю, лише
/// на аркуш.
///
/// ⚠ Поштучний метод реалізований через пакетний з одним екземпляром, тож
/// ця еквівалентність стереже саме ПАКЕТНІ місця: групування, стан решти
/// аркушів групи, спільні правила й рядки. Мутації, що це доводять, — у
/// звіті коміту.
/// </remarks>
[Collection("SqlServer")]
public sealed class AccessDecisionBatchEquivalenceTests(SqlServerFixture sql) : IDisposable
{
    /// <summary>Період обох документів.</summary>
    /// <remarks>
    /// ⚠ Не 2026-05…07 (їх архівують <c>ArchiveJobTests</c>) і не 202609
    /// (<c>PeriodAccessSliceTests</c>): звільнення партиції йде по періоду.
    /// </remarks>
    private const int PeriodKeyValue = 202610;

    private const byte MonthNumber = 10;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакетні_рішення_тотожні_поштучним_для_кожного_зрізу_і_профілю()
    {
        var world = await ArrangeAsync();

        var reasons = new HashSet<EditDenyReason>();
        var allowed = 0;

        foreach (var (name, profile) in world.Profiles)
        {
            IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>> batch;
            await using (var db = world.Builder.CreateContext())
            {
                batch = await Service(db).CanEditSlicesAsync(profile, world.InstanceIds, CancellationToken.None);
            }

            Assert.Equal(world.InstanceIds.Order(), batch.Keys.Order());

            foreach (var instanceId in world.InstanceIds)
            {
                IReadOnlyDictionary<CellAddress, EditDecision> single;
                await using (var db = world.Builder.CreateContext())
                {
                    single = await Service(db).CanEditSliceAsync(profile, instanceId, CancellationToken.None);
                }

                var batched = batch[instanceId];

                Assert.NotEmpty(single);
                Assert.Equal(single.Keys.OrderBy(Key), batched.Keys.OrderBy(Key));

                foreach (var (address, expected) in single)
                {
                    var actual = batched[address];
                    var where = $"профіль «{name}», екземпляр {instanceId}, {address}";

                    Assert.True(expected.IsAllowed == actual.IsAllowed, $"{where}: IsAllowed {expected.IsAllowed} ≠ {actual.IsAllowed}");
                    Assert.True(expected.Reason == actual.Reason, $"{where}: Reason {expected.Reason} ≠ {actual.Reason}");
                    Assert.True(
                        expected.RequiresConfirmation == actual.RequiresConfirmation,
                        $"{where}: RequiresConfirmation");
                    Assert.True(string.Equals(expected.Detail, actual.Detail, StringComparison.Ordinal), $"{where}: Detail");

                    reasons.Add(expected.Reason);
                    allowed += expected.IsAllowed ? 1 : 0;
                }
            }
        }

        // ⛔ Проти хибнозеленого: набір мусить справді проходити крізь гілки,
        // які пакетний шлях ділить. Збіг «усе заборонено» з «усе заборонено»
        // нічого б не доводив.
        Assert.True(allowed > 0, "Жодної дозволеної комірки — набір не перевіряє дозволу.");
        Assert.Contains(EditDenyReason.NoGrant, reasons);
        Assert.Contains(EditDenyReason.DocumentSubmitted, reasons);
        Assert.Contains(EditDenyReason.DocumentApproved, reasons);
        Assert.Contains(EditDenyReason.PeriodClosed, reasons);
        Assert.Contains(EditDenyReason.OutsidePermitWindow, reasons);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    public async Task Пакет_бачить_Deny_на_таблицю_і_стан_кожного_аркуша_окремо()
    {
        // ⚠ Предметна перевірка тих самих випадків, що й еквівалентність вище:
        // якщо обидва шляхи колись разом «забудуть» Deny чи стан аркуша,
        // еквівалентність лишиться зеленою — а ця ні.
        var world = await ArrangeAsync();

        var profile = world.Profiles["Write + Deny таблиці E2"];

        await using var db = world.Builder.CreateContext();
        var batch = await Service(db).CanEditSlicesAsync(profile, world.InstanceIds, CancellationToken.None);

        Assert.All(batch[world.Denied.TableInstanceId].Values, d => Assert.Equal(EditDenyReason.NoGrant, d.Reason));
        Assert.All(batch[world.Submitted.TableInstanceId].Values, d => Assert.Equal(EditDenyReason.DocumentSubmitted, d.Reason));
        Assert.All(batch[world.Approved.TableInstanceId].Values, d => Assert.Equal(EditDenyReason.DocumentApproved, d.Reason));
        Assert.All(batch[world.ClosedInstanceId].Values, d => Assert.Equal(EditDenyReason.PeriodClosed, d.Reason));
        Assert.Contains(batch[world.Base.TableInstanceId].Values, d => d.IsAllowed);
    }

    /// <summary>
    /// P8 + симуляція «очима користувача X» (<c>ФВ-6.16a</c>, <c>D-96</c>):
    /// пакет через <see cref="SimulationAwareAccessDecisionService"/> дає те
    /// саме, що поштучний шлях під тією ж симуляцією, а права всередині
    /// симуляції — ті самі, що в реального X.
    /// </summary>
    /// <remarks>
    /// ⚠ Профіль підміняє ЛИШЕ <c>BuildProfileAsync</c>; обидва методи зрізу
    /// отримують профіль параметром і делегують його без змін. Тест стереже,
    /// щоб так і лишилось: пакетний шлях, який сам перебудував би профіль (чи
    /// взяв профіль того, хто симулює), розійшовся б тут.
    ///
    /// ⚠ «Ті самі рішення, що в реального X» буквально не можуть збігтися:
    /// <c>EditRules.CanEdit</c> ПЕРШОЮ відмовляє будь-який запис у симуляції
    /// (<c>SimulationReadOnly</c>, <c>D-96</c>) — інакше «подивитися очима»
    /// стало б способом писати від чужого імені. Тому порівнюється так:
    /// (1) під симуляцією КОЖНА комірка — <c>SimulationReadOnly</c>, набір
    /// комірок той самий, що в X; (2) профіль симуляції несе рівно гранти,
    /// заборони й ролі X, і той самий профіль без ознаки симуляції дає пакетом
    /// рівно рішення реального X.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P8")]
    [Trait("Requirement", "ФВ-6.16a")]
    public async Task Пакет_під_симуляцією_тотожний_поштучному_і_правам_самого_користувача()
    {
        var world = await ArrangeAsync();
        var (actorId, subjectId) = await ArrangeUsersAsync(world);

        await using var db = world.Builder.CreateContext();

        var current = new FakeCurrentUser(actorId);
        var inner = new AccessDecisionService(
            db, new MetadataCache(_memory, db), new AccessProfileCache(_memory),
            new TestClock(Now), current, new WorkflowStore(db));
        var simulation = new SimulationService(db, inner);

        var sessionId = await simulation.StartAsync(
            actorId, subjectId, "P8 simulation equivalence", CancellationToken.None);
        current.SimulationSessionId = sessionId;

        try
        {
            var aware = new SimulationAwareAccessDecisionService(inner, simulation, current);

            // ── Профіль симуляції: суб'єкт X, а не той, хто симулює ─────────
            var simulated = await aware.BuildProfileAsync(actorId, CancellationToken.None);

            Assert.True(simulated.IsSimulation);
            Assert.Equal(subjectId, simulated.UserId);
            Assert.Equal(subjectId, simulated.SimulatedForUserId);
            Assert.Equal(actorId, simulated.SimulationActorUserId);

            var real = await inner.BuildProfileAsync(subjectId, CancellationToken.None);

            Assert.False(real.IsSimulation);
            Assert.Equal(real.Grants.OrderBy(g => g.Key), simulated.Grants.OrderBy(g => g.Key));
            Assert.Equal(real.Denies.Order(), simulated.Denies.Order());
            Assert.Equal(real.RoleIds.Order(), simulated.RoleIds.Order());

            // ── (1) Пакет == поштучний під тією самою симуляцією ─────────────
            var simulatedBatch = await aware.CanEditSlicesAsync(simulated, world.InstanceIds, CancellationToken.None);
            var realBatch = await inner.CanEditSlicesAsync(real, world.InstanceIds, CancellationToken.None);

            Assert.Equal(world.InstanceIds.Order(), simulatedBatch.Keys.Order());

            foreach (var instanceId in world.InstanceIds)
            {
                var single = await aware.CanEditSliceAsync(simulated, instanceId, CancellationToken.None);

                Assert.NotEmpty(single);
                AssertSame($"симуляція, пакет vs поштучний, екземпляр {instanceId}", single, simulatedBatch[instanceId]);

                // Симуляція не ховає й не додає комірок — лише забороняє запис.
                Assert.Equal(realBatch[instanceId].Keys.OrderBy(Key), single.Keys.OrderBy(Key));
                Assert.All(single.Values, d =>
                {
                    Assert.False(d.IsAllowed);
                    Assert.Equal(EditDenyReason.SimulationReadOnly, d.Reason);
                });
            }

            // ── (2) Права всередині симуляції == права реального X ───────────
            var unmasked = new AccessProfile
            {
                CacheKey = simulated.CacheKey + "|unmasked",
                UserId = simulated.UserId,
                SecurityStamp = simulated.SecurityStamp,
                Permissions = simulated.Permissions,
                Grants = simulated.Grants,
                Denies = simulated.Denies,
                RoleIds = simulated.RoleIds,
            };

            var unmaskedBatch = await aware.CanEditSlicesAsync(unmasked, world.InstanceIds, CancellationToken.None);

            var reasons = new HashSet<EditDenyReason>();
            var allowed = 0;

            foreach (var instanceId in world.InstanceIds)
            {
                AssertSame(
                    $"права X у симуляції vs реальний X, екземпляр {instanceId}",
                    realBatch[instanceId], unmaskedBatch[instanceId]);

                foreach (var decision in realBatch[instanceId].Values)
                {
                    reasons.Add(decision.Reason);
                    allowed += decision.IsAllowed ? 1 : 0;
                }
            }

            // ⛔ Проти хибнозеленого: права X мусять справді давати і дозвіл, і
            // різні відмови — інакше збіг нічого не доводить.
            Assert.True(allowed > 0, "У X немає жодної дозволеної комірки — набір не перевіряє дозволу.");
            Assert.Contains(EditDenyReason.NoGrant, reasons);
            Assert.Contains(EditDenyReason.DocumentSubmitted, reasons);
            Assert.Contains(EditDenyReason.DocumentApproved, reasons);
            Assert.Contains(EditDenyReason.PeriodClosed, reasons);
            Assert.Contains(EditDenyReason.OutsidePermitWindow, reasons);
        }
        finally
        {
            await simulation.EndAsync(sessionId, CancellationToken.None);
        }
    }

    /// <summary>
    /// Той, хто симулює (без ролей), і суб'єкт X із роллю: <c>Write</c> на
    /// обидва проєкти, заборона колонки базової таблиці й заборона таблиці
    /// документа B — справжні рядки <c>sec.*</c>, щоб профіль X будувався з
    /// бази так само, як у продуктиві.
    /// </summary>
    private static async Task<(int ActorId, int SubjectId)> ArrangeUsersAsync(World world)
    {
        await using var db = world.Builder.CreateContext();

        var tag = Guid.NewGuid().ToString("N")[..10];
        var actor = new User($"p8sim_a_{tag}", "P8 actor", AuthProvider.Local);
        var subject = new User($"p8sim_x_{tag}", "P8 subject", AuthProvider.Local);

        // ⚠ Локальний запис без хеша пароля відхиляє `CK_User_Provider`.
        var hash = new PasswordHasher().Hash("P8-Simulation-2026!");
        actor.SetPassword(hash);
        subject.SetPassword(hash);
        db.Users.AddRange(actor, subject);

        var role = new Role(
            EcrCode.Create($"P8SIM_{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "P8 simulation subject" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync(CancellationToken.None);

        var closed = await db.TableInstances
            .Where(t => t.Id == world.ClosedInstanceId)
            .Join(db.Documents, t => t.DocumentId, d => d.Id, (t, d) => new { d.ProjectId, t.TableDefId })
            .FirstAsync(CancellationToken.None);

        db.RoleAssignments.Add(new RoleAssignment(role.Id, subject.Id, null));
        db.ResourceGrants.AddRange(
            new ResourceGrant(role.Id, ResourceKind.Project, world.Base.ProjectId, GrantLevel.Write),
            new ResourceGrant(role.Id, ResourceKind.Project, closed.ProjectId, GrantLevel.Write),
            new ResourceGrant(role.Id, ResourceKind.Column, world.Base.ColumnDefIds[1], GrantLevel.None, isDeny: true),
            new ResourceGrant(role.Id, ResourceKind.Table, closed.TableDefId, GrantLevel.None, isDeny: true));
        await db.SaveChangesAsync(CancellationToken.None);

        return (actor.Id, subject.Id);
    }

    private static void AssertSame(
        string what,
        IReadOnlyDictionary<CellAddress, EditDecision> expected,
        IReadOnlyDictionary<CellAddress, EditDecision> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(Key), actual.Keys.OrderBy(Key));

        foreach (var (address, e) in expected)
        {
            var a = actual[address];
            var where = $"{what}, {address}";

            Assert.True(e.IsAllowed == a.IsAllowed, $"{where}: IsAllowed {e.IsAllowed} ≠ {a.IsAllowed}");
            Assert.True(e.Reason == a.Reason, $"{where}: Reason {e.Reason} ≠ {a.Reason}");
            Assert.True(e.RequiresConfirmation == a.RequiresConfirmation, $"{where}: RequiresConfirmation");
            Assert.True(string.Equals(e.Detail, a.Detail, StringComparison.Ordinal), $"{where}: Detail");
        }
    }

    /// <summary>Двійник поточного користувача з сеансом симуляції.</summary>
    /// <remarks>
    /// ⚠ Клас, а не NSubstitute: <c>SimulationSessionId</c> — член інтерфейсу
    /// з тілом за замовчуванням, і на його заміну в сабі тут ніхто не спирався.
    /// </remarks>
    private sealed class FakeCurrentUser(int userId) : ICurrentUser
    {
        public int? UserId => userId;

        public string? UserName => "p8-actor";

        public string CorrelationId => "p8-sim";

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];

        public long? SimulationSessionId { get; set; }
    }

    private static string Key(CellAddress address) => address.ToString();

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Base,
        ExtraTable Submitted,
        ExtraTable Approved,
        ExtraTable Denied,
        long ClosedInstanceId,
        IReadOnlyList<long> InstanceIds,
        IReadOnlyDictionary<string, AccessProfile> Profiles);

    /// <summary>
    /// Документ A (відкритий період): базова таблиця на своєму аркуші, аркуш
    /// SA з двома таблицями (поданий), SB з однією (чернетка, правило
    /// <c>SourceWindow</c>), SC з однією (затверджений). Документ B — інший
    /// проєкт, закритий період, дві таблиці на двох аркушах.
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        var docA = await builder.BuildAsync(PeriodKeyValue, columnCount: 3, rowCount: 3, ct: CancellationToken.None);
        var extraA = await MultiTableDocument.AddTablesAsync(builder, docA, [2, 1, 1], ct: CancellationToken.None);

        var docB = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 2, ct: CancellationToken.None);
        var extraB = await MultiTableDocument.AddTablesAsync(builder, docB, [1], ct: CancellationToken.None);

        var submitted = extraA[0];
        var permitted = extraA[2];
        var approved = extraA[3];

        await using (var db = builder.CreateContext())
        {
            await AdvanceAsync(db, docA.ProjectId, PeriodState.Open);
            await AdvanceAsync(db, docB.ProjectId, PeriodState.Open);
            await AdvanceAsync(db, docB.ProjectId, PeriodState.Closed);

            var submittedState = new ApprovalState(docA.DocumentId, submitted.SheetDefId, PeriodKeyValue);
            submittedState.Submit(userId: 1, Now);

            var approvedState = new ApprovalState(docA.DocumentId, approved.SheetDefId, PeriodKeyValue);
            approvedState.Submit(userId: 1, Now);
            approvedState.Approve(userId: 2, Now);

            db.ApprovalStates.AddRange(submittedState, approvedState);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await ArrangePermitAsync(builder, docA.TemplateVersionId, permitted);

        var projects = new[] { docA.ProjectId, docB.ProjectId };

        AccessBuilder Grant(GrantLevel level)
        {
            var access = new AccessBuilder();
            foreach (var project in projects)
            {
                access.Grant(ResourceKind.Project, project, level);
            }

            return access;
        }

        var profiles = new Dictionary<string, AccessProfile>(StringComparer.Ordinal)
        {
            ["без грантів"] = new AccessBuilder().Build(),
            ["Write на проєкти"] = Grant(GrantLevel.Write).Build(),
            ["Read на проєкти"] = Grant(GrantLevel.Read).Build(),
            ["Write + Deny таблиці E2"] = Grant(GrantLevel.Write).Deny(ResourceKind.Table, permitted.TableDefId).Build(),
            ["Write + Deny колонки базової таблиці"] =
                Grant(GrantLevel.Write).Deny(ResourceKind.Column, docA.ColumnDefIds[1]).Build(),
            ["Write + Deny проєкту A"] = Grant(GrantLevel.Write).Deny(ResourceKind.Project, docA.ProjectId).Build(),
            ["Read на проєкти + Write лише на таблицю E3"] =
                Grant(GrantLevel.Read).Grant(ResourceKind.Table, approved.TableDefId, GrantLevel.Write)
                    .Grant(ResourceKind.Table, docA.TableDefId, GrantLevel.Write).Build(),
            ["Read на проєкти + Write лише на аркуш SB"] =
                Grant(GrantLevel.Read).Grant(ResourceKind.Sheet, permitted.SheetDefId, GrantLevel.Write).Build(),
        };

        // ⚠ Базова таблиця документа A навмисно НЕ перша в списку: порядок
        // запиту не мусить збігатися з порядком «першого аркуша» групи.
        var ids = new List<long>();
        ids.AddRange(extraA.Select(t => t.TableInstanceId));
        ids.Add(docA.TableInstanceId);
        ids.Add(docB.TableInstanceId);
        ids.AddRange(extraB.Select(t => t.TableInstanceId));

        return new World(
            builder, docA, submitted, approved, permitted, docB.TableInstanceId, ids, profiles);
    }

    private static async Task AdvanceAsync(EcrDbContext db, int projectId, PeriodState target)
    {
        var period = await db.Periods.FirstAsync(
            p => p.ProjectId == projectId && p.PeriodKeyValue == PeriodKeyValue, CancellationToken.None);

        period.AdvanceTo(target, Now);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Правило <c>SourceWindow</c> на таблиці <paramref name="table"/>: перша
    /// колонка обирає дозвіл (діє до 31 серпня), друга — жовтнева.
    /// </summary>
    private async Task ArrangePermitAsync(TestDocumentBuilder builder, int templateVersionId, ExtraTable table)
    {
        await ExecuteAsync(
            "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = @month WHERE Id = @id",
            ("@id", table.ColumnDefIds[1]), ("@month", MonthNumber));

        await using var db = builder.CreateContext();

        var registry = new RegistryDef(
            EcrCode.Create($"PERMIT_P8_{table.TableDefId}"), Text("Permits"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync(CancellationToken.None);

        var entry = new RegistryEntry(registry.Id, EcrCode.Create("P1"), Text("Permit 1"));
        entry.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 8, 31));
        db.RegistryEntries.Add(entry);

        db.PeriodAccessRules.Add(PeriodAccessRuleDef
            .ForSourceWindow(templateVersionId, table.ColumnDefIds[0], OutOfWindowBehavior.ReadOnly)
            .ForTable(table.TableDefId));
        await db.SaveChangesAsync(CancellationToken.None);

        // Лише ПЕРШИЙ рядок обрав дозвіл: другий лишається редагованим, і
        // пакет мусить розрізнити їх так само, як поштучний шлях.
        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(PeriodKeyValue), table.RowIds[0], table.ColumnDefIds[0]),
            table.TableDefId,
            new CellValueData { ValueRegistryEntryId = entry.Id }));

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));

    private async Task ExecuteAsync(string sqlText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
