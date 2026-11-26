// src/Ecr.Infrastructure/Integration/DataSourceCredentialResolver.cs
using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Microsoft.AspNetCore.DataProtection;

namespace Ecr.Infrastructure.Integration;

/// <summary>
/// Облікові дані джерела під DataProtection (P-1126-SRCCRED, R-23): власне призначення, кільце ключів — те саме, що в
/// застосунку (<c>sec.DataProtectionKey</c>).
/// </summary>
/// <remarks>
/// ⛔ <see cref="Purpose"/> не перейменовувати: блоби, зашифровані під старим призначенням, перестали б читатися, і
/// всі UserPassword-джерела стали б <c>credentialsUnreadable</c>.
/// </remarks>
public sealed class DataSourceCredentialResolver(IDataProtectionProvider provider) : IDataSourceCredentialResolver
{
    /// <summary>Призначення DataProtection — окреме від SMTP і секретів каналів.</summary>
    public const string Purpose = "Ecr.DataSource.Credential.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    /// <inheritdoc />
    public byte[] Protect(SecretString password)
    {
        ArgumentNullException.ThrowIfNull(password);

        return _protector.Protect(Encoding.UTF8.GetBytes(password.Reveal()));
    }

    /// <inheritdoc />
    public ResolvedCredentials? Resolve(DataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // ⛔ Windows (і наявні до міграції рядки) — жодного Unprotect (AC-R).
        if (source.AuthMode != SourceAuthMode.UserPassword)
        {
            return null;
        }

        if (source.PasswordProtected is not { Length: > 0 } blob || string.IsNullOrEmpty(source.UserName))
        {
            throw new CredentialsUnreadableException(source.Code);
        }

        try
        {
            return new ResolvedCredentials(source.UserName, new SecretString(Encoding.UTF8.GetString(_protector.Unprotect(blob))));
        }
        catch (CryptographicException)
        {
            // ⛔ Ні блоба, ні тексту винятку DataProtection назовні: лише «введіть пароль заново» (R-03).
            throw new CredentialsUnreadableException(source.Code);
        }
    }
}
