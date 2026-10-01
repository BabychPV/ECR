using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace Ecr.Api.Health;

/// <summary>
/// Чим захищені ключі кільця DataProtection — для показу в <c>/health/db</c>.
/// </summary>
/// <remarks>
/// ⛔ Окремий тип, а не читання конфігурації всередині перевірки. Причина не
/// стильова: <c>/health/db</c> має відповідати на питання «що застосунок
/// зробив», а не «що в нього написано в налаштуваннях». Ці два факти
/// розходяться рівно в найцікавішому випадку — відбиток заданий, сертифікат
/// узятий, — і перевірка, яка дивиться в конфігурацію, показала б «захищено»
/// навіть тоді, коли реєстрація впала б до незахищеного режиму.
///
/// ⚠ Значення створює <c>AuthenticationSetup.AddEcrDataProtection</c> — те
/// саме місце, де ухвалюється рішення. Іншого джерела цього факту немає.
/// </remarks>
/// <param name="IsProtected">Чи шифруються ключі сертифікатом.</param>
/// <param name="CertificateThumbprint">
/// Відбиток сертифіката, коли <paramref name="IsProtected"/> істинне.
/// </param>
/// <param name="ReadableThumbprints">
/// Відбитки сертифікатів, якими служба ФАКТИЧНО може розшифрувати ключі
/// (поточний + знайдені попередні, D-267); <c>null</c> — лише поточний.
/// </param>
public sealed record DataProtectionKeyProtection(
    bool IsProtected, string? CertificateThumbprint, IReadOnlyCollection<string>? ReadableThumbprints = null)
{
    /// <summary>Режим HTTP без сертифіката: ключі в таблиці відкрито.</summary>
    public static DataProtectionKeyProtection Unprotected { get; } = new(IsProtected: false, null);

    /// <summary>Ключі шифруються сертифікатом із <c>LocalMachine\My</c>.</summary>
    public static DataProtectionKeyProtection ProtectedBy(string thumbprint, IReadOnlyCollection<string>? readable = null)
        => new(IsProtected: true, thumbprint, readable);

    /// <summary>
    /// Відбитки (SHA-1, великі літери) сертифікатів, якими зашифровані ключі кільця:
    /// <c>EncryptedData/KeyInfo/EncryptedKey/KeyInfo/X509Data/X509Certificate</c>.
    /// Лише публічна частина сертифіката — секретів у результаті немає.
    /// </summary>
    public static IReadOnlySet<string> ThumbprintsInKeyRing(IEnumerable<string> keyXml)
    {
        ArgumentNullException.ThrowIfNull(keyXml);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";
        foreach (var xml in keyXml)
        {
            XElement root;
            try
            {
                root = XElement.Parse(xml);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            foreach (var element in root.Descendants(ds + "X509Certificate"))
            {
                try
                {
                    using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(element.Value.Trim()));
                    result.Add(certificate.Thumbprint);
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException)
                {
                    // Нерозпізнаваний вміст не вигадуємо: відбиток невідомий — пропуск.
                }
            }
        }

        return result;
    }
}
