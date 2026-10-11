using Ecr.Application.Common;
using Ecr.Application.Security;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ecr.Api.Security;

/// <summary>
/// Вимагає функціональні права ДО читання тіла запиту (S1-04, аудит 3).
/// </summary>
/// <remarks>
/// ⛔ Авторизаційні фільтри MVC виконуються до прив'язки моделі. Право, яке перевіряє лише обробник, спрацьовує
/// ПІСЛЯ того, як <c>[FromBody]</c> уже прочитав і розібрав усе тіло: будь-який автентифікований користувач без
/// права змушував сервер приймати й десеріалізувати великий (до 64 МБ) пакет. Тут відмова —
/// <c>403</c> до першого байта тіла.
///
/// ⚠ Це лише РАННЯ перевірка. Обробник перевіряє право так само і далі (єдина точка рішення —
/// <see cref="IAccessDecisionService"/>, а не атрибут з іменем ролі); фільтр її не замінює, а лише переносить
/// відмову вперед. Анонімного користувача фільтр не чіпає — це робить <c>[Authorize]</c> (401).
/// </remarks>
/// <param name="permissions">Коди прав; потрібні ВСІ — так само, як у обробнику.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequirePermissionsBeforeBodyAttribute(params string[] permissions) : Attribute, IAsyncAuthorizationFilter
{
    /// <inheritdoc />
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var services = context.HttpContext.RequestServices;
        var currentUser = (ICurrentUser)services.GetService(typeof(ICurrentUser))!;
        if (currentUser.UserId is null)
        {
            return;
        }

        var access = (IAccessDecisionService)services.GetService(typeof(IAccessDecisionService))!;
        foreach (var permission in permissions)
        {
            await PermissionCheck.RequireAsync(access, currentUser, permission, context.HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}
