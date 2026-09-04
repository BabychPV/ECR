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

    /// <summary>Додає новий обліковий запис.</summary>
    public void Add(User user);

    /// <summary>Призначає роль записові; застосовується разом із транзакцією.</summary>
    public Task GrantRoleAsync(User user, string roleCode, CancellationToken ct);

    /// <summary>Політика паролів запису або типова.</summary>
    /// <remarks>
    /// Повертає завжди щось: відсутня політика не має означати «без обмежень» —
    /// це був би тихий спосіб вимкнути перевірку довжини одним порожнім полем.
    /// </remarks>
    public Task<PasswordPolicy> GetPolicyAsync(User user, CancellationToken ct);
}
