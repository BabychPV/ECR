namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Право, що входить у роль. Зв'язок «багато до багатьох» без сурогатного
/// ключа: пара <c>(RoleId, PermissionCode)</c> і є ідентичністю.
/// </summary>
/// <remarks>
/// ⚠ Посилання на право — за **кодом**, а не за числом. Коди приходять із
/// seed і згадуються в текстах помилок, документації і тестах; числовий id
/// довелося б звіряти щоразу, коли хтось питає, чому доступ закрито.
/// </remarks>
public sealed class RolePermission
{
    private RolePermission() { }

    /// <summary>Створює зв'язок ролі і права.</summary>
    /// <param name="roleId">Роль.</param>
    /// <param name="permissionCode">Код права.</param>
    public RolePermission(int roleId, string permissionCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionCode);

        RoleId = roleId;
        PermissionCode = permissionCode;
    }

    /// <summary>Роль.</summary>
    public int RoleId { get; private set; }

    /// <summary>Код права з <c>sec.Permission</c>.</summary>
    public string PermissionCode { get; private set; } = null!;
}
