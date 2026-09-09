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

    /// <summary>
    /// Замінює НАБІР ролей користувача цілком.
    /// </summary>
    /// <remarks>
    /// ⛔ Способу призначити роль наявному користувачеві не існувало взагалі:
    /// ролі можна було видати лише при створенні, а форма створення надсилала
    /// порожній перелік. Обліковий запис виходив працездатним на вигляд і
    /// безправним насправді, і виправити це було нічим.
    ///
    /// ⚠ Заміна набором, а не «додати/прибрати»: набір ролей — це і є
    /// повноваження людини, і бачити його треба цілком, а не як історію
    /// правок.
    /// </remarks>
    /// <param name="userId">Користувач.</param>
    /// <param name="roleCodes">Коди ролей; порожньо — прибрати всі.</param>
    /// <param name="validity">
    /// Межі чинності за кодом ролі (<c>ФВ-6.16</c>) — підміна на час
    /// відпустки; <c>null</c> або код без запису тут — роль безстрокова.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки ролей тепер призначено.</returns>
    public Task<int> ReplaceRolesAsync(
        int userId,
        IReadOnlyList<string> roleCodes,
        IReadOnlyDictionary<string, Security.RoleValidityWindow>? validity,
        CancellationToken ct);

    /// <summary>
    /// Коди БЕЗСТРОКОВИХ ролей користувача.
    /// </summary>
    /// <remarks>
    /// ⚠ Строкові призначення (підміна на час відпустки) сюди не входять і
    /// не редагуються цим шляхом: інакше збереження форми перетворювало б
    /// тимчасове на постійне — людина бачить роль у списку і лишає її.
    /// </remarks>
    public Task<IReadOnlyList<string>> ListUserRolesAsync(int userId, CancellationToken ct);

    /// <summary>Сторінка облікових записів.</summary>
    /// <remarks>⛔ Хеш пароля і <c>SecurityStamp</c> не покидають сховище (ФВ-6.11).</remarks>
    public Task<Common.PagedResult<Security.UserView>> ListAsync(
        Common.CursorRequest page, DateTime utcNow, CancellationToken ct);

    /// <summary>Ролі з їхніми правами.</summary>
    public Task<IReadOnlyList<Security.RoleView>> ListRolesAsync(CancellationToken ct);

    /// <summary>Створює роль із набором прав; повертає її ідентифікатор.</summary>
    public Task<int> AddRoleAsync(Role role, IReadOnlyList<string> permissionCodes, CancellationToken ct);

    /// <summary>Залишає з переліку лише **небезпечні** права (<c>ФВ-6.12</c>).</summary>
    /// <summary>Ресурсні гранти ролі.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<Security.ResourceGrantDto>> ListGrantsAsync(int roleId, CancellationToken ct);

    /// <summary>
    /// Замінює набір ресурсних грантів ролі цілком.
    /// </summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="grants">Новий набір; порожній прибирає всі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Саме ЗАМІНА, а не додавання: набір грантів — це відповідь на питання
    /// «що покриває роль», і вона має бути повною. Часткові правки лишають
    /// стан, у якому джерело доступу не відновлюється (ФВ-6.6).
    /// </remarks>
    public Task ReplaceGrantsAsync(
        int roleId, IReadOnlyList<Security.ResourceGrantDto> grants, CancellationToken ct);

    /// <summary>
    /// Прокручує <c>SecurityStamp</c> усім носіям ролі.
    /// </summary>
    /// <param name="roleId">Роль, доступ якої змінився.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки сеансів довелося перевидати.</returns>
    /// <remarks>
    /// ⛔ Без цього зміна доступу не діє (`A7-23`). Профіль доступу кешується
    /// на 30 хвилин під ключем «користувач + штамп», і припущення кешу
    /// записане прямо в ньому: «зміна ролей або пароля змінює штамп». Для
    /// ГРАНТІВ воно не виконувалося, тому виданий доступ не з'являвся, а
    /// знятий — не зникав, і обидва по пів години.
    ///
    /// ⚠ Небезпечний бік саме другий. «Видали, але ще діє» — це доступ, який
    /// адміністратор уже вважає закритим.
    /// </remarks>
    public Task<int> RotateStampsForRoleAsync(int roleId, CancellationToken ct);

    /// <summary>
    /// Призначення ролей, адресовані особі <b>або</b> будь-якому з переданих
    /// SID груп — разом із тим, чи діють вони на дату.
    /// </summary>
    /// <param name="userId">Користувач; його особисті призначення входять завжди.</param>
    /// <param name="groupSids">SID груп із квитка сесії; порожньо — лише особисті.</param>
    /// <param name="asOf">Дата, на яку рахується чинність.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Повертає призначення, а не самі ролі, і <b>разом із нечинними</b>:
    /// це діагностика (`H-21`), і «роль була, але підміна на час відпустки
    /// скінчилася» — відповідь, а «ролей немає» — ні. Відфільтрувати нечинні
    /// в запиті означало б стерти рівно ту різницю, заради якої питали.
    ///
    /// ⚠ Чинність рахує <b>доменний метод</b> (<c>RoleAssignment.IsEffectiveOn</c>),
    /// а не копія його умови в SQL (`H-23a`): друге формулювання того самого
    /// правила розходиться з першим тихо.
    /// </remarks>
    public Task<IReadOnlyList<Security.RoleAssignmentTrace>> ListAssignmentsAsync(
        int userId, IReadOnlyList<string> groupSids, DateOnly asOf, CancellationToken ct);

    /// <summary>
    /// Усі призначення, адресовані ГРУПАМ, незалежно від чийогось членства.
    /// </summary>
    /// <param name="asOf">Дата, на яку рахується чинність.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Єдина відповідь на «у яку групу мене треба додати». Членство
    /// приходить із квитка (`ФВ-6.15a`), а квитка чужої сесії в нас немає
    /// (`P-02`) — тож про чужий запис система не може сказати, у яких він
    /// групах, зате може сказати, які групи взагалі щось дають. З цим
    /// адміністратор іде до відділу AD; без цього — нікуди.
    ///
    /// ⚠ Перелік видно лише носієві <c>Security.ManageUsers</c>: він показує,
    /// яка саме AD-група дає адміністративну роль.
    /// </remarks>
    public Task<IReadOnlyList<Security.RoleAssignmentTrace>> ListGroupAssignmentsAsync(
        DateOnly asOf, CancellationToken ct);

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
