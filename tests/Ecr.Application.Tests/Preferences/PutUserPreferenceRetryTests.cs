// tests/Ecr.Application.Tests/Preferences/PutUserPreferenceRetryTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Preferences;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Preferences;

/// <summary>
/// PA4-S-01: перший <c>PUT /me/preferences/{key}</c> з двох сесій одного користувача. Програшний <c>INSERT</c> приходить
/// з <c>UnitOfWork</c> як <c>ConcurrencyConflictException ECR-CELL-0409</c>; обробник мусить перечитати рядок
/// переможця й оновити його, а не віддати клієнтові помилку.
/// </summary>
/// <remarks>
/// ⛔ Мутація: прибрати <c>catch</c> із <c>PutUserPreferenceHandler</c> — «Програш_першого_запису…» почервоніє винятком;
/// прибрати умову <c>added</c> — почервоніє «Конфлікт_оновлення_наявного…» (зайвий <c>Detach</c> і ковтання чужого 0409).
/// </remarks>
public sealed class PutUserPreferenceRetryTests
{
    private const int UserId = 7;
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Програш_першого_запису_перечитує_переможця_і_оновлює_його()
    {
        var winner = new UserPreference(UserId, "theme", "\"dark\"", Now.AddMinutes(-1));
        var store = new FakeStore { FindResults = { null, winner } };
        var uow = new FakeUow { FailFirstWith = Conflict() };

        var dto = await Handler(store, uow).HandleAsync("theme", "\"light\"", CancellationToken.None);

        Assert.Equal(2, uow.Saves);
        Assert.Single(store.Detached);
        Assert.Equal("\"light\"", winner.ValueJson);
        Assert.Equal("light", dto.Value.GetString());
        Assert.Equal(Now, dto.UpdatedAt);
        Assert.Equal(1, store.Counts); // ліміт при повторі не перевіряється
    }

    [Fact]
    public async Task Конфлікт_оновлення_наявного_рядка_не_ковтається_і_без_Detach()
    {
        var existing = new UserPreference(UserId, "theme", "\"dark\"", Now.AddMinutes(-1));
        var store = new FakeStore { FindResults = { existing } };
        var uow = new FakeUow { FailFirstWith = Conflict() };

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler(store, uow).HandleAsync("theme", "\"light\"", CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal(1, uow.Saves);
        Assert.Empty(store.Detached);
    }

    [Fact]
    public async Task Програш_першого_запису_коли_переможця_вже_прибрали_дає_оригінальний_конфлікт()
    {
        var store = new FakeStore { FindResults = { null, null } };
        var uow = new FakeUow { FailFirstWith = Conflict() };

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler(store, uow).HandleAsync("theme", "\"light\"", CancellationToken.None));

        Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
        Assert.Equal(1, uow.Saves);
    }

    [Fact]
    public async Task Успішне_перше_збереження_не_повторюється()
    {
        var store = new FakeStore { FindResults = { null } };
        var uow = new FakeUow();

        var dto = await Handler(store, uow).HandleAsync("theme", "\"light\"", CancellationToken.None);

        Assert.Equal(1, uow.Saves);
        Assert.Empty(store.Detached);
        Assert.Equal("light", dto.Value.GetString());
    }

    private static ConcurrencyConflictException Conflict()
        => new("ECR-CELL-0409", "конфлікт", new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CELL-0409.concurrentChange" });

    private static PutUserPreferenceHandler Handler(FakeStore store, FakeUow uow)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        return new PutUserPreferenceHandler(store, uow, user, clock, Substitute.For<IUiStringCatalog>());
    }

    private sealed class FakeStore : IUserPreferenceStore
    {
        public FindList FindResults { get; } = new();
        public List<UserPreference> Detached { get; } = [];
        public int Counts { get; private set; }

        public Task<IReadOnlyList<UserPreference>> ListAsync(int userId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<UserPreference>>([]);

        public Task<UserPreference?> FindAsync(int userId, string key, CancellationToken ct)
            => Task.FromResult(FindResults.Next());

        public Task<int> CountAsync(int userId, CancellationToken ct)
        {
            Counts++;
            return Task.FromResult(0);
        }

        public void Add(UserPreference preference)
        {
        }

        public void Remove(UserPreference preference)
        {
        }

        public void Detach(UserPreference preference) => Detached.Add(preference);
    }

    /// <summary>Черга відповідей <c>FindAsync</c>: по одній на виклик; вичерпана - останнє значення.</summary>
    private sealed class FindList : List<UserPreference?>
    {
        private int _next;

        public UserPreference? Next() => this[Math.Min(_next++, Count - 1)];
    }

    private sealed class FakeUow : IUnitOfWork
    {
        public ConcurrencyConflictException? FailFirstWith { get; init; }
        public int Saves { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken ct)
        {
            Saves++;
            if (Saves == 1 && FailFirstWith is not null)
            {
                throw FailFirstWith;
            }

            return Task.FromResult(1);
        }

        public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
