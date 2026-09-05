using Ecr.Domain.Entities.Security;

namespace Ecr.Application.Ports;

/// <summary>
/// Доступ до облікових записів для use-cases безпеки.
/// </summary>
/// <remarks>
/// Окремий порт, а не <see cref="IRepository{T, TId}"/>: сценаріям безпеки
/// потрібні не «знайти за Id», а питання — чи є вже адміністратор, чи
/// заблокований запис, чи існує bootstrap. Записані як методи порту, вони
/// перевіряються без бази; записані як <c>IQueryable</c>, вони протекли б у
/// use-case разом із провайдером.
/// </remarks>
public interface IUserStore
{
    /// <summary>Технічний запис первинного налаштування; <c>null</c> — його немає.</summary>
    public Task<User?> FindBootstrapAdminAsync(CancellationToken ct);

    /// <summary>Обліковий запис за іменем входу.</summary>
    public Task<User?> FindByUserNameAsync(string userName, CancellationToken ct);

    /// <summary>Обліковий запис за ідентифікатором.</summary>
    public Task<User?> FindByIdAsync(int userId, CancellationToken ct);

    /// <summary>
    /// Чи є **активний доменний** користувач із правом
    /// <paramref name="permissionCode"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме з правом, а не «будь-який доменний». Інакше перший рядовий
    /// співробітник, що увійшов у систему, вимкнув би bootstrap-адміністратора
    /// — і налаштовувати систему стало б нікому (D-97).
    /// </remarks>
    public Task<bool> HasActiveDomainAdminAsync(string permissionCode, CancellationToken ct);

    /// <summary>Обліковий запис за SID каталогу.</summary>
    public Task<User?> FindByWindowsSidAsync(string sid, CancellationToken ct);

    /// <summary>Додає новий обліковий запис.</summary>
    public void Add(User user);

    /// <summary>Фіксує спробу входу — вдалу чи ні.</summary>
    /// <remarks>
    /// Пишеться й для НЕІСНУЮЧОГО імені: інакше підбір імен не лишав би сліду
    /// взагалі, а саме він і є першою фазою атаки.
    /// </remarks>
    public void RecordAttempt(LoginAttempt attempt);

    /// <summary>Призначає роль записові; застосовується разом із транзакцією.</summary>
    public Task GrantRoleAsync(User user, string roleCode, CancellationToken ct);

    /// <summary>Сторінка облікових записів.</summary>
    /// <remarks>⛔ Хеш пароля і <c>SecurityStamp</c> не покидають сховище (ФВ-6.11).</remarks>
    public Task<Common.PagedResult<Security.UserView>> ListAsync(
        Common.CursorRequest page, DateTime utcNow, CancellationToken ct);

    /// <summary>Ролі з їхніми правами.</summary>
    public Task<IReadOnlyList<Security.RoleView>> ListRolesAsync(CancellationToken ct);

    /// <summary>Створює роль із набором прав; повертає її ідентифікатор.</summary>
    public Task<int> AddRoleAsync(Role role, IReadOnlyList<string> permissionCodes, CancellationToken ct);

    /// <summary>Залишає з переліку лише **небезпечні** права (<c>ФВ-6.12</c>).</summary>
    /// <summary>Коди прав із переданих, яких у каталозі НЕМАЄ.</summary>
    /// <param name="permissionCodes">Коди, які просять видати.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Питається саме про НЕВІДОМІ, а не про відомі: відповідь — це вже
    /// готовий перелік для повідомлення користувачеві, і його не треба
    /// вираховувати відніманням на кожному виклику.
    /// </remarks>
    public Task<IReadOnlyList<string>> FilterUnknownAsync(
        IReadOnlyList<string> permissionCodes, CancellationToken ct);

    public Task<IReadOnlyList<string>> FilterDangerousAsync(
        IReadOnlyList<string> permissionCodes, CancellationToken ct);

    /// <summary>Політика паролів запису або типова.</summary>
    /// <remarks>
    /// Повертає завжди щось: відсутня політика не має означати «без обмежень» —
    /// це був би тихий спосіб вимкнути перевірку довжини одним порожнім полем.
    /// </remarks>
    public Task<PasswordPolicy> GetPolicyAsync(User user, CancellationToken ct);
}
