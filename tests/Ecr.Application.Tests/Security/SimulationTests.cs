// tests/Ecr.Application.Tests/Security/SimulationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Симуляція «очима користувача» — **лише читання** (ФВ-6.16a, D-96).
/// </summary>
/// <remarks>
/// Два тести тут захищають від різних видів провалу: перший — від того, що
/// симуляція стане способом щось зробити за іншого; третій — від того, що
/// профіль суб'єкта витече справжньому користувачеві через кеш.
/// </remarks>
public sealed class SimulationTests
{
    private const int Actor = 7;
    private const int Subject = 42;
    private static readonly DateTime Now = new(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeSimulationService _simulation = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public SimulationTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Actor);

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
               .Returns(new AccessBuilder { UserId = Actor }
                   .Permission(StartSimulationHandler.Permission)
                   .Build());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.16a")]
    public async Task Запис_під_симуляцією_відхиляється_навіть_із_Manage()
    {
        await Start().HandleAsync(Subject, "перевірка скарги", CancellationToken.None);
        var profile = Start().Profile ?? _simulation.LastProfile!;

        Assert.Equal(GrantLevel.Manage, profile.LevelFor(ResourceKind.Project, AccessBuilder.ProjectId));

        var decision = EditRules.CanEdit(profile, AccessBuilder.Cell());

        // ⚠ Симуляція відхиляє запис ПЕРШОЮ і незалежно від прав того, кого
        // симулюють. Інакше «подивитися очима» стало б способом зробити зміну
        // від чужого імені — з правами суб'єкта і без його відома.
        Assert.False(decision.IsAllowed);
        Assert.Equal(EditDenyReason.SimulationReadOnly, decision.Reason);

        // Подання і затвердження — теж запис, і теж відхиляються.
        Assert.False(EditRules.CanSubmit(profile, AccessBuilder.Cell(), hasBlockingErrors: false).IsAllowed);
        Assert.False(EditRules.CanApprove(
            profile, AccessBuilder.Cell(sheet: DocumentStatus.Submitted)).IsAllowed);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-6.16")]
    public async Task Сеанс_потрапляє_в_аудит_до_видачі_профілю()
    {
        await Start().HandleAsync(Subject, "перевірка скарги", CancellationToken.None);

        // ⚠ Порядок тут і є вимогою: збій між видачею профілю і записом лишив
        // би сеанс перегляду чужих даних без сліду — а слід і є суттю ФВ-6.16a.
        Assert.Equal(["start", "profile"], _simulation.Calls);
        Assert.Equal((Actor, Subject, "перевірка скарги"), _simulation.Started);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Профіль_симуляції_не_кешується_і_не_витікає_справжньому_користувачу()
    {
        var handler = Start();
        await handler.HandleAsync(Subject, "перевірка скарги", CancellationToken.None);

        var profile = handler.Profile!;

        // ⛔ Профіль суб'єкта не будується через кешований шлях: під ключем
        // суб'єкта він дістався б справжньому користувачеві разом із чужими
        // правами (ФВ-6.16a п. 4).
        await _access.DidNotReceive().BuildProfileAsync(Subject, Arg.Any<CancellationToken>());

        // Ключ навмисно інший, ніж у профілю суб'єкта, і прапорець стоїть —
        // тобто цей профіль ні з чим не сплутати.
        Assert.True(profile.IsSimulation);
        Assert.StartsWith("sim:", profile.CacheKey, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Автором_дій_лишається_той_хто_симулює()
    {
        var handler = Start();
        await handler.HandleAsync(Subject, "перевірка скарги", CancellationToken.None);

        var profile = handler.Profile!;

        // Права — суб'єкта, автор — той, хто симулює (D-86). Інакше в аудиті
        // стояла б людина, яка нічого не робила, і питання «хто це зробив»
        // лишилося б без відповіді.
        Assert.Equal(Subject, profile.SimulatedForUserId);
        Assert.Equal(Actor, profile.SimulationActorUserId);
        Assert.Equal(Actor, _simulation.Started.Actor);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Симуляція_самого_себе_дає_ECR_SIM_0422()
    {
        var self = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Start().HandleAsync(Actor, "просто подивитися", CancellationToken.None));
        Assert.Equal("ECR-SIM-0422", self.ErrorCode);

        // Порожня причина — той самий код: журнал без причини не відповідає ні
        // на що, а саме заради відповіді він і ведеться.
        var noReason = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Start().HandleAsync(Subject, "   ", CancellationToken.None));
        Assert.Equal("ECR-SIM-0422", noReason.ErrorCode);

        // Жодна з відмов не лишила сеансу в журналі.
        Assert.Empty(_simulation.Calls);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Завершити_можна_лише_власний_сеанс()
    {
        var sessionId = await Start().HandleAsync(Subject, "перевірка скарги", CancellationToken.None);

        // Інший адміністратор із тим самим правом.
        _user.UserId.Returns(99);
        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => End().HandleAsync(sessionId, CancellationToken.None));

        // ⚠ Обрив чужого сеансу псує чужий аудит: у журналі лишився б сеанс,
        // який закрив не той, хто відкривав.
        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        Assert.Null(_simulation.EndedAt);

        _user.UserId.Returns(Actor);
        await End().HandleAsync(sessionId, CancellationToken.None);

        // Запис не видаляється, лише позначається завершеним (D-25).
        Assert.Equal(Now, _simulation.EndedAt);
        Assert.Null(await _simulation.GetActorAsync(sessionId, CancellationToken.None));
    }

    private StartSimulationHandler Start() => new(_simulation, _access, _user, _clock);

    private EndSimulationHandler End() => new(_simulation, _user, _clock);

    /// <summary>Служба симуляції в пам'яті, що фіксує порядок викликів.</summary>
    private sealed class FakeSimulationService : ISimulationService
    {
        private DateTime? _endedAt;

        public List<string> Calls { get; } = [];

        public (int Actor, int Subject, string Reason) Started { get; private set; }

        public AccessProfile? LastProfile { get; private set; }

        public DateTime? EndedAt => _endedAt;

        public Task<long> StartAsync(int actorUserId, int subjectUserId, string reason, CancellationToken ct)
        {
            Calls.Add("start");
            Started = (actorUserId, subjectUserId, reason);
            return Task.FromResult(1L);
        }

        public Task<int?> GetActorAsync(long sessionId, CancellationToken ct)
            => Task.FromResult(_endedAt is null ? Started.Actor : (int?)null);

        public Task EndAsync(long sessionId, CancellationToken ct)
        {
            _endedAt = Now;
            return Task.CompletedTask;
        }

        public Task<AccessProfile> BuildProfileAsync(long sessionId, CancellationToken ct)
        {
            Calls.Add("profile");

            var subject = new AccessBuilder { UserId = Started.Subject }
                .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Manage)
                .Build();

            LastProfile = new AccessProfile
            {
                CacheKey = $"sim:{sessionId}",
                UserId = Started.Subject,
                SecurityStamp = subject.SecurityStamp,
                Permissions = subject.Permissions,
                Grants = subject.Grants,
                Denies = subject.Denies,
                IsSimulation = true,
                SimulatedForUserId = Started.Subject,
                SimulationActorUserId = Started.Actor,
            };

            return Task.FromResult(LastProfile);
        }
    }
}
