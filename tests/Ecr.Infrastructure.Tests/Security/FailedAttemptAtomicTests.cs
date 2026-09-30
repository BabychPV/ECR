// tests/Ecr.Infrastructure.Tests/Security/FailedAttemptAtomicTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// S8(в): лічильник невдалих спроб під ПАРАЛЕЛЬНИМИ спробами — на справжній базі.
/// </summary>
/// <remarks>
/// ⛔ <c>sec.User</c> не має маркера паралельності, і обробник входу робив
/// «прочитати → +1 у пам'яті → зберегти»: дві одночасні хибні спроби читали
/// той самий лічильник і обидві писали N+1. Пачка паралельних спроб рахувалась
/// як одна-дві, тобто поріг блокування (ФВ-6.4a) під підбором у кілька потоків
/// не наставав ніколи.
/// </remarks>
[Collection("SqlServer")]
public sealed class FailedAttemptAtomicTests(SqlServerFixture sql)
{
    private const int Parallel = 16;
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.4a")]
    public async Task Паралельні_хибні_входи_рахуються_всі_без_втрачених_оновлень()
    {
        // Поріг вище за кількість спроб: кожна має дійти до лічильника, а не
        // зупинитися на «заблоковано» — інакше рахувалося б не те.
        // ⚠ Не 0: у колонки є DEFAULT, і EF не пише значення, рівне CLR-типовому,
        // — база підставила б свій поріг.
        var name = await ArrangeUserAsync(maxFailedAttempts: 1000).ConfigureAwait(true);

        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, Parallel).Select(_ => Task.Run(async () =>
        {
            await using var db = Context();
            var handler = new LoginHandler(
                new UserStore(db), new RejectingHasher(), new UnitOfWork(db), new FixedClock(),
                NullLogger<LoginHandler>.Instance);

            start.Wait();
            await Assert.ThrowsAsync<AccessDeniedException>(
                () => handler.HandleAsync(name, "wrong-password", "10.0.0.1", CancellationToken.None));
        })).ToList();

        start.Set();
        await Task.WhenAll(attempts).ConfigureAwait(true);

        await using var check = Context();
        var failed = await check.Users.Where(u => u.UserName == name)
            .Select(u => u.FailedAttempts).SingleAsync().ConfigureAwait(true);

        // ⛔ Мутаційний доказ: повернути в обробник `user.RegisterFailedAttempt`
        // (читання-зміна-запис через EF) — лічильник помітно менший за Parallel.
        Assert.Equal(Parallel, failed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.4a")]
    public async Task Паралельні_спроби_понад_поріг_блокують_рівно_одна_і_лічильник_повний()
    {
        const int Max = 5;
        var name = await ArrangeUserAsync(maxFailedAttempts: Max).ConfigureAwait(true);
        var userId = await UserIdAsync(name).ConfigureAwait(true);

        using var start = new ManualResetEventSlim(false);
        var calls = Enumerable.Range(0, Parallel).Select(_ => Task.Run(async () =>
        {
            await using var db = Context();
            start.Wait();
            return await new UserStore(db)
                .RegisterFailedAttemptAsync(userId, Max, 15, Now, CancellationToken.None)
                .ConfigureAwait(false);
        })).ToList();

        start.Set();
        var outcomes = await Task.WhenAll(calls).ConfigureAwait(true);

        Assert.Single(outcomes, o => o.LockedNow);
        Assert.Equal(Max, outcomes.Single(o => o.LockedNow).FailedAttempts);
        Assert.Equal(
            Enumerable.Range(1, Parallel),
            outcomes.Select(o => o.FailedAttempts).Order());

        await using var check = Context();
        var row = await check.Users.AsNoTracking().SingleAsync(u => u.Id == userId).ConfigureAwait(true);
        Assert.Equal(Parallel, row.FailedAttempts);
        Assert.True(row.IsLockedOut(Now));
    }

    /// <summary>
    /// SQL сховища і доменний метод дають ОДНАКОВИЙ стан на тих самих входах.
    /// </summary>
    /// <remarks>
    /// ⛔ Правило записане двічі (домен і <c>UPDATE</c>), і друге формулювання
    /// розходиться з першим тихо (`H-23a`). Тут обидва проганяються на
    /// однакових початкових станах і звіряються поле в поле.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.4a")]
    [InlineData(0, null, 5)]            // чистий запис
    [InlineData(4, null, 5)]            // ця спроба — порогова
    [InlineData(5, -1, 5)]              // блокування минуло хвилину тому
    [InlineData(5, 10, 5)]              // блокування ще триває
    [InlineData(2, null, 0)]            // поріг вимкнено
    [InlineData(7, 0, 5)]               // межа блокування рівно «зараз» — вже минула
    public async Task SQL_сховища_збігається_з_доменом(int failedBefore, int? lockedOffsetMinutes, int max)
    {
        DateTime? lockedUntil = lockedOffsetMinutes is { } m ? Now.AddMinutes(m) : null;

        var name = await ArrangeUserAsync(maxFailedAttempts: max).ConfigureAwait(true);
        var userId = await UserIdAsync(name).ConfigureAwait(true);
        await SetCounterAsync(userId, failedBefore, lockedUntil).ConfigureAwait(true);

        await using var db = Context();
        var stored = await new UserStore(db)
            .RegisterFailedAttemptAsync(userId, max, 15, Now, CancellationToken.None).ConfigureAwait(true);

        var domain = new User("mirror", "mirror", AuthProvider.Local);
        typeof(User).GetProperty(nameof(User.FailedAttempts))!.SetValue(domain, failedBefore);
        typeof(User).GetProperty(nameof(User.LockedUntil))!.SetValue(domain, lockedUntil);
        var domainLocked = domain.RegisterFailedAttempt(max, 15, Now);

        Assert.Equal(domain.FailedAttempts, stored.FailedAttempts);
        Assert.Equal(domain.LockedUntil, stored.LockedUntil);
        Assert.Equal(domainLocked, stored.LockedNow);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-12")]
    public async Task Адміністративне_блокування_хибна_спроба_не_скорочує()
    {
        var name = await ArrangeUserAsync(maxFailedAttempts: 1).ConfigureAwait(true);
        var userId = await UserIdAsync(name).ConfigureAwait(true);
        await SetCounterAsync(userId, 0, User.AdministrativeLockUntil).ConfigureAwait(true);

        await using var db = Context();
        var outcome = await new UserStore(db)
            .RegisterFailedAttemptAsync(userId, 1, 15, Now, CancellationToken.None).ConfigureAwait(true);

        Assert.False(outcome.LockedNow);
        Assert.Equal(User.AdministrativeLockUntil, outcome.LockedUntil);
    }

    private async Task<int> UserIdAsync(string name)
    {
        await using var db = Context();
        return await db.Users.Where(u => u.UserName == name).Select(u => u.Id).SingleAsync().ConfigureAwait(false);
    }

    private async Task SetCounterAsync(int userId, int failed, DateTime? lockedUntil)
    {
        await using var db = Context();
        var user = await db.Users.SingleAsync(u => u.Id == userId).ConfigureAwait(false);
        db.Entry(user).Property(u => u.FailedAttempts).CurrentValue = failed;
        db.Entry(user).Property(u => u.LockedUntil).CurrentValue = lockedUntil;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<string> ArrangeUserAsync(int maxFailedAttempts)
    {
        await using var db = Context();

        var policy = new PasswordPolicy($"FA_{_tag}", minLength: 12, maxFailedAttempts: maxFailedAttempts);
        db.PasswordPolicies.Add(policy);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var name = $"fa_{_tag}";
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword("hash-that-never-matches");
        db.Users.Add(user);
        db.Entry(user).Property(u => u.PasswordPolicyId).CurrentValue = policy.Id;
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Хешер, для якого жоден пароль не підходить — і миттєво.</summary>
    private sealed class RejectingHasher : IPasswordHasher
    {
        public string Hash(string password) => "decoy";

        public bool Verify(string password, string hash) => false;

        public bool NeedsRehash(string hash) => false;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }
}
