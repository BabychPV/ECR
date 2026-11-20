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
/// <param name="DnsNames">DNS-імена сертифіката (SAN; без SAN — CN); <c>null</c> — невідомо (PSS-28).</param>
internal sealed record CertificateInfo(
    string Thumbprint, string Subject, DateTime NotBefore, DateTime NotAfter, bool HasPrivateKey,
    IReadOnlyList<string>? DnsNames = null);

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
                        certificate.HasPrivateKey,
                        ReadDnsNames(certificate)));
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

    /// <summary>
    /// DNS-імена з розширення Subject Alternative Name; без нього — DNS-ім'я з CN (так робить Windows).
    /// </summary>
    /// <remarks>Збій читання розширення — порожній перелік: майстер тоді просто не підставить імена.</remarks>
    internal static IReadOnlyList<string> ReadDnsNames(X509Certificate2 certificate)
    {
        try
        {
            var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                .SelectMany(e => e.EnumerateDnsNames())
                .ToList();
            if (names.Count == 0 && certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false) is { Length: > 0 } cn)
            {
                names.Add(cn);
            }

            return names;
        }
        catch (Exception unreadable) when (unreadable is CryptographicException or ArgumentException)
        {
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

/// <summary>
/// ⛔ PSS-28: перелік дозволених імен хоста (<c>AllowedHosts</c> ASP.NET) — що пропонує майстер і що
/// він пропускає далі в <c>deploy-ecr.ps1 -AllowedHosts</c>.
/// </summary>
/// <remarks>
/// Хибне значення зачиняє користувачам вхід (запит з іншим <c>Host</c> дістає 400), тому порожнє значення
/// не приймається: або імена, або явна <c>*</c> («будь-який хост», типове значення застосунку). Пропонується
/// список DNS-імен вибраного сертифіката HTTPS плюс <c>localhost</c>: перевірку здоров'я наприкінці розгортання
/// скрипт виконує на <c>localhost</c>, і без цього імені вона отримала б 400.
/// </remarks>
internal static class AllowedHostsRules
{
    /// <summary>«Будь-який хост» — типове значення застосунку.</summary>
    public const string Any = "*";

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    // ім'я або `*.ім'я`; `[IPv6]` — у квадратних дужках. ⛔ БЕЗ порту: фільтр хостів ASP.NET Core відкидає порт
    // із заголовка Host, а записи AllowedHosts порівнює як є — запис `ecr.example.com:8443` не збігся б НІКОЛИ
    // (усі користувачі отримали б 400, а перевірка розгортання на localhost лишилась би зеленою; рецензія L9 H1).
    private static readonly System.Text.RegularExpressions.Regex EntryPattern = new(
        @"^(?:(?:\*\.)?[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*|\[[0-9A-Fa-f:.]+\])$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>Значення за DNS-іменами сертифіката: унікальні імена + <c>localhost</c>; без імен — <c>*</c>.</summary>
    /// <param name="dnsNames">DNS-імена сертифіката.</param>
    public static string FromCertificate(IEnumerable<string>? dnsNames)
    {
        var names = (dnsNames ?? [])
            .Select(n => n.Trim().ToLowerInvariant())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            return Any;
        }

        if (!names.Contains("localhost", StringComparer.Ordinal))
        {
            names.Add("localhost");
        }

        return string.Join(';', names);
    }

    /// <summary>Розбирає й перевіряє введене значення; <paramref name="normalized"/> — через <c>;</c>.</summary>
    /// <param name="text">Текст поля (розділювачі: <c>;</c>, кома, пробіл).</param>
    /// <param name="normalized">Нормалізоване значення.</param>
    /// <param name="error">Зрозуміла причина відмови.</param>
    public static bool TryNormalize(string? text, out string normalized, out string error)
    {
        normalized = string.Empty;
        var entries = (text ?? string.Empty)
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count == 0)
        {
            error = "Enter the host names users type in the browser (separated by ';'), or '*' to allow any host. "
                + "An empty value is not accepted.";
            return false;
        }

        if (entries.Contains(Any, StringComparer.Ordinal))
        {
            if (entries.Count > 1)
            {
                error = "'*' means any host and cannot be combined with host names. Remove '*' or the other entries.";
                return false;
            }

            normalized = Any;
            error = string.Empty;
            return true;
        }

        foreach (var entry in entries)
        {
            if (HasPort(entry))
            {
                error = $"'{entry}' contains a port. Enter the host name without the port (for example ecr.example.com): "
                    + "the server compares host names without ports, so an entry with a port would never match.";
                return false;
            }

            if (!IsValidEntry(entry))
            {
                error = $"'{entry}' is not a valid host name. Use names like ecr.example.com or *.example.com "
                    + "(no scheme, no port, no path).";
                return false;
            }
        }

        normalized = string.Join(';', entries);
        error = string.Empty;
        return true;
    }

    private static bool IsValidEntry(string entry)
    {
        try
        {
            return EntryPattern.IsMatch(entry);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Запис із портом: <c>ім'я:порт</c> або <c>[IPv6]:порт</c> (двокрапки всередині дужок — частина адреси).</summary>
    private static bool HasPort(string entry)
        => entry.StartsWith('[')
            ? entry.IndexOf(']', StringComparison.Ordinal) is var close and >= 0 && close < entry.Length - 1 && entry[close + 1] == ':'
            : entry.Contains(':', StringComparison.Ordinal);
}
