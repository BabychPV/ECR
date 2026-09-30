// tests/Ecr.Infrastructure.Tests/Jobs/YearGraceWiringTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ФВ-1.8: річне вікно <c>31.12 + YearGraceOffsetDays</c> проведене в УСІ
/// місця, що рахують стан періоду: задачу станів (<c>PeriodStateJob</c>),
/// рішення про запис (<c>AccessDecisionService</c>), активацію та архівацію
/// проєкту (<c>ProjectQueryHandlers</c>).
/// </summary>
/// <remarks>
/// ⛔ Предмет — саме ПРОВОДКА, а не правило: правило вікна доведене в домені
/// (<c>YearGracePeriodStateTests</c>). Тут перевіряється, що кожен бойовий
/// шлях передає вікно калькулятору; якщо один передає, а інший ні, задача
/// тримає грудень у <c>Grace</c>, а запис відмовляє як у закритому (або
/// навпаки).
/// <para>
/// Політика навмисно коротка (<c>grace 15</c>, <c>hard-close 30</c>): грудень
/// 2026 за власними межами закривається 30.01.2027, тобто ВСЕРЕДИНІ
/// 45-денного вікна (останній день 14.02.2027). Тому 01.02 без вікна —
/// <c>Closed</c>, з вікном — <c>Grace</c>; 15.02 — <c>Closed</c> в обох
/// випадках.
/// </para>
/// <para>
/// Мутаційні докази (2026-09-28, червоний → зелений після повернення):
/// (1) <c>AccessDecisionService</c> без вікна в <c>Effective</c> → червоний
/// <see cref="Запис_у_грудень_01_02_проходить_з_IsLateEdit"/>;
/// (2) <c>PeriodStateJob</c> без вікна в <c>PlanTransitions</c> → червоний
/// <see cref="Задача_станів_у_вікні_лишає_грудень_Grace_після_вікна_Closed"/>;
/// (3) активація без вікна в <c>Plan</c> → червоний
/// <see cref="Активація_у_вікні_переводить_у_Grace_і_грудень_і_листопад"/>;
/// (4) архівація без вікна в <c>Effective</c> → червоний
/// <see cref="Архівація_у_вікні_відмовляє_грудень_ще_не_закритий"/>.
/// </para>
/// <para>
/// ⚠ <c>PeriodStateJob</c> обходить УСІ активні проєкти спільної бази; свої
/// проєкти тест архівує у <c>finally</c>, а рядки журналу обслуговування,
/// написані за час тесту, прибирає.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class YearGraceWiringTests(SqlServerFixture sql) : IDisposable
{
    private const int December = 202612;

    private static readonly TimeZoneInfo Site = SiteTimeZone.Create("Asia/Almaty").ToTimeZoneInfo();

    /// <summary>У вікні: після власного закриття грудня (30.01), до кінця вікна (15.02).</summary>
    private static readonly DateTime InWindow = SiteTime(2027, 2, 1, 10);

    /// <summary>Після вікна: 15.02 — перша доба поза «+45 до 31.12».</summary>
    private static readonly DateTime AfterWindow = SiteTime(2027, 2, 15, 10);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Запис_у_грудень_01_02_проходить_з_IsLateEdit()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(December, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed);

        try
        {
            await ArmDecemberAsync(builder, doc, PeriodState.Grace);
            var userId = await ArrangeWriterAsync(builder, doc.ProjectId);

            await using var db = builder.CreateContext();
            await Handler(db, doc, userId, InWindow).HandleAsync(await RequestAsync(doc, 777m), CancellationToken.None);

            // ⛔ Запис пройшов і позначений ПІЗНІМ — той самий Grace, що й у
            // звичайного пільгового строку (ФВ-1.9), а не другий механізм.
            Assert.Equal(1, await ScalarAsync($"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId}"));
            Assert.Equal(1, await ScalarAsync(
                $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId} AND IsLateEdit = 1"));
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Запис_у_грудень_15_02_після_вікна_відмова_закритий_період()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(December, columnCount: 2, rowCount: 1, rowMode: TableRowMode.Mixed);

        try
        {
            // Збережений стан — ще Grace (задача не встигла): рішення рахує
            // стан на зараз і відмовляє без очікування годинного прогону.
            await ArmDecemberAsync(builder, doc, PeriodState.Grace);
            var userId = await ArrangeWriterAsync(builder, doc.ProjectId);

            await using var db = builder.CreateContext();
            var error = await Assert.ThrowsAsync<AccessDeniedException>(
                async () => await Handler(db, doc, userId, AfterWindow)
                    .HandleAsync(await RequestAsync(doc, 778m), CancellationToken.None));

            Assert.Equal("ECR-ACCS-0403", error.ErrorCode);
            Assert.Contains(nameof(EditDenyReason.PeriodClosed), error.Message, StringComparison.Ordinal);
            Assert.Equal(0, await ScalarAsync($"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId}"));
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Задача_станів_у_вікні_лишає_грудень_Grace_після_вікна_Closed()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(December, rowCount: 1);
        var baseline = await ScalarAsync("SELECT ISNULL(MAX(Id), 0) FROM itg.MaintenanceRun");

        try
        {
            // Збережений стан Open (задача востаннє йшла в грудні): перехід
            // має відбутися, і в яку сторону — вирішує саме вікно.
            await ArmDecemberAsync(builder, doc, PeriodState.Open);

            var inWindowError = await RunStateJobAsync(builder, InWindow);
            Assert.True(
                await StateAsync(builder, doc.ProjectId, December) == PeriodState.Grace,
                $"01.02 у вікні грудень мав лишитися Grace. Збій прогону: {inWindowError}");

            var afterWindowError = await RunStateJobAsync(builder, AfterWindow);
            Assert.True(
                await StateAsync(builder, doc.ProjectId, December) == PeriodState.Closed,
                $"15.02 після вікна грудень мав закритися. Збій прогону: {afterWindowError}");
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
            await ExecuteAsync(
                "DELETE FROM itg.MaintenanceRun WHERE Id > @id AND JobCode = @code",
                ("@id", baseline), ("@code", PeriodStateJob.Code));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Активація_у_вікні_переводить_у_Grace_і_грудень_і_листопад()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(December, rowCount: 1);

        try
        {
            await UseShortPolicyAsync(builder, doc.ProjectId);

            await using (var db = builder.CreateContext())
            {
                await Activation(db, doc.ProjectId, InWindow).HandleAsync(doc.ProjectId, CancellationToken.None);
            }

            Assert.Equal(PeriodState.Grace, await StateAsync(builder, doc.ProjectId, December));

            // ⚠ D-204 (варіант «б»): у вікні року Grace — УСІ періоди року,
            // і листопад, що за власними межами закрився 30.12.
            Assert.Equal(PeriodState.Grace, await StateAsync(builder, doc.ProjectId, 202611));
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Активація_після_вікна_закриває_грудень()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(December, rowCount: 1);

        try
        {
            await UseShortPolicyAsync(builder, doc.ProjectId);

            await using (var db = builder.CreateContext())
            {
                await Activation(db, doc.ProjectId, AfterWindow).HandleAsync(doc.ProjectId, CancellationToken.None);
            }

            Assert.Equal(PeriodState.Closed, await StateAsync(builder, doc.ProjectId, December));
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-1.8")]
    public async Task Архівація_у_вікні_відмовляє_грудень_ще_не_закритий()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(December, rowCount: 1);

        try
        {
            await UseShortPolicyAsync(builder, doc.ProjectId);

            await using (var db = builder.CreateContext())
            {
                await Activation(db, doc.ProjectId, InWindow).HandleAsync(doc.ProjectId, CancellationToken.None);
            }

            // ⛔ Без вікна архівація порахувала б грудень `Closed` і закрила б
            // рік, у якому ще триває дозволене виправлення.
            await using (var db = builder.CreateContext())
            {
                var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                    () => Archive(db, doc.ProjectId, InWindow).HandleAsync(doc.ProjectId, CancellationToken.None));

                Assert.Equal("ECR-PRD-0409", error.ErrorCode);

                // D-204: у вікні незакриті — усі періоди року (активація
                // перевела їх у Grace), серед них грудень і листопад.
                var open = Assert.IsAssignableFrom<IEnumerable<int>>(error.Details!["periodKeys"]).ToList();
                Assert.Contains(December, open);
                Assert.Contains(202611, open);
                Assert.Equal(12, open.Count);
            }
        }
        finally
        {
            await RetireAsync(doc.ProjectId);
        }
    }

    // ── Підготовка ──────────────────────────────────────────────────────

    private static DateTime SiteTime(int year, int month, int day, int hour)
        => TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified), Site);

    /// <summary>Грудень закривається 30.01 — усередині 45-денного вікна.</summary>
    private static PeriodPolicy ShortPolicy(string code)
        => new(EcrCode.Create(code), openOffsetDays: 0, graceOffsetDays: 15,
               hardCloseOffsetDays: 30, yearGraceOffsetDays: 45);

    /// <summary>Активний проєкт, межі грудня за короткою політикою, збережений стан.</summary>
    private static async Task ArmDecemberAsync(TestDocumentBuilder builder, TestDocument doc, PeriodState state)
    {
        await using var db = builder.CreateContext();

        var project = await db.Projects.SingleAsync(p => p.Id == doc.ProjectId);
        project.Activate(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == December);
        period.RecomputeBoundaries(ShortPolicy("YGSHORT"), Site);

        // Контроль передумови: власне закриття — до 01.02, тобто без вікна
        // 01.02 грудень уже Closed.
        Assert.True(period.ComputedCloseAt < InWindow, "Політика не закриває грудень до 01.02 — тест нічого не доводить.");

        period.AdvanceTo(state, period.ComputedGraceAt);
        await db.SaveChangesAsync();
    }

    /// <summary>Політика з коротким закриттям — у базі, бо активація читає її звідти.</summary>
    private async Task UseShortPolicyAsync(TestDocumentBuilder builder, int projectId)
    {
        await using var db = builder.CreateContext();

        var policy = ShortPolicy($"YG{Guid.NewGuid():N}"[..14].ToUpperInvariant());
        db.PeriodPolicies.Add(policy);
        await db.SaveChangesAsync();

        await ExecuteAsync(
            "UPDATE doc.Project SET PeriodPolicyId = @policy WHERE Id = @id",
            ("@policy", policy.Id), ("@id", projectId));
    }

    /// <summary>Користувач із грантом Write на проєкт — рішення про запис рахує справжня служба.</summary>
    private static async Task<int> ArrangeWriterAsync(TestDocumentBuilder builder, int projectId)
    {
        await using var db = builder.CreateContext();

        var name = $"yg_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash("Year-Grace-Wiring-2026!"));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"YG_{Guid.NewGuid():N}"[..16].ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Year grace writer" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync();

        return user.Id;
    }

    private async Task<PatchCellsRequest> RequestAsync(TestDocument doc, decimal value)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.RowKey, r.RowVersion, c.Code
              FROM doc.TableRow r, cfg.ColumnDef c
             WHERE r.Id = @row AND c.Id = @column;
            """;
        command.Parameters.AddWithValue("@row", doc.RowIds[0]);
        command.Parameters.AddWithValue("@column", doc.ColumnDefIds[1]);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return new PatchCellsRequest(
            doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(reader.GetString(0), Convert.ToBase64String((byte[])reader[1]), [new PatchCell(reader.GetString(2), value)])]);
    }

    // ── Обробники зі справжніми сховищами і службою доступу ────────────

    /// <summary>
    /// <c>PatchCellsHandler</c> зі СПРАВЖНІМИ рішенням про доступ, метаданими,
    /// сховищем періодів (звідти <c>IsLateEdit</c>) і записом; підмінено лише
    /// те, що до предмета не належить (методології, довідники, черга).
    /// </summary>
    private PatchCellsHandler Handler(EcrDbContext db, TestDocument doc, int userId, DateTime at)
    {
        var clock = new TestClock(at);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);

        var metadata = new MetadataCache(_memory, db);
        var access = new AccessDecisionService(
            db, metadata, new AccessProfileCache(_memory), clock, user, new WorkflowStore(db));

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(doc.TableDefId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        var registries = Substitute.For<IRegistryStore>();
        registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                  .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        return new PatchCellsHandler(
            new NormalizedCellStore(db), new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock),
            new DocumentStore(db), new PeriodStore(db), metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, headers, new AuditWriter(db), Substitute.For<IAuditReader>(),
            Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db), user, clock,
            Substitute.For<ISheetEditGate>(), Substitute.For<IUnitCatalog>());
    }

    private static ActivateProjectHandler Activation(EcrDbContext db, int projectId, DateTime at)
    {
        var (access, user) = Manager(projectId);
        var periods = new PeriodStore(db);

        return new ActivateProjectHandler(
            periods, access, user, new UnitOfWork(db), new PeriodStateCalculator(), new TestClock(at),
            new Ecr.Application.Periods.PeriodCalendarMaterializer(periods),
            Substitute.For<IMaterializationScheduler>());
    }

    private static ArchiveProjectHandler Archive(EcrDbContext db, int projectId, DateTime at)
    {
        var (access, user) = Manager(projectId);
        return new ArchiveProjectHandler(new PeriodStore(db), access, user, new UnitOfWork(db), new TestClock(at));
    }

    /// <summary>Керівник проєкту: предмет — стани періодів, не гранти.</summary>
    private static (IAccessDecisionService Access, ICurrentUser User) Manager(int projectId)
    {
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(ActivateProjectHandler.Permission)
                .Grant(ResourceKind.Project, projectId, GrantLevel.Manage)
                .Build());

        return (access, user);
    }

    /// <summary>Справжній прогін задачі станів; збій чужого проєкту повертається текстом, а не валить тест.</summary>
    private static async Task<string?> RunStateJobAsync(TestDocumentBuilder builder, DateTime at)
    {
        await using var db = builder.CreateContext();

        try
        {
            await new PeriodStateJob(
                    db, new PeriodStateCalculator(), new UnitOfWork(db), new TestClock(at),
                    Substitute.For<IMaterializationScheduler>())
                .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.ToString();
        }
    }

    // ── Прибирання й читання ────────────────────────────────────────────

    private static async Task<PeriodState> StateAsync(TestDocumentBuilder builder, int projectId, int periodKey)
    {
        await using var db = builder.CreateContext();
        return await db.Periods
            .Where(p => p.ProjectId == projectId && p.PeriodKeyValue == periodKey)
            .Select(p => p.State)
            .SingleAsync();
    }

    /// <summary>Архівує проєкт тесту: задачі станів інших тестів обходять лише активні.</summary>
    private Task RetireAsync(int projectId)
        => ExecuteAsync(
            "UPDATE doc.Project SET Status = @status WHERE Id = @id",
            ("@status", (byte)ProjectStatus.Archived), ("@id", projectId));

    private async Task<long> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task ExecuteAsync(string sqlText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
