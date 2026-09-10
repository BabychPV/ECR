// src/Ecr.Domain/Entities/Security/RoleAssignment.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Призначення ролі. Основний спосіб для доменних користувачів — **на
/// AD-групу** (ФВ-6.15), для локальних — на користувача.
/// </summary>
/// <remarks>
/// Членство в групі береться з токена входу, а не запитом до каталогу на
/// кожну перевірку (ФВ-6.15a): недоступність каталогу не має розривати сеанс
/// уже автентифікованого користувача.
/// </remarks>
public sealed class RoleAssignment : Entity<int>
{
    private RoleAssignment() { }

    public RoleAssignment(int roleId, int? userId, string? principalSid)
    {
        RoleId = roleId;
        UserId = userId;
        PrincipalSid = principalSid;
    }

    /// <summary>Призначення на ще НЕ збереженого користувача.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="user">Користувач.</param>
    /// <remarks>
    /// ⚠ Потрібно рівно для одного сценарію — створення bootstrap-адміністратора
    /// разом із роллю в ОДНІЙ транзакції. З окремими збереженнями збій між
    /// ними лишив би адміністратора без жодного права — і систему без входу,
    /// бо повторний старт уже не створить запис (D-97).
    /// </remarks>
    public RoleAssignment(int roleId, User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        RoleId = roleId;
        User = user;
    }

    public int RoleId { get; private set; }

    /// <summary>Призначення на особу. Взаємовиключне з <see cref="PrincipalSid"/>.</summary>
    public int? UserId { get; private set; }

    /// <summary>Навігація на особу; потрібна лише для вставки разом із користувачем.</summary>
    public User? User { get; private set; }

    /// <summary>SID AD-групи. Це **не** авторство — воно завжди `UserId` (D-86).</summary>
    public string? PrincipalSid { get; private set; }

    /// <summary>Область дії: проєкт, аркуш, період (ФВ-6.14).</summary>
    public string? ScopeJson { get; private set; }

    /// <summary>Початок дії призначення; <c>null</c> — від завжди.</summary>
    /// <remarks>
    /// Строкове призначення — це підміна на час відпустки: без меж її доводиться
    /// знімати руками, а того, хто мав би зняти, саме й немає на місці.
    /// </remarks>
    public DateOnly? ValidFrom { get; private set; }

    /// <summary>Кінець дії; <c>null</c> — безстроково.</summary>
    public DateOnly? ValidTo { get; private set; }

    /// <summary>Чи діє призначення на вказану дату.</summary>
    /// <param name="on">Дата у поясі майданчика.</param>
    /// <returns><c>true</c> — призначення чинне на цю дату.</returns>
    /// <remarks>
    /// ⛔ Це ЄДИНЕ формулювання правила меж дії (<c>H-23a</c>). Копія цієї
    /// умови жила у запиті <c>AccessDecisionService.LoadAsync</c>, а сам метод
    /// не кликав ніхто: він був покритий тестом і недосяжний. Дослівний збіг
    /// двох формулювань не рятує — він тримається рівно до першої правки
    /// одного з них, і розходження тут не ламає нічого видимого: підміна на
    /// час відпустки просто не закінчується, а адміністратор вважає її
    /// закритою.
    ///
    /// ⚠ Межі ВКЛЮЧНІ з обох боків. Призначення «до 31 травня» діє 31 травня:
    /// напівінтервал тут відібрав би права на день раніше, ніж написано в
    /// наказі.
    ///
    /// ⛔ Це **єдиний виняток** із напівінтервального правила системи, і він
    /// свідомий (крок <c>I.10</c>, <c>D2-123</c>). Перевірено при переході на
    /// <c>[from, to)</c>: <see cref="ValidTo"/> тут не походить із міграції —
    /// його набирає адміністратор із наказу про підміну, у якому написано
    /// «до 31 травня». Трьох «нескінченностей» джерела
    /// (<c>9999-*</c>, <c>23:59:59</c>, <c>12:59:59</c>), заради яких
    /// заведено напівінтервал, у <c>sec.RoleAssignment</c> немає: таблиця
    /// наповнюється тільки нами. Тому тут <c>&gt;=</c> лишається, і саме тому
    /// поруч стоїть це пояснення, а не мовчазна нерівність.
    /// </remarks>
    public bool IsEffectiveOn(DateOnly on)
        => (ValidFrom is null || ValidFrom <= on) && (ValidTo is null || ValidTo >= on);

    /// <summary>
    /// Виставляє межі чинності — підміна ролі на час відпустки (ФВ-6.16,
    /// `#48`).
    /// </summary>
    /// <param name="validFrom">Початок дії; <c>null</c> — від завжди.</param>
    /// <param name="validTo">Кінець дії; <c>null</c> — безстроково.</param>
    /// <remarks>
    /// ⛔ До цього методу задати межі не міг ЖОДЕН прикладний шлях: поля мали
    /// лише приватний сетер, і єдиним місцем, що заповнювало їх узагалі, була
    /// рефлексія у тестовій фікстурі (`FakeUserStore.SetValidity`) — факт про
    /// систему, а не про зручність тесту. <see cref="IsEffectiveOn"/>
    /// перевіряв межі коректно, але жодне збережене призначення їх не мало:
    /// підміна на час відпустки (<c>ФВ-6.16</c>) лишалася полем схеми без
    /// шляху запису.
    ///
    /// ⚠ Порядок меж НЕ перевіряється тут: <c>validFrom &gt; validTo</c> —
    /// помилка ЗАПИТУ (переплутані дати в наказі про підміну), а не
    /// доменного інваріанта, і ловить її прикладний шар до виклику
    /// (<c>ReplaceUserRolesHandler</c>, <c>ECR-REQ-0422</c>) — так відповідь
    /// називає поле форми, а не абстрактний «внутрішній стан недійсний».
    /// </remarks>
    public void SetValidity(DateOnly? validFrom, DateOnly? validTo)
    {
        ValidFrom = validFrom;
        ValidTo = validTo;
    }
}
