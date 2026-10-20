// tests/Ecr.Infrastructure.Tests/Persistence/UserPreferenceFirstPutRaceTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Preferences;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// PA4-S-01: перший <c>PUT /me/preferences/{key}</c> з двох сесій одного користувача. Обидві читають «налаштування немає»
/// й додають його; другу відбивав лише <c>PK_UserPreference</c> - голий <c>500 ECR-SYS-0500</c>. Тепер <c>UnitOfWork</c>
/// мапить його в <c>ConcurrencyConflictException</c>, а обробник перечитує рядок переможця й оновлює його - обидва <c>PUT</c>
/// завершуються, у базі один рядок.
/// </summary>
/// <remarks>
/// ⛔ Мутації: прибрати арм <c>PK_UserPreference</c> з <c>UnitOfWork.TryMapDuplicateKey</c> - обидва тести червоніють сирим
/// <c>DbUpdateException</c>; прибрати <c>catch</c> повтору з <c>PutUserPreferenceHandler</c> - «Паралельний_перший_запис…»
/// червоніє <c>ConcurrencyConflictException</c>.
/// ⚠ Гонка детермінована, без пауз: декоратор сховища тримає ПЕРШЕ читання кожного обробника, доки обидва не прочитали
/// «немає», і лише тоді відпускає обох на <c>SaveChanges</c> - тож один з двох ГАРАНТОВАНО програє на ключі.
/// </remarks>
[Collection("SqlServer")]
public sealed class UserPreferenceFirstPutRaceTests(SqlServerFixture sql)
{
    private const int Rounds = 20;
    private const string Key = "theme";
    private static readonly string[] Expected = ["\"dark\"", "\"light\""];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Паралельний_перший_запис_ключа_з_двох_сесій_завершується_обома_і_лишає_один_рядок()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var userId = await ArrangeUserAsync();
            var gate = new Gate(2);

            var first = PutAsync(userId, "\"dark\"", gate);
            var second = PutAsync(userId, "\"light\"", gate);
            var results = await Task.WhenAll(first, second); // жодного винятку: ні 500, ні 409

            Assert.All(results, dto => Assert.Equal(Key, dto.Key));

            await using var check = Context();
            var rows = await check.UserPreferences.AsNoTracking()
                .Where(p => p.UserId == userId && p.Key == Key)
                .ToListAsync();

            var row = Assert.Single(rows);
            Assert.Contains(row.ValueJson, Expected);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дублікат_ключа_налаштування_дає_ECR_CELL_0409_а_не_сирий_виняток_бази()
    {
        var userId = await ArrangeUserAsync();

        await using var db = Context();
        db.UserPreferences.Add(new UserPreference(userId, Key, "\"dark\"", DateTime.UtcNow));
        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);

        await using var db2 = Context();
        db2.UserPreferences.Add(new UserPreference(userId, Key, "\"light\"", DateTime.UtcNow));

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new UnitOfWork(db2).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
    }

    /// <summary>Один «запит»: власні DbContext, сховище й UnitOfWork, як у DI-скоупі.</summary>
    private async Task<UserPreferenceDto> PutAsync(int userId, string valueJson, Gate gate)
    {
        await Task.Yield();
        await using var db = Context();

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);

        var handler = new PutUserPreferenceHandler(
            new GatedStore(new UserPreferenceStore(db), gate),
            new UnitOfWork(db),
            user,
            clock,
            Substitute.For<IUiStringCatalog>());

        return await handler.HandleAsync(Key, valueJson, CancellationToken.None);
    }

    private async Task<int> ArrangeUserAsync()
    {
        await using var db = Context();
        var user = new User($"pref_{Guid.NewGuid():N}"[..20], "Test user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash"); // CK_User_Provider: локальному - хеш.
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Бар'єр на <paramref name="parties"/> учасників; обмежений в часі, щоб збій не вішав набір.</summary>
    private sealed class Gate(int parties)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) == parties)
            {
                _release.TrySetResult();
            }

            return _release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Першу відповідь «немає» на <c>FindAsync</c> тримає на бар'єрі; решта викликів - наскрізні.</summary>
    private sealed class GatedStore(IUserPreferenceStore inner, Gate gate) : IUserPreferenceStore
    {
        private bool _gated;

        public async Task<UserPreference?> FindAsync(int userId, string key, CancellationToken ct)
        {
            var found = await inner.FindAsync(userId, key, ct);
            if (!_gated)
            {
                _gated = true;
                if (found is null)
                {
                    await gate.ArriveAsync();
                }
            }

            return found;
        }

        public Task<IReadOnlyList<UserPreference>> ListAsync(int userId, CancellationToken ct) => inner.ListAsync(userId, ct);

        public Task<int> CountAsync(int userId, CancellationToken ct) => inner.CountAsync(userId, ct);

        public void Add(UserPreference preference) => inner.Add(preference);

        public void Remove(UserPreference preference) => inner.Remove(preference);

        public void Detach(UserPreference preference) => inner.Detach(preference);
    }
}
