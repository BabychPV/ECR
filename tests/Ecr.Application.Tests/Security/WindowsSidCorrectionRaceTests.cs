// tests/Ecr.Application.Tests/Security/WindowsSidCorrectionRaceTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Z4-01: виправлення SID доменного запису не перезаписує SID, який ВСТИГ підтвердити вхід між читанням і записом.
/// </summary>
/// <remarks>
/// ⛔ «Не входив» перевірялося на сутності, прочитаній до запису, а <c>SaveChanges</c> писав SID безумовно: вхід,
/// що завершився між ними, лишав підтверджений каталогом SID перезаписаним. Справжня СУБД і справжній
/// <c>UnitOfWork</c>; гонку вносить обгортка UoW — перший же запис (транзакція чи <c>SaveChanges</c>) іде після того,
/// як «людина увійшла» іншим з'єднанням. Мутація: повернути безумовний запис SID (без умови
/// <c>LastSignInAt IS NULL</c> в <c>UPDATE</c>) — SID перезаписано, відмови немає.
/// </remarks>
[Collection("SqlServer")]
public sealed class WindowsSidCorrectionRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Z4-01")]
    public async Task Вхід_між_читанням_і_записом_не_дозволяє_перезаписати_підтверджений_SID()
    {
        var oldSid = $"S-1-5-21-{Random.Shared.Next(100000, 999999)}-1";
        var newSid = $"S-1-5-21-{Random.Shared.Next(100000, 999999)}-2";
        var (actorId, targetId) = await ArrangeAsync(oldSid).ConfigureAwait(true);

        await using var db = Context();
        var uow = new LoginBeforeWriteUnitOfWork(new UnitOfWork(db), () => ConfirmByLoginAsync(targetId));
        var handler = Handler(db, actorId, uow);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(targetId, newSid, CancellationToken.None));

        Assert.Equal("ECR-SEC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-SEC-0409.windowsSidConfirmed", error.Details!["messageKey"]);
        Assert.True(uow.LoginInjected);

        await using var check = Context();
        var row = await check.Users.AsNoTracking().SingleAsync(u => u.Id == targetId).ConfigureAwait(true);
        Assert.Equal(oldSid, row.WindowsSid);
        Assert.NotNull(row.LastSignInAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Z4-01")]
    public async Task Без_гонки_непідтверджений_SID_виправляється_а_штамп_крутиться()
    {
        var oldSid = $"S-1-5-21-{Random.Shared.Next(100000, 999999)}-3";
        var newSid = $"S-1-5-21-{Random.Shared.Next(100000, 999999)}-4";
        var (actorId, targetId) = await ArrangeAsync(oldSid).ConfigureAwait(true);

        string stampBefore;
        await using (var read = Context())
        {
            stampBefore = (await read.Users.AsNoTracking().SingleAsync(u => u.Id == targetId).ConfigureAwait(true)).SecurityStamp;
        }

        await using var db = Context();
        await Handler(db, actorId, new UnitOfWork(db)).HandleAsync(targetId, newSid, CancellationToken.None)
            .ConfigureAwait(true);

        await using var check = Context();
        var row = await check.Users.AsNoTracking().SingleAsync(u => u.Id == targetId).ConfigureAwait(true);
        Assert.Equal(newSid, row.WindowsSid);
        Assert.NotEqual(stampBefore, row.SecurityStamp);
    }

    private static CorrectWindowsSidHandler Handler(EcrDbContext db, int actorId, IUnitOfWork uow)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(actorId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = actorId }.Permission(CorrectWindowsSidHandler.Permission).Build());

        var current = Substitute.For<ICurrentUser>();
        current.UserId.Returns(actorId);
        current.CorrelationId.Returns("z4-01");

        return new CorrectWindowsSidHandler(new UserStore(db), access, uow, new AuditWriter(db), current, new FixedClock());
    }

    private async Task ConfirmByLoginAsync(int userId)
    {
        await using var db = Context();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE sec.[User] SET LastSignInAt = {Now} WHERE Id = {userId}").ConfigureAwait(false);
    }

    private async Task<(int ActorId, int TargetId)> ArrangeAsync(string windowsSid)
    {
        await using var db = Context();

        var actor = new User($"z401a_{_tag}", $"Admin {_tag}", AuthProvider.Local);
        actor.SetPassword("hash-that-never-matches");
        var target = User.CreateDomain($"CORP-z401_{_tag}", "Ivan", windowsSid, Now);
        db.Users.AddRange(actor, target);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (actor.Id, target.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>UoW, що перед ПЕРШИМ записом (транзакція чи SaveChanges) «впускає» вхід людини іншим з'єднанням.</summary>
    private sealed class LoginBeforeWriteUnitOfWork(IUnitOfWork inner, Func<Task> login) : IUnitOfWork
    {
        public bool LoginInjected { get; private set; }

        public async Task<int> SaveChangesAsync(CancellationToken ct)
        {
            await InjectAsync().ConfigureAwait(false);
            return await inner.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct) => inner.BeginTransactionAsync(ct);

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
        {
            await InjectAsync().ConfigureAwait(false);
            await inner.ExecuteInTransactionAsync(operation, ct).ConfigureAwait(false);
        }

        private async Task InjectAsync()
        {
            if (!LoginInjected)
            {
                LoginInjected = true;
                await login().ConfigureAwait(false);
            }
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }
}
