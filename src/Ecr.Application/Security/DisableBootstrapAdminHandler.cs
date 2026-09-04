// src/Ecr.Application/Security/DisableBootstrapAdminHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Вимикає bootstrap-адміністратора, щойно з'явився активний доменний
/// (ФВ-6.18, D-97). **Не видаляє** — запис потрібен в аудиті.
/// </summary>
public sealed class DisableBootstrapAdminHandler(
    IUnitOfWork uow, IAuditWriter audit, IClock clock)
{
    public Task<bool> HandleAsync(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) умова — є щонайменше один АКТИВНИЙ доменний користувач із " +
            "   правом Security.ManageUsers. Не просто «є доменний користувач»: " +
            "   інакше перший же рядовий співробітник вимкне адміністратора;\n" +
            "2) user.DisableAsBootstrap(): IsActive = false, новий SecurityStamp;\n" +
            "3) IsBootstrapAdmin лишити — за ним видно, звідки взявся перший " +
            "   адміністратор системи;\n" +
            "4) в аудит; повернути, чи справді вимкнули;\n" +
            "5) викликати після кожного призначення ролі, а не за розкладом: " +
            "   вікно між появою доменного адміністратора і вимкненням має бути " +
            "   якомога коротшим.");
}
