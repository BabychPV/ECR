// tests/Ecr.Infrastructure.Tests/Persistence/PermitHarness.cs
using Ecr.Application.Common;
using Ecr.Application.Permits;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using NSubstitute;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Обробники типів дозволів над справжніми сховищами й базою (Permits, PM-03): один контекст на «запит», як у контейнері.
/// </summary>
/// <remarks>
/// ⚠ Правила виразів тут не компілюються (<c>RegistryRuleCompiler</c> не підставлено): це перевірка складу типу й
/// транзакційності, а не граматики правил - її тримає <c>RegistryRulesHttpTests</c>.
/// </remarks>
internal sealed class PermitHarness : IAsyncDisposable
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private readonly Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] _interceptors;

    private PermitHarness(SqlServerFixture sql, int userId, string[] permissions, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        Sql = sql;
        UserId = userId;
        Permissions = permissions;
        _interceptors = interceptors;
        Db = sql.CreateContext(interceptors);

        var access = Substitute.For<IAccessDecisionService>();
        var builder = new AccessBuilder { UserId = userId };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        access.BuildProfileAsync(userId, Arg.Any<CancellationToken>()).Returns(builder.Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);
        user.CorrelationId.Returns("test");
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);

        Permits = new PermitTypeStore(Db);
        Registries = new RegistryStore(Db);
        Uow = new UnitOfWork(Db, clock, user);
        var audit = new AuditWriter(Db);
        var keys = new RegistryKeyStore(Db);

        Create = new CreatePermitTypeHandler(Permits, Registries, Uow, audit, access, user, clock);
        Get = new GetPermitTypeHandler(Permits, Registries, access, user);
        List = new ListPermitTypesHandler(Permits, Registries, access, user);
        Replace = new ReplacePermitTypeHandler(Permits, Registries, Uow, access, user, clock);
        OpenVersion = new OpenPermitTypeVersionHandler(Permits, Registries, Uow, access, user, clock);
        Delete = new DeletePermitTypeHandler(Permits, Uow, audit, access, user, clock);

        var save = new SaveRegistryDefinitionHandler(
            Registries, Uow, audit, access, user, clock, new UnitCatalog(Db), keys,
            new Ecr.Application.Registries.Keys.RegistryKeyService(keys, Uow));
        Publish = new PublishPermitTypeHandler(Permits, Registries, keys, save, Uow, audit, access, user, clock);

        // Запис записів реєстрів - тим самим писачем і службою ключів, що в контейнері (PM-04: шапки дозволів, R2).
        KeyService = new Ecr.Application.Registries.Keys.RegistryKeyService(keys, Uow);
        Writer = new RegistryEntryWriter(Registries, Uow, audit, user, clock, KeyService);
        RowsQuery = new RegistryRowsQuery(Db);
        Stats = new PermitStore(Db);
        CreatePermit = new CreatePermitHandler(Permits, Registries, RowsQuery, Stats, Writer, Uow, access, user, clock);
        GetPermit = new GetPermitHandler(Permits, Registries, RowsQuery, Stats, access, user, clock);
        ListPermits = new ListPermitsHandler(Permits, Registries, RowsQuery, Stats, access, user, clock);
        UpdatePermit = new UpdatePermitHandler(Permits, Registries, RowsQuery, Stats, Writer, Uow, access, user, clock);
        DeletePermit = new DeletePermitHandler(Permits, Registries, RowsQuery, Stats, KeyService, Uow, audit, access, user, clock);
        GetRows = new Ecr.Application.Registries.Rows.GetRegistryRowsHandler(
            Registries, RowsQuery, new RegistryResolver(), access, user, Permits);
        ActivatePermit = new ActivatePermitHandler(Permits, Registries, RowsQuery, Stats, Writer, Uow, audit, access, user, clock);
        EndPermit = new EndPermitHandler(Permits, Registries, RowsQuery, Stats, Writer, Uow, audit, access, user, clock);
        RenewPermit = new RenewPermitHandler(Permits, Registries, RowsQuery, Stats, Writer, Uow, audit, access, user, clock);

        Access = access;
        User = user;
        Clock = clock;
        Audit = audit;
        Keys = keys;
    }

    public SqlServerFixture Sql { get; }

    public int UserId { get; }

    public string[] Permissions { get; }

    public Ecr.Infrastructure.Persistence.EcrDbContext Db { get; }

    public PermitTypeStore Permits { get; }

    public RegistryStore Registries { get; }

    public UnitOfWork Uow { get; }

    public IAccessDecisionService Access { get; }

    public ICurrentUser User { get; }

    public IClock Clock { get; }

    public AuditWriter Audit { get; }

    public RegistryKeyStore Keys { get; }

    public CreatePermitTypeHandler Create { get; }

    public GetPermitTypeHandler Get { get; }

    public ListPermitTypesHandler List { get; }

    public ReplacePermitTypeHandler Replace { get; }

    public OpenPermitTypeVersionHandler OpenVersion { get; }

    public DeletePermitTypeHandler Delete { get; }

    public PublishPermitTypeHandler Publish { get; }

    public Ecr.Application.Registries.Keys.RegistryKeyService KeyService { get; }

    public RegistryEntryWriter Writer { get; }

    public RegistryRowsQuery RowsQuery { get; }

    public PermitStore Stats { get; }

    public CreatePermitHandler CreatePermit { get; }

    public GetPermitHandler GetPermit { get; }

    public ListPermitsHandler ListPermits { get; }

    public UpdatePermitHandler UpdatePermit { get; }

    public DeletePermitHandler DeletePermit { get; }

    public Ecr.Application.Registries.Rows.GetRegistryRowsHandler GetRows { get; }

    public ActivatePermitHandler ActivatePermit { get; }

    public EndPermitHandler EndPermit { get; }

    public RenewPermitHandler RenewPermit { get; }

    /// <summary>Права, потрібні для ведення дозволів: читати й писати дані реєстрів.</summary>
    public static string[] Editor => ["Registry.View", "Registry.EditData"];

    /// <summary>Усі права: тип + дозволи.</summary>
    public static string[] EverythingWithData => [.. Everything, "Registry.EditData"];

    /// <summary>Нова «сесія» тієї самої особи: свій контекст, як у наступному запиті.</summary>
    public async Task<PermitHarness> NextRequestAsync() => await CreateAsync(Sql, Permissions, UserId, _interceptors).ConfigureAwait(false);

    /// <summary>Будує обробники для нового користувача з потрібними правами.</summary>
    /// <param name="sql">Фікстура бази.</param>
    /// <param name="permissions">Глобальні права.</param>
    /// <param name="existingUserId">Користувач, якщо вже заведений.</param>
    /// <param name="interceptors">Перехоплювачі команд EF (за потреби тесту: запис тексту SQL).</param>
    public static async Task<PermitHarness> CreateAsync(
        SqlServerFixture sql, string[] permissions, int? existingUserId = null,
        params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var userId = existingUserId ?? await NewUserAsync(sql).ConfigureAwait(false);
        return new PermitHarness(sql, userId, permissions, interceptors);
    }

    /// <summary>Усі права, потрібні для повного циклу типу.</summary>
    public static string[] Everything =>
        ["Registry.View", "Permit.EditType", "Permit.PublishType"];

    public static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    private static async Task<int> NewUserAsync(SqlServerFixture sql)
    {
        await using var db = sql.CreateContext();
        var name = $"prm_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash("Pm03-test-pass-1!"));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return user.Id;
    }
}
