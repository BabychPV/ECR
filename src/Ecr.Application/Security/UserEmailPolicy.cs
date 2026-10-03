using Ecr.Application.Errors;
using Ecr.Domain.Entities.Notifications;
using Ecr.Domain.Errors;

namespace Ecr.Application.Security;

/// <summary>
/// Перевірка пошти користувача (T1-04): той самий суворий валідатор, що й для адрес каналів і
/// відправника (<see cref="SmtpSettings.IsValidAddress"/>). Адреса потрапляє в одержувачів
/// сповіщень, тож «not-an-email» не має зберігатися.
/// </summary>
public static class UserEmailPolicy
{
    /// <summary>Кидає 422, якщо непорожня адреса не є коректною; порожня (прибрати адресу) — дозволена.</summary>
    /// <param name="email">Адреса з запиту.</param>
    public static void EnsureValid(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || SmtpSettings.IsValidAddress(email.Trim()))
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.UserInvalid,
            $"Адреса «{email.Trim()}» не є коректною поштовою адресою.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-USR-0422.emailInvalid",
                ["email"] = email.Trim(),
            });
    }
}
