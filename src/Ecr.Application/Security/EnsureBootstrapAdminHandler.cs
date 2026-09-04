// src/Ecr.Application/Security/EnsureBootstrapAdminHandler.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Security;

/// <summary>
/// Створює локального адміністратора при першому старті (ФВ-6.18, D-115).
/// Пароль — зі змінної середовища <c>ECR_Bootstrap__Password</c>.
/// </summary>
/// <remarks>
/// Без цього запису систему неможливо налаштувати до під'єднання домену. Але
/// він же є найпривабливішою мішенню, тому живе за трьома обмеженнями: один на
/// систему, обов'язкова зміна пароля при першому вході, автоматичне вимкнення
/// після появи доменного адміністратора.
/// </remarks>
public sealed class EnsureBootstrapAdminHandler(
    IPasswordHasher hasher, IUnitOfWork uow, IClock clock)
{
    public Task HandleAsync(string? bootstrapPassword, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) якщо bootstrap-запис уже існує — не робити НІЧОГО і " +
            "   змінну ігнорувати: повторне створення скинуло б пароль " +
            "   працюючої системи;\n" +
            "2) якщо запису немає і змінної немає — ПОПЕРЕДЖЕННЯ, не помилка. " +
            "   Це нормальний стан контуру, де вхід переведено на домен (D-115);\n" +
            "3) створити User(Provider = Local, IsBootstrapAdmin = true, " +
            "   MustChangePassword = true) з роллю адміністратора;\n" +
            "4) ⛔ пароль не логувати, не повертати, не класти в жодне " +
            "   повідомлення (ФВ-6.11);\n" +
            "5) унікальність гарантує частковий індекс UX_User_Bootstrap — " +
            "   гонка двох інстансів при старті дасть помилку вставки, а не " +
            "   другий запис.");
}
