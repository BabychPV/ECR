// src/Ecr.Application/Ports/IDataSourceCredentialResolver.cs
using Ecr.Application.Common;
using Ecr.Domain.Entities.External;

namespace Ecr.Application.Ports;

/// <summary>Логін і пароль джерела в момент з'єднання (P-1126-SRCCRED, AC-3).</summary>
/// <remarks>⛔ Пароль — <see cref="SecretString"/>: друк запису дає <c>***</c>, а не значення.</remarks>
/// <param name="UserName">Логін.</param>
/// <param name="Password">Пароль.</param>
public sealed record ResolvedCredentials(string UserName, SecretString Password);

/// <summary>
/// Облікові дані джерела: шифрування пароля перед записом і розшифрування в момент з'єднання (P-1126-SRCCRED, R-23).
/// </summary>
/// <remarks>
/// ⛔ Порт, а не DataProtection в адаптерах: адаптери (<c>Ecr.Adapters.*</c>) не знають ASP.NET DataProtection, як
/// сьогодні не знають, звідки <see cref="ISecretProvider"/> бере значення. Реалізація — в Infrastructure, з окремим
/// призначенням (<c>Ecr.DataSource.Credential.v1</c>), не тим, що в SMTP чи каналів.
/// <para>
/// ⛔ Розшифрування — лише в момент з'єднання (адаптер, перевірка з'єднання). API ніколи не розшифровує: йому досить
/// <see cref="DataSource.HasPassword"/>.
/// </para>
/// </remarks>
public interface IDataSourceCredentialResolver
{
    /// <summary>Шифрує пароль перед записом у <c>ext.DataSource.PasswordProtected</c>.</summary>
    /// <param name="password">Пароль.</param>
    public byte[] Protect(SecretString password);

    /// <summary>
    /// Облікові дані для з'єднання: <c>null</c> — джерело ходить Windows-входом (облікові дані не потрібні).
    /// </summary>
    /// <param name="source">Джерело.</param>
    /// <exception cref="CredentialsUnreadableException">
    /// UserPassword, а пароля немає або його не розшифрувати (кільце ключів після відновлення бази).
    /// </exception>
    public ResolvedCredentials? Resolve(DataSource source);
}

/// <summary>
/// Збережений пароль джерела не читається: блоба немає або DataProtection його не розшифровує (R-03,
/// <c>credentialsUnreadable</c>). Не 500 і не «інше»: людині треба ввести пароль заново.
/// </summary>
/// <param name="code">Код джерела.</param>
public sealed class CredentialsUnreadableException(string code)
    : Errors.SourceAuthenticationException(
        Domain.Errors.ErrorCodes.SourceAuthenticationRefused,
        $"Збережений пароль джерела «{code}» не читається: введіть його заново.",
        new Dictionary<string, object?> { ["messageKey"] = MessageKey, ["code"] = code })
{
    /// <summary>Ключ каталогу.</summary>
    public const string MessageKey = "err.ECR-INT-0502.credentialsUnreadable";
}
