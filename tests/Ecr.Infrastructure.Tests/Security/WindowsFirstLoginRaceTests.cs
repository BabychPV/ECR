// tests/Ecr.Infrastructure.Tests/Security/WindowsFirstLoginRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// B150 (рішення координатора 16.11): два одночасні ПЕРШІ входи Windows того самого доменного користувача — обидва
/// успішні, запис користувача один. Справжні база, <see cref="UserStore"/> і <see cref="UnitOfWork"/>.
/// </summary>
/// <remarks>
/// Детермінований тест відтворює гонитву точно: сховище «програшного» входу перший раз не бачить запису ні за SID,
/// ні за ім'ям (так виглядає читання до коміту переможця), а сам запис переможця вже в базі. Тоді вставка
/// програшного відбивається унікальним індексом, і вхід мусить узяти запис переможця. Мутації: прибрати
/// <c>catch</c> у <c>LoginHandler.HandleWindowsAsync</c> (умова <c>&amp;&amp; race.Details is null</c>) або арм
/// <c>UX_User_Sid</c> у <c>UnitOfWork</c> → червоний. Справжня паралель — тест «два одночасні» (запускати 20 разів).
/// B161: програшний, чиє ім'я зайняв запис з ІНШИМ SID, отримує <c>windowsSidMismatch</c> і спробу <c>SidMismatch</c>
/// у журналі, як послідовний шлях X5-01; мутація — повернути <c>throw;</c> на <c>winner is null</c> → червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class WindowsFirstLoginRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 1, 15, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Програшний_перший_вхід_бере_запис_переможця_і_входить()
    {
        var (sid, name) = Identity();
        try
        {
            int winnerId;
            await using (var winnerDb = Context())
            {
                var winner = await Handler(winnerDb, new UserStore(winnerDb))
                    .HandleWindowsAsync(sid, name, name, [], null, CancellationToken.None);
                winnerId = winner.UserId;
            }

            await using var loserDb = Context();
            var loser = await Handler(loserDb, BlindOnce(new UserStore(loserDb)))
                .HandleWindowsAsync(sid, name, name, [], null, CancellationToken.None);

            Assert.Equal(winnerId, loser.UserId);
            Assert.Equal(1, await CountAsync(sid));
        }
        finally
        {
            await CleanupAsync(sid, name);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Переможець_видний_лише_за_ім_ям_з_тим_самим_SID_це_не_SidMismatch()
    {
        // Друге вікно гонитви (знайдене 20× прогоном): пошук за SID — до коміту переможця, пошук за ім'ям — після.
        var (sid, name) = Identity();
        try
        {
            int winnerId;
            await using (var winnerDb = Context())
            {
                winnerId = (await Handler(winnerDb, new UserStore(winnerDb))
                    .HandleWindowsAsync(sid, name, name, [], null, CancellationToken.None)).UserId;
            }

            await using var loserDb = Context();
            var loser = await Handler(loserDb, BlindOnce(new UserStore(loserDb), blindName: false))
                .HandleWindowsAsync(sid, name, name, [], null, CancellationToken.None);

            Assert.Equal(winnerId, loser.UserId);
            Assert.Equal(1, await CountAsync(sid));
        }
        finally
        {
            await CleanupAsync(sid, name);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B161")]
    public async Task Ім_я_щойно_зайняв_запис_з_іншим_SID_це_SidMismatch_а_не_userNameTaken()
    {
        // Вставку програшного відбиває `UQ_User_Name`, а за його SID переможця немає: ім'я зайняв запис з ІНШИМ SID
        // (комітився між пошуком за ім'ям і вставкою). Відповідь — та сама, що й послідовний шлях X5-01.
        var (sid, name) = Identity();
        var holderSid = sid.Replace("-1001", "-1002", StringComparison.Ordinal);
        try
        {
            int holderId;
            await using (var holderDb = Context())
            {
                var holder = User.CreateDomain(name, name, holderSid, Now);
                holderDb.Users.Add(holder);
                await new UnitOfWork(holderDb).SaveChangesAsync(CancellationToken.None);
                holderId = holder.Id;
            }

            await using var loserDb = Context();
            var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Handler(loserDb, BlindOnce(new UserStore(loserDb)))
                    .HandleWindowsAsync(sid, name, name, [], null, CancellationToken.None));

            Assert.Equal("ECR-USR-0409", thrown.ErrorCode);
            Assert.Equal("err.ECR-USR-0409.windowsSidMismatch", thrown.Details!["messageKey"]);
            Assert.Equal(sid, thrown.Details["sid"]);

            await using var check = Context();
            var attempt = await check.LoginAttempts.SingleAsync(a => a.UserName == name);
            Assert.False(attempt.IsSuccess);
            Assert.Equal("SidMismatch", attempt.FailReason);
            Assert.Equal(AuthProvider.Windows, attempt.Provider);
            Assert.Equal(holderSid, (await check.Users.SingleAsync(u => u.Id == holderId)).WindowsSid);
            Assert.Equal(0, await CountAsync(sid));
        }
        finally
        {
            await CleanupAsync(sid, name);
            await CleanupAsync(holderSid, name);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Два_одночасні_перші_входи_обидва_успішні_і_запис_один()
    {
        var (sid, name) = Identity();
        try
        {
            await using var a = Context();
            await using var b = Context();
            using var gate = new Barrier(2);

            Task<LoginResult> Login(EcrDbContext db) => Task.Run(async () =>
            {
                gate.SignalAndWait();
                return await Handler(db, new UserStore(db)).HandleWindowsAsync(sid, name, name, [], null, CancellationToken.None);
            });

            var results = await Task.WhenAll(Login(a), Login(b));

            Assert.Equal(results[0].UserId, results[1].UserId);
            Assert.Equal(1, await CountAsync(sid));
        }
        finally
        {
            await CleanupAsync(sid, name);
        }
    }

    /// <summary>Сховище, яке ПЕРШИЙ раз не бачить запису ні за SID, ні за ім'ям — читання «до коміту переможця».</summary>
    private static IUserStore BlindOnce(UserStore real, bool blindName = true)
    {
        var store = Substitute.For<IUserStore>();
        var sidCalls = 0;
        var nameCalls = 0;
        store.FindByWindowsSidAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref sidCalls) == 1
                ? Task.FromResult<User?>(null)
                : real.FindByWindowsSidAsync(ci.ArgAt<string>(0), ci.ArgAt<CancellationToken>(1)));
        store.FindByUserNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => blindName && Interlocked.Increment(ref nameCalls) == 1
                ? Task.FromResult<User?>(null)
                : real.FindByUserNameAsync(ci.ArgAt<string>(0), ci.ArgAt<CancellationToken>(1)));
        store.When(s => s.Add(Arg.Any<User>())).Do(ci => real.Add(ci.ArgAt<User>(0)));
        store.When(s => s.ForgetAdded(Arg.Any<User>())).Do(ci => real.ForgetAdded(ci.ArgAt<User>(0)));
        store.When(s => s.RecordAttempt(Arg.Any<LoginAttempt>())).Do(ci => real.RecordAttempt(ci.ArgAt<LoginAttempt>(0)));
        store.ListAssignmentsAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(ci => real.ListAssignmentsAsync(
                ci.ArgAt<int>(0), ci.ArgAt<IReadOnlyList<string>>(1), ci.ArgAt<DateOnly>(2), ci.ArgAt<CancellationToken>(3)));
        return store;
    }

    private static LoginHandler Handler(EcrDbContext db, IUserStore users)
        => new(users, new PasswordHasher(), new UnitOfWork(db), new TestClock(Now), NullLogger<LoginHandler>.Instance);

    private static (string Sid, string Name) Identity()
    {
        var n = Random.Shared.Next(100_000, 999_999);
        return ($"S-1-5-21-2099-150-{n}-1001", $"B150\\jit{n}");
    }

    private async Task<int> CountAsync(string sid)
    {
        await using var db = Context();
        return await db.Users.CountAsync(u => u.WindowsSid == sid);
    }

    private async Task CleanupAsync(string sid, string name)
    {
        await using var db = Context();
        await db.LoginAttempts.Where(a => a.UserName == name).ExecuteDeleteAsync();
        await db.Users.Where(u => u.WindowsSid == sid).ExecuteDeleteAsync();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
