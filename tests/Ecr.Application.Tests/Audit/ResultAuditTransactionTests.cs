// tests/Ecr.Application.Tests/Audit/ResultAuditTransactionTests.cs
using Ecr.Application.Audit;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Application.Units;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
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
using UomUnit = Ecr.Domain.Entities.Units.Unit;

namespace Ecr.Application.Tests.Audit;

/// <summary>
/// C4 (enterprise-аудит, коректність): РЕЗУЛЬТАТНА подія аудиту комітиться
/// разом з операцією, яку описує; подія-СПРОБА — незалежно від неї.
/// </summary>
/// <remarks>
/// ⛔ <c>AuditWriter</c> пише сирим <c>INSERT</c> по з'єднанню контексту й
/// приєднується до <c>CurrentTransaction</c>, якщо вона є. Шість обробників
/// нижче транзакції не відкривали: аудит автокомітився ДО
/// <c>SaveChangesAsync</c> (журнал мав подію, якої не сталося), а
/// <c>UpdateUnitHandler</c> — навпаки, ПІСЛЯ (зміна без сліду, якщо впав
/// аудит).
///
/// ⚠ Та сама форма доказу, що й <c>ReopenPeriodTransactionTests</c>: справжня
/// СУБД, справжній <c>UnitOfWork</c>, збій вноситься РІВНО між двома записами.
/// Підробка <c>IUnitOfWork</c> перевіряла б підробку.
/// </remarks>
[Collection("SqlServer")]
public sealed class ResultAuditTransactionTests(SqlServerFixture sql)
{
    private const int Actor = 9;
    private static readonly DateTime Now = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    // ── (а) операція падає ПІСЛЯ запису аудиту → в журналі нічого ─────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Збій_збереження_перейменування_ролі_не_лишає_RoleRenamed()
    {
        // ⛔ Сценарій аудитора: два паралельні перейменування в один код —
        // другий `SaveChanges` падає на `UQ_Role`, а `RoleRenamed` уже в журналі.
        var roleId = await ArrangeRoleAsync();

        await using var db = Context();
        var handler = new RenameRoleHandler(
            new UserStore(db), Allow(ListRolesHandler.Permission), new FailingUnitOfWork(new UnitOfWork(db)),
            new AuditWriter(db), _user, new TestClock(Now));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(roleId, $"REN_{_tag}", name: null, CancellationToken.None));

        Assert.Equal(FailingUnitOfWork.Marker, error.Message);
        Assert.Equal(0, await SecurityEventsAsync("RoleRenamed", roleId: roleId));

        await using var check = Context();
        Assert.Equal($"C4_{_tag}", await check.Roles.Where(r => r.Id == roleId).Select(r => r.Code).SingleAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Збій_збереження_видалення_ролі_не_лишає_RoleDeleted()
    {
        var roleId = await ArrangeRoleAsync();

        await using var db = Context();
        var handler = new DeleteRoleHandler(
            new UserStore(db), Allow(ListRolesHandler.Permission), new FailingUnitOfWork(new UnitOfWork(db)),
            new AuditWriter(db), _user, new TestClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(roleId, CancellationToken.None));

        Assert.Equal(0, await SecurityEventsAsync("RoleDeleted", roleId: roleId));

        await using var check = Context();
        Assert.True(await check.Roles.AnyAsync(r => r.Id == roleId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Збій_збереження_грантів_не_лишає_ResourceGrantsReplaced()
    {
        var roleId = await ArrangeRoleAsync();

        await using var db = Context();
        var handler = new ReplaceResourceGrantsHandler(
            new UserStore(db), Allow(ListResourceGrantsHandler.Permission), _user, new AuditWriter(db),
            new FailingUnitOfWork(new UnitOfWork(db)), new TestClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(roleId, [], ResourceGrantsVersion.Of([]), CancellationToken.None));

        Assert.Equal(0, await SecurityEventsAsync("ResourceGrantsReplaced", roleId: roleId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.7")]
    public async Task Збій_збереження_пароля_не_лишає_PasswordChanged()
    {
        var userId = await ArrangeLocalUserAsync();
        _user.UserId.Returns(userId);

        var hasher = Substitute.For<IPasswordHasher>();
        // ⚠ S15: лише чинний пароль збігається з хешем — інакше новий пароль
        // «збігся б із чинним» і відмова настала б до збереження.
        hasher.Verify("old-password", "old-hash").Returns(true);
        hasher.Hash(Arg.Any<string>()).Returns("new-hash");

        await using var db = Context();
        var handler = new ChangePasswordHandler(
            new UserStore(db), hasher, new FailingUnitOfWork(new UnitOfWork(db)),
            new AuditWriter(db), _user, new TestClock(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync("old-password", "a-long-enough-new-password-2026", CancellationToken.None));

        Assert.Equal(0, await SecurityEventsAsync("PasswordChanged", userId: userId));

        await using var check = Context();
        Assert.Equal("old-hash", await check.Users.Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.9")]
    public async Task Збій_збереження_публікації_не_лишає_події_публікації()
    {
        // ⛔ Коментар у `PublishAsync` обіцяв «в одній транзакції», а транзакції
        // не було: подія публікації жила в журналі при версії-чернетці.
        var versionId = await ArrangeVersionAsync(publish: false);

        await using var db = Context();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var handler = Publisher(db, memory, new FailingUnitOfWork(new UnitOfWork(db)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.PublishAsync(versionId, Actor, "C4", CancellationToken.None));

        Assert.Equal(0, await PublicationEventsAsync(versionId));

        await using var check = Context();
        Assert.Equal(
            TemplateVersionStatus.Draft,
            await check.TemplateVersions.Where(v => v.Id == versionId).Select(v => v.Status).SingleAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.8")]
    public async Task Збій_збереження_виведення_з_обігу_не_лишає_події_публікації()
    {
        var versionId = await ArrangeVersionAsync(publish: true);

        await using var db = Context();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var handler = Publisher(db, memory, new FailingUnitOfWork(new UnitOfWork(db)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.DeprecateAsync(versionId, Actor, "C4", CancellationToken.None));

        Assert.Equal(0, await PublicationEventsAsync(versionId));

        await using var check = Context();
        Assert.Equal(
            TemplateVersionStatus.Published,
            await check.TemplateVersions.Where(v => v.Id == versionId).Select(v => v.Status).SingleAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_запису_аудиту_одиниці_відкочує_саму_зміну()
    {
        // ⛔ Зворотний порядок: `UpdateUnitHandler` комітив зміну ДО аудиту. Збій
        // аудиту лишав у довіднику нове позначення без жодного сліду в журналі.
        var unitId = await ArrangeUnitAsync();

        await using var db = Context();
        var unit = await db.Units.AsNoTracking().SingleAsync(u => u.Id == unitId);

        var handler = new UpdateUnitHandler(
            new UnitStore(db), new UnitOfWork(db), Allow(CreateUnitHandler.Permission), _user,
            new SecurityEventFailingAuditWriter(), new TestClock(Now));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(
                unitId, Map($"new{_tag}"), Map("C4 unit"), unit.FactorToBase, unit.OffsetToBase,
                $"\"{UnitVersion.Of(unit)}\"", CancellationToken.None));

        Assert.Equal(SecurityEventFailingAuditWriter.Marker, error.Message);

        await using var check = Context();
        var symbol = (await check.Units.AsNoTracking().SingleAsync(u => u.Id == unitId)).SymbolL10n.Get("en");
        Assert.Equal($"old{_tag}", symbol);
    }

    // ── (б) успішна операція → рівно одна подія ──────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task Успішне_перейменування_ролі_лишає_рівно_одну_подію()
    {
        // ⚠ Контроль до (а): без нього «нуль подій при збої» був би зеленим і на
        // системі, яка не пише аудит узагалі.
        var roleId = await ArrangeRoleAsync();

        await using var db = Context();
        var handler = new RenameRoleHandler(
            new UserStore(db), Allow(ListRolesHandler.Permission), new UnitOfWork(db),
            new AuditWriter(db), _user, new TestClock(Now));

        await handler.HandleAsync(roleId, $"REN_{_tag}", name: null, CancellationToken.None);

        Assert.Equal(1, await SecurityEventsAsync("RoleRenamed", roleId: roleId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.9")]
    public async Task Успішна_публікація_лишає_рівно_одну_подію()
    {
        var versionId = await ArrangeVersionAsync(publish: false);

        await using var db = Context();
        using var memory = new MemoryCache(new MemoryCacheOptions());

        await Publisher(db, memory, new UnitOfWork(db)).PublishAsync(versionId, Actor, "C4", CancellationToken.None);

        Assert.Equal(1, await PublicationEventsAsync(versionId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Успішна_зміна_одиниці_лишає_рівно_одну_подію()
    {
        var unitId = await ArrangeUnitAsync();

        await using var db = Context();
        var unit = await db.Units.AsNoTracking().SingleAsync(u => u.Id == unitId);

        await new UpdateUnitHandler(
                new UnitStore(db), new UnitOfWork(db), Allow(CreateUnitHandler.Permission), _user,
                new AuditWriter(db), new TestClock(Now))
            .HandleAsync(
                unitId, Map($"new{_tag}"), Map("C4 unit"), unit.FactorToBase, unit.OffsetToBase,
                $"\"{UnitVersion.Of(unit)}\"", CancellationToken.None);

        Assert.Equal(1, await SecurityEventsAsync(UpdateUnitHandler.EventType, detailsLike: $"C4U{_tag}"));
    }

    // ── (в) подія-спроба не залежить від долі операції ───────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_експорту_журналу_пишеться_рівно_одна()
    {
        // ⚠ Регресія: поза транзакцією подія-спроба пишеться, як і до C4.
        await using var db = Context();
        await Exporter(db).PrepareAsync(Filter(), maxRows: 10, CancellationToken.None);

        Assert.Equal(1, await SecurityEventsAsync(ExportStructureChangesHandler.ExportedEventType, detailsLike: _tag));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Подія_експорту_журналу_лишається_й_при_відкаті_транзакції_навколо()
    {
        // ⛔ Подія-СПРОБА: «дані пішли» — факт незалежно від того, чим скінчився
        // решта запиту. Якщо хтось колись загорне експорт у транзакцію
        // операції, відкат не має стерти слід доступу до журналу.
        await using var db = Context();
        var exporter = Exporter(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new UnitOfWork(db).ExecuteInTransactionAsync(
                async ct =>
                {
                    await exporter.PrepareAsync(Filter(), maxRows: 10, ct);
                    throw new InvalidOperationException("відкат операції навколо");
                },
                CancellationToken.None));

        Assert.Equal(1, await SecurityEventsAsync(ExportStructureChangesHandler.ExportedEventType, detailsLike: _tag));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Результатна_подія_в_транзакції_відкочується_разом_з_нею()
    {
        // ⚠ Контраст до тесту вище на рівні самого порту: той самий запис
        // `WriteSecurityEventAsync` у транзакції, що відкотилась, зникає.
        await using var db = Context();
        var writer = new AuditWriter(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new UnitOfWork(db).ExecuteInTransactionAsync(
                async ct =>
                {
                    await writer.WriteSecurityEventAsync(Event($"C4R{_tag}"), ct);
                    throw new InvalidOperationException("відкат");
                },
                CancellationToken.None));

        Assert.Equal(0, await SecurityEventsAsync($"C4R{_tag}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Незалежна_подія_в_транзакції_що_відкотилась_лишається()
    {
        await using var db = Context();
        var writer = new AuditWriter(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new UnitOfWork(db).ExecuteInTransactionAsync(
                async ct =>
                {
                    await writer.WriteIndependentSecurityEventAsync(Event($"C4I{_tag}"), ct);
                    throw new InvalidOperationException("відкат");
                },
                CancellationToken.None));

        Assert.Equal(1, await SecurityEventsAsync($"C4I{_tag}"));
    }

    // ── підготовка ───────────────────────────────────────────────────────────

    private static SecurityEventRecord Event(string eventType)
        => new(Now, eventType, TargetUserId: null, TargetRoleId: null, DetailsJson: null, Actor, CorrelationId: null);

    private StructureChangeFilter Filter()
        => new(Now.AddDays(-1), Now, EntityType: $"C4X{_tag}");

    private ExportStructureChangesHandler Exporter(EcrDbContext db)
    {
        var access = Allow(GetCellChangesHandler.Permission);
        var reader = new AuditReader(db);

        return new ExportStructureChangesHandler(
            new GetStructureChangesHandler(reader, access, _user), reader, new AuditWriter(db),
            access, _user, new TestClock(Now), Substitute.For<IUiStringCatalog>());
    }

    private PublishTemplateVersionHandler Publisher(EcrDbContext db, MemoryCache memory, IUnitOfWork uow)
        => new(
            new Repository<TemplateVersion, int>(db),
            new TemplateVersionStore(db),
            new RealFormulaEngine(),
            new CalculationBindingStore(db),
            new MetadataCache(memory, db),
            new UnitCatalog(db),
            Allow(PublishTemplateVersionHandler.Permission),
            _user,
            new AuditWriter(db),
            uow,
            new TestClock(Now),
            new Ecr.Infrastructure.Reporting.ReportViewGenerator(db));

    private IAccessDecisionService Allow(string permission)
    {
        _user.UserId.Returns(Actor);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Permission("Template.View").Build());
        return _access;
    }

    private async Task<int> ArrangeRoleAsync()
    {
        await using var db = Context();
        var role = new Role(EcrCode.Create($"C4_{_tag}"), Text("C4 role"));
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    private async Task<int> ArrangeLocalUserAsync()
    {
        await using var db = Context();
        var user = new User($"c4_{_tag}", "C4 user", AuthProvider.Local);
        user.SetPassword("old-hash");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<int> ArrangeVersionAsync(bool publish)
    {
        await using var db = Context();

        var template = new Template(EcrCode.Create($"C4T{_tag}"), Text("C4 template"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        // Аркуш без таблиць — найменша структура, яку публікація пропускає.
        db.SheetDefs.Add(new SheetDef(version.Id, EcrCode.Create($"S{_tag}"), Text("Sheet"), 1));
        await db.SaveChangesAsync();

        if (publish)
        {
            version.Publish(publishedByUserId: 8, utcNow: Now);
            await db.SaveChangesAsync();
        }

        return version.Id;
    }

    private async Task<int> ArrangeUnitAsync()
    {
        await using var db = Context();
        var dimensionId = await db.Dimensions.Select(d => d.Id).FirstAsync();

        var unit = new UomUnit(
            EcrCode.Create($"C4U{_tag}"), Text($"old{_tag}"), Text("C4 unit"), dimensionId,
            isBase: false, factorToBase: 2m, offsetToBase: 0m);
        db.Units.Add(unit);
        await db.SaveChangesAsync();
        return unit.Id;
    }

    private async Task<int> SecurityEventsAsync(
        string eventType, int? roleId = null, int? userId = null, string? detailsLike = null)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM aud.SecurityEvent
            WHERE EventType = @e
              AND (@r IS NULL OR TargetRoleId = @r)
              AND (@u IS NULL OR TargetUserId = @u)
              AND (@d IS NULL OR DetailsJson LIKE N'%' + @d + N'%');
            """;
        command.Parameters.AddWithValue("@e", eventType);
        command.Parameters.Add("@r", System.Data.SqlDbType.Int).Value = (object?)roleId ?? DBNull.Value;
        command.Parameters.Add("@u", System.Data.SqlDbType.Int).Value = (object?)userId ?? DBNull.Value;
        command.Parameters.Add("@d", System.Data.SqlDbType.NVarChar, 100).Value = (object?)detailsLike ?? DBNull.Value;

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> PublicationEventsAsync(int versionId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.PublicationEvent WHERE EntityType = N'TemplateVersion' AND EntityId = @id;";
        command.Parameters.AddWithValue("@id", versionId);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static LocalizedText Text(string value) => new(Map(value));

    private static Dictionary<string, string> Map(string value) => new() { ["en"] = value };

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.CommandTimeout(30))
            .Options);

    /// <summary>Справжня транзакція, але <c>SaveChangesAsync</c> завжди падає.</summary>
    private sealed class FailingUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public const string Marker = "C4: збій збереження після запису в аудит";

        public Task<int> SaveChangesAsync(CancellationToken ct) => throw new InvalidOperationException(Marker);

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct) => inner.BeginTransactionAsync(ct);

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
            => inner.ExecuteInTransactionAsync(operation, ct);
    }

    /// <summary>Журнал, що падає на події безпеки, — збій ПІСЛЯ збереження зміни.</summary>
    private sealed class SecurityEventFailingAuditWriter : IAuditWriter
    {
        public const string Marker = "C4: збій запису аудиту";

        public Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct)
            => throw new InvalidOperationException(Marker);

        public Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct)
            => throw new InvalidOperationException(Marker);

        public Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
            => throw new InvalidOperationException(Marker);

        public Task WriteSecurityEventsAsync(IReadOnlyList<SecurityEventRecord> events, CancellationToken ct)
            => throw new InvalidOperationException(Marker);

        public Task WriteIndependentSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct)
            => throw new InvalidOperationException(Marker);

        public Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct)
            => throw new InvalidOperationException(Marker);
    }
}
