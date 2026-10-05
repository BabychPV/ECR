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

    /// <summary>
    /// L1-03: правильний пароль, що перевіряється ПОКИ паралельні хибні спроби ставлять блокування, не дає входу.
    /// </summary>
    /// <remarks>
    /// Сценарій: обробник прочитав незаблокований запис і стоїть у Verify (хешер тримає виклик); у цей час поріг
    /// наставав (хибні спроби). Раніше успіх записував сутність через EF і знімав блокування — вхід проходив
    /// (cookie після блокування). Тепер — один UPDATE з умовою, 0 рядків → 423 <c>ECR-AUTH-0423</c>, блокування
    /// лишається, успішної спроби в журналі немає.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.4a")]
    public async Task L1_03_правильний_пароль_під_час_блокування_паралельними_спробами_не_дає_входу()
    {
        const int Max = 3;
        var name = await ArrangeUserAsync(maxFailedAttempts: Max).ConfigureAwait(true);
        var userId = await UserIdAsync(name).ConfigureAwait(true);

        using var inVerify = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var correct = Task.Run(async () =>
        {
            await using var db = Context();
            var handler = new LoginHandler(
                new UserStore(db), new GatedAcceptingHasher(inVerify, release), new UnitOfWork(db), new FixedClock(),
                NullLogger<LoginHandler>.Instance);

            return await Assert.ThrowsAsync<BusinessRuleException>(
                () => handler.HandleAsync(name, "right-password", "10.0.0.2", CancellationToken.None));
        });

        inVerify.Wait(TimeSpan.FromSeconds(30));

        // Пачка підбору ставить блокування, поки правильний пароль «перевіряється».
        for (var i = 0; i < Max; i++)
        {
            await using var db = Context();
            await new UserStore(db).RegisterFailedAttemptAsync(userId, Max, 15, Now, CancellationToken.None).ConfigureAwait(true);
        }

        release.Set();
        var refusal = await correct.ConfigureAwait(true);
        Assert.Equal("ECR-AUTH-0423", refusal.ErrorCode);

        await using var check = Context();
        var row = await check.Users.AsNoTracking().SingleAsync(u => u.Id == userId).ConfigureAwait(true);
        Assert.True(row.IsLockedOut(Now), "блокування знято успішним входом");
        Assert.Equal(Max, row.FailedAttempts);
        Assert.False(await check.LoginAttempts.AnyAsync(a => a.UserName == name && a.IsSuccess).ConfigureAwait(true));
    }

    /// <summary>
    /// L1-03 (рев'ю): атомарний успіх - без блокування скидає лічильник; після спливу <c>LockedUntil</c> теж
    /// проходить; поки блокування діє (навіть рівно на межі +1 с) - <c>false</c> і рядок не змінюється.
    /// Мутація: прибрати умову LockedUntil у UPDATE - третій випадок червоніє.
    /// </summary>
    [Theory]
    [InlineData(3, -1, true)]
    [InlineData(2, 0, true)]
    [InlineData(2, 60, true)]
    [InlineData(5, 1, false)]
    [InlineData(5, 900, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.4a")]
    public async Task Атомарний_успішний_вхід_поважає_блокування_і_скидає_лічильник(int failed, int lockSeconds, bool expected)
    {
        var name = await ArrangeUserAsync(maxFailedAttempts: 5).ConfigureAwait(true);
        var userId = await UserIdAsync(name).ConfigureAwait(true);
        DateTime? lockedUntil = lockSeconds < 0 ? null : lockSeconds == 60 ? Now.AddSeconds(-60) : Now.AddSeconds(lockSeconds);
        await SetCounterAsync(userId, failed, lockedUntil).ConfigureAwait(true);

        await using (var db = Context())
        {
            Assert.Equal(expected, await new UserStore(db).TryRegisterSuccessfulLoginAsync(userId, Now, CancellationToken.None).ConfigureAwait(true));
        }

        await using var check = Context();
        var row = await check.Users.AsNoTracking().SingleAsync(u => u.Id == userId).ConfigureAwait(true);
        if (expected)
        {
            Assert.Equal(0, row.FailedAttempts);
            Assert.Null(row.LockedUntil);
        }
        else
        {
            Assert.Equal(failed, row.FailedAttempts);
            Assert.Equal(lockedUntil, row.LockedUntil);
        }
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

    /// <summary>Хешер, що приймає будь-який пароль, але спершу сигналить і чекає дозволу (тримає Verify).</summary>
    private sealed class GatedAcceptingHasher(ManualResetEventSlim entered, ManualResetEventSlim release) : IPasswordHasher
    {
        public string Hash(string password) => "decoy";

        public bool Verify(string password, string hash)
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return true;
        }

        public bool NeedsRehash(string hash) => false;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }
}
