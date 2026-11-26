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

/// <summary>
/// Джерело вже відмовило в автентифікації збереженим паролем (<c>CredentialsRejectedAt</c>, R-03,
/// <c>credentialsRejected</c>): збір і перевірка за id не відкривають з'єднання, доки пароль не замінено — захист облікового
/// запису від блокування.
/// </summary>
/// <param name="code">Код джерела.</param>
public sealed class CredentialsRejectedException(string code)
    : Errors.SourceAuthenticationException(
        Domain.Errors.ErrorCodes.SourceAuthenticationRefused,
        $"Джерело «{code}» уже відхилило збережений пароль: до заміни пароля з'єднання не відкривається.",
        new Dictionary<string, object?> { ["messageKey"] = MessageKey, ["code"] = code })
{
    /// <summary>Ключ каталогу.</summary>
    public const string MessageKey = "err.ECR-INT-0502.credentialsRejected";
}

/// <summary>Позначає відмову автентифікації збереженим паролем (<c>CredentialsRejectedAt</c>, R-03).</summary>
public interface IDataSourceCredentialRejections
{
    /// <summary>
    /// Ставить мітку відмови джерелу в режимі UserPassword — лише якщо рядок ТОЙ САМИЙ, що завантажив прогін (L3):
    /// прогін у польоті не позначає щойно замінений пароль.
    /// </summary>
    /// <param name="source">Джерело, яким відкривали з'єднання (його <c>Id</c> і <c>RowVersion</c>).</param>
    /// <param name="ct">Скасування.</param>
    public Task ReportRejectedAsync(DataSource source, CancellationToken ct);
}

/// <summary>
/// Скидає пули з'єднань драйвера після заміни чи стирання облікових даних (R-20): відкрите з'єднання зі старим паролем
/// не має жити далі в пулі процесу.
/// </summary>
public interface IConnectionPoolReset
{
    /// <summary>Скидає пул.</summary>
    public void Reset();
}

/// <summary>
/// Перевірка з'єднання з НЕЗБЕРЕЖЕНОЇ конфігурації (P-1126-SRCCRED §13.1): адаптер відкриває з'єднання за ефемерною
/// сутністю й обліковими даними з тіла — тією самою точкою підстановки, що й збір.
/// </summary>
public interface IExternalConnectionProbe
{
    /// <summary>Транспорт адаптера.</summary>
    public Domain.Enums.ExternalTransport Transport { get; }

    /// <summary>Відкриває й закриває з'єднання; відмова — виняток, як у збору.</summary>
    /// <param name="spec">Ефемерна (не збережена) сутність.</param>
    /// <param name="credentials">Облікові дані; <c>null</c> — Windows.</param>
    /// <param name="ct">Скасування.</param>
    public Task ProbeAsync(DataSource spec, ResolvedCredentials? credentials, CancellationToken ct);
}
