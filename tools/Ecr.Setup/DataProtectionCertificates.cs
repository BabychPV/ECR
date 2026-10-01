using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ecr.Setup;

/// <summary>
/// Сертифікат зі сховища машини — лише ті поля, що потрібні майстру (S11).
/// </summary>
/// <param name="Thumbprint">Відбиток (великими літерами, без пробілів).</param>
/// <param name="Subject">Суб'єкт.</param>
/// <param name="NotBefore">Початок строку дії (місцевий час, як у сховищі Windows).</param>
/// <param name="NotAfter">Кінець строку дії (місцевий час).</param>
/// <param name="HasPrivateKey">Чи є закритий ключ.</param>
internal sealed record CertificateInfo(
    string Thumbprint, string Subject, DateTime NotBefore, DateTime NotAfter, bool HasPrivateKey);

/// <summary>
/// Шов до сховища сертифікатів: тести майстра не залежать від
/// <c>Cert:\LocalMachine\My</c> машини, на якій їх запускають.
/// </summary>
internal interface ICertificateSource
{
    /// <summary>Усі сертифікати сховища; порожньо, якщо сховище недоступне.</summary>
    public IReadOnlyList<CertificateInfo> List();
}

/// <summary>Справжнє джерело — <c>LocalMachine\My</c>, те саме, де шукає застосунок.</summary>
internal sealed class LocalMachineCertificateSource : ICertificateSource
{
    /// <inheritdoc />
    public IReadOnlyList<CertificateInfo> List()
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

            var result = new List<CertificateInfo>(store.Certificates.Count);
            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    result.Add(new CertificateInfo(
                        certificate.Thumbprint,
                        certificate.Subject,
                        certificate.NotBefore,
                        certificate.NotAfter,
                        certificate.HasPrivateKey));
                }
            }

            return result;
        }
        catch (Exception unreachable) when (unreachable is CryptographicException
                                                         or UnauthorizedAccessException
                                                         or PlatformNotSupportedException)
        {
            // Недосяжне сховище = жодного сертифіката на вибір: крок покаже
            // порожній список і не пропустить далі — та сама відмова.
            return [];
        }
    }
}

/// <summary>
/// Правила вибору сертифіката HTTPS у майстрі (D14-08): ті самі, що й для Data Protection
/// (у сховищі, із закритим ключем, чинний за датами), лише текст порожнього вибору свій.
/// </summary>
internal static class HttpsCertificateRules
{
    /// <summary>Чи годиться вибраний відбиток HTTPS ЗАРАЗ (при виборі й перед стартом розгортання).</summary>
    /// <param name="thumbprint">Відбиток зі стану майстра.</param>
    /// <param name="source">Сховище сертифікатів.</param>
    /// <param name="now">Поточний місцевий час.</param>
    /// <param name="error">Зрозуміла причина відмови.</param>
    public static bool TryValidate(string? thumbprint, ICertificateSource source, DateTime now, out string error)
    {
        if (DataProtectionCertificateRules.Normalize(thumbprint).Length == 0)
        {
            error = "Select the HTTPS certificate issued for the server name. Without it users cannot sign in from other "
                + "computers: the session cookie is Secure and is not sent over HTTP. "
                + "Or choose the reverse-proxy or the test-stand (HTTP) option.";
            return false;
        }

        return DataProtectionCertificateRules.TryValidate(thumbprint, source, now, out error);
    }
}

/// <summary>
/// Правила вибору сертифіката Data Protection у майстрі (S11) — ті самі, що
/// перевіряє <c>deploy-ecr.ps1</c> на кроці 1, плюс строк дії.
/// </summary>
internal static class DataProtectionCertificateRules
{
    /// <summary>Сертифікати, які можна запропонувати: лише із закритим ключем, найдовший строк першим.</summary>
    /// <param name="certificates">Уміст сховища.</param>
    public static IReadOnlyList<CertificateInfo> Selectable(IEnumerable<CertificateInfo> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);

        return certificates
            .Where(c => c.HasPrivateKey)
            .OrderByDescending(c => c.NotAfter)
            .ToList();
    }

    /// <summary>Відбиток без пробілів і нерозривних пробілів, великими літерами.</summary>
    /// <param name="thumbprint">Відбиток у будь-якому вигляді.</param>
    public static string Normalize(string? thumbprint)
        => thumbprint is null
            ? string.Empty
            : new string(thumbprint.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Рядок для списку: суб'єкт, строк дії, відбиток.</summary>
    /// <param name="certificate">Сертифікат.</param>
    public static string Describe(CertificateInfo certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{certificate.Subject} (valid until {certificate.NotAfter:yyyy-MM-dd}, thumbprint {certificate.Thumbprint})");
    }

    /// <summary>
    /// Чи годиться вибраний відбиток ЗАРАЗ. Перевіряється двічі: при виборі й
    /// перед стартом розгортання (сертифікат міг зникнути чи прострочитися,
    /// поки майстер стояв відкритим).
    /// </summary>
    /// <param name="thumbprint">Відбиток зі стану майстра.</param>
    /// <param name="source">Сховище сертифікатів.</param>
    /// <param name="now">Поточний місцевий час.</param>
    /// <param name="error">Зрозуміла причина відмови.</param>
    public static bool TryValidate(string? thumbprint, ICertificateSource source, DateTime now, out string error)
    {
        ArgumentNullException.ThrowIfNull(source);

        var normalized = Normalize(thumbprint);
        if (normalized.Length == 0)
        {
            error = "Select the Data Protection certificate. Without it the service does not start in Production: "
                + "session keys would be stored unencrypted in the database.";
            return false;
        }

        var found = source.List()
            .FirstOrDefault(c => string.Equals(Normalize(c.Thumbprint), normalized, StringComparison.Ordinal));

        if (found is null)
        {
            error = $"The certificate {normalized} is no longer in Cert:\\LocalMachine\\My. "
                + "Import it again (PFX with the private key) or select another one.";
            return false;
        }

        if (!found.HasPrivateKey)
        {
            error = $"The certificate {normalized} has no private key. "
                + "Import the PFX with the private key: without it no session can be opened.";
            return false;
        }

        if (found.NotAfter < now)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"The certificate {normalized} expired on {found.NotAfter:yyyy-MM-dd}. Select a valid certificate.");
            return false;
        }

        if (found.NotBefore > now)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"The certificate {normalized} is not valid until {found.NotBefore:yyyy-MM-dd}. Select a valid certificate.");
            return false;
        }

        error = string.Empty;
        return true;
    }
}
