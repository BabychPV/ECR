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

    private PermitHarness(SqlServerFixture sql, int userId, string[] permissions)
    {
        Sql = sql;
        UserId = userId;
        Permissions = permissions;
        Db = sql.CreateContext();

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

    /// <summary>Нова «сесія» тієї самої особи: свій контекст, як у наступному запиті.</summary>
    public async Task<PermitHarness> NextRequestAsync() => await CreateAsync(Sql, Permissions, UserId).ConfigureAwait(false);

    /// <summary>Будує обробники для нового користувача з потрібними правами.</summary>
    /// <param name="sql">Фікстура бази.</param>
    /// <param name="permissions">Глобальні права.</param>
    /// <param name="existingUserId">Користувач, якщо вже заведений.</param>
    public static async Task<PermitHarness> CreateAsync(SqlServerFixture sql, string[] permissions, int? existingUserId = null)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var userId = existingUserId ?? await NewUserAsync(sql).ConfigureAwait(false);
        return new PermitHarness(sql, userId, permissions);
    }

    /// <summary>Усі права, потрібні для повного циклу типу.</summary>
    public static string[] Everything =>
        ["Registry.View", "Registry.EditDefinition", "Registry.Publish"];

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
