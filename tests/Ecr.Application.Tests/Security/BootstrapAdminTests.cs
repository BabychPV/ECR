// tests/Ecr.Application.Tests/Security/BootstrapAdminTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>Життєвий цикл bootstrap-адміністратора (ФВ-6.18, D-97, D-115).</summary>
public sealed class BootstrapAdminTests
{
    private const string Password = "Tengiz-2026-Bootstrap!";
    private static readonly DateTime Now = new(2026, 3, 1, 6, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IPasswordHasher _hasher = new FakePasswordHasher();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly List<SecurityEventRecord> _events = [];

    public BootstrapAdminTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.CorrelationId.Returns("c1");
        _audit.WriteSecurityEventAsync(Arg.Do<SecurityEventRecord>(_events.Add), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Створюється_лише_якщо_запису_ще_немає()
    {
        await Ensure().HandleAsync(Password, CancellationToken.None);

        var created = Assert.Single(_users.Users);
        Assert.True(created.IsBootstrapAdmin);
        Assert.True(created.MustChangePassword);
        Assert.Equal((BootstrapAdmin.UserName, BootstrapAdmin.RoleCode), Assert.Single(_users.Grants));

        // ⚠ Повторний старт із тією самою змінною не має ні перезаписати
        // пароль, ні «полагодити» запис. Інакше змінна, яку забули прибрати з
        // конфігурації, назавжди лишається чинним входом (D-97).
        var handler = Ensure();
        await handler.HandleAsync("зовсім-інший-пароль", CancellationToken.None);

        Assert.Single(_users.Users);
        Assert.Equal(BootstrapAdmin.Outcome.Skip, handler.LastOutcome);
        Assert.Equal(FakePasswordHasher.Prefix + Password, created.PasswordHash);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Відсутня_змінна_середовища_дає_попередження_а_не_помилку()
    {
        var handler = Ensure();

        await handler.HandleAsync(bootstrapPassword: null, CancellationToken.None);

        // Це нормальний стан контуру, де вхід уже переведено на домен (D-115).
        // Падіння тут зупинило б робочу систему через відсутність запису, який
        // їй не потрібен.
        Assert.Equal(BootstrapAdmin.Outcome.Warn, handler.LastOutcome);
        Assert.Empty(_users.Users);
        Assert.NotEmpty(handler.Warnings);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Доки_MustChangePassword_інші_запити_дають_ECR_PWD_0428()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => PasswordChangeGate.Ensure(mustChangePassword: true, "/api/v1/documents/7"));

        Assert.Equal("ECR-PWD-0428", error.ErrorCode);

        // Дозволені рівно чотири напрямки. Каталог рядків серед них не з
        // поблажливості: без нього екран зміни пароля показав би сирі ключі
        // замість підписів полів.
        //
        // ⛔ Шляхи тут — СПРАВЖНІ маршрути контролерів. До `A7-14` і перелік,
        // і цей тест писали `/api/v1/auth/logout` та `/api/v1/auth/me`, яких
        // не існує: тест підтверджував, що ворота пропускають вигаданий шлях,
        // і мовчав про те, що справжній вони закривають. Що ці шляхи існують,
        // окремо стежить архітектурний сторож.
        PasswordChangeGate.Ensure(true, "/api/v1/auth/change-password");
        PasswordChangeGate.Ensure(true, "/api/v1/logout");
        PasswordChangeGate.Ensure(true, "/api/v1/ui-strings/en?scope=public");

        // ⚠ `/me` — теж дозволений: саме з нього клієнт дізнається, що треба
        // на зміну пароля. Закритий, він робить вхід нескінченним колом.
        PasswordChangeGate.Ensure(true, "/api/v1/me");

        // А ось те, чого немає, ворота НЕ пропускають — інакше помилка в
        // переліку знову лишилася б непоміченою.
        Assert.Throws<BusinessRuleException>(
            () => PasswordChangeGate.Ensure(true, "/api/v1/auth/logout"));

        // Без прапорця не блокується нічого.
        PasswordChangeGate.Ensure(false, "/api/v1/documents/7");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Зміна_пароля_знімає_прапорець_і_крутить_SecurityStamp()
    {
        await Ensure().HandleAsync(Password, CancellationToken.None);
        var created = Assert.Single(_users.Users);
        var stampBefore = created.SecurityStamp;

        _user.UserId.Returns(created.Id);
        await ChangePassword().HandleAsync(Password, "Новий-Пароль-2026", CancellationToken.None);

        Assert.False(created.MustChangePassword);

        // ⚠ Без нового штампа старі сесії лишилися б дійсними після зміни
        // пароля — тиха діра, якої не видно ні в логах, ні в UI (ФВ-6.7).
        Assert.NotEqual(stampBefore, created.SecurityStamp);
        Assert.Equal(FakePasswordHasher.Prefix + "Новий-Пароль-2026", created.PasswordHash);

        // Тепер закриті напрямки відкриті.
        PasswordChangeGate.Ensure(created.MustChangePassword, "/api/v1/documents/7");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Поява_доменного_адміністратора_вимикає_запис_але_не_видаляє()
    {
        await Ensure().HandleAsync(Password, CancellationToken.None);
        var bootstrap = Assert.Single(_users.Users);

        // Поки доменного адміністратора немає — не вимикається.
        Assert.False(await Disable().HandleAsync(CancellationToken.None));
        Assert.True(bootstrap.IsActive);

        _users.HasDomainAdmin = true;
        Assert.True(await Disable().HandleAsync(CancellationToken.None));

        Assert.False(bootstrap.IsActive);

        // ⚠ Запис лишається в базі і лишається позначеним: за ним видно,
        // звідки взявся перший адміністратор системи. Видалення стерло б цю
        // відповідь назавжди (D-97).
        Assert.Contains(bootstrap, _users.Users);
        Assert.True(bootstrap.IsBootstrapAdmin);

        // Повторний виклик уже нічого не робить — і не пише другої події.
        Assert.False(await Disable().HandleAsync(CancellationToken.None));
        Assert.Single(_events);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Пароль_не_потрапляє_в_лог()
    {
        var handler = Ensure();
        await handler.HandleAsync(Password, CancellationToken.None);
        var created = Assert.Single(_users.Users);

        _user.UserId.Returns(created.Id);
        _users.HasDomainAdmin = true;
        await Disable().HandleAsync(CancellationToken.None);

        // Спроба зі старим паролем після зміни — щоб перевірити ще й текст
        // винятку, а не лише вдалий шлях.
        var wrong = await Assert.ThrowsAsync<AccessDeniedException>(
            () => ChangePassword().HandleAsync("не-той-пароль", Password, CancellationToken.None));

        var surfaces = new List<string> { wrong.ToString() };
        surfaces.AddRange(handler.Warnings);
        surfaces.AddRange(_events.Select(e => JsonSerializer.Serialize(e)));

        // ⛔ Ні пароль, ні його хеш не з'являються в жодній поверхні, що
        // переживає запит: попередження старту, події аудиту і тексти винятків
        // потрапляють у лог, а лог живе довше за секрет (ФВ-6.11).
        foreach (var surface in surfaces)
        {
            Assert.DoesNotContain(Password, surface, StringComparison.Ordinal);
            Assert.DoesNotContain(created.PasswordHash!, surface, StringComparison.Ordinal);
        }
    }

    private EnsureBootstrapAdminHandler Ensure() => new(_users, _hasher, _uow, _clock);

    private DisableBootstrapAdminHandler Disable() => new(_users, _uow, _audit, _user, _clock);

    private ChangePasswordHandler ChangePassword()
        => new(_users, _hasher, _uow, _audit, _user, _clock);

    /// <summary>
    /// Хешер, чий результат видно в тесті.
    /// </summary>
    /// <remarks>
    /// Справжній PBKDF2 тут коштував би ~200 мс на виклик і нічого не додав би:
    /// перевіряється не стійкість хеша, а те, що пароль ніде не спливає.
    /// </remarks>
    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public const string Prefix = "hash:";

        public string Hash(string password) => Prefix + password;

        public bool Verify(string password, string hash)
            => string.Equals(Prefix + password, hash, StringComparison.Ordinal);

        public bool NeedsRehash(string hash) => false;
    }
}
