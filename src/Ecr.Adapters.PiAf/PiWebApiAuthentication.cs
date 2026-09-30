using System.Net;
using System.Net.Http.Headers;

namespace Ecr.Adapters.PiAf;

/// <summary>Як PI Web API автентифікує запити ECR.</summary>
public enum PiWebApiAuthMode
{
    /// <summary>Заголовок <c>Authorization: Basic …</c> зі значення секрету.</summary>
    Basic,

    /// <summary>Заголовок <c>Authorization: Bearer …</c> (або іншої названої схеми) зі значення секрету.</summary>
    Bearer,

    /// <summary>
    /// Windows-автентифікація (Kerberos/NTLM) службовим обліковим записом
    /// процесу ECR; заголовка з секрету немає, облікові дані дає ОС.
    /// </summary>
    Negotiate,
}

/// <summary>
/// Вибір режиму автентифікації PI Web API і обробника HTTP під нього.
/// </summary>
/// <remarks>
/// ⚠ Режим — це **налаштування джерела**, а не гілка коду (<c>P-12</c>), і
/// живе там, де вже живуть облікові дані джерела: у значенні секрету, ім'я
/// якого стоїть у <c>ext.DataSource.SecretName</c>. Окремої колонки під
/// режим немає свідомо: вона потребувала б міграції, а вся потрібна
/// інформація вже є в секреті.
/// <list type="bullet">
/// <item><c>Basic dXNlcjpwYXNz</c> → <see cref="PiWebApiAuthMode.Basic"/>;</item>
/// <item><c>Bearer eyJ…</c> або значення без пробілу → <see cref="PiWebApiAuthMode.Bearer"/>
/// (інша схема перед пробілом передається як є — поведінка до цієї зміни);</item>
/// <item><c>Negotiate</c> (без значення), або секрет відсутній чи порожній →
/// <see cref="PiWebApiAuthMode.Negotiate"/>.</item>
/// </list>
/// <para>
/// ⛔ Відсутній секрет раніше означав «жодного заголовка» на звичайному
/// обробнику, тобто анонімний запит — хоча <c>ISecretProvider</c> і
/// <c>D-34</c> описують його як інтегровану автентифікацію службового
/// облікового запису. PI Web API з Kerberos відповідав на такий запит
/// <c>401</c>. Тепер відсутній секрет справді йде під службовим обліковим
/// записом; PI Web API з анонімним доступом від цього не ламається —
/// облікові дані надсилаються лише у відповідь на виклик <c>401</c>.
/// </para>
/// </remarks>
public static class PiWebApiAuthentication
{
    /// <summary>Ім'я клієнта <see cref="IHttpClientFactory"/> для режиму <see cref="PiWebApiAuthMode.Negotiate"/>.</summary>
    public const string NegotiateClientName = "PiWebApi.Negotiate";

    /// <summary>Значення секрету, що вмикає Windows-автентифікацію явно.</summary>
    public const string NegotiateKeyword = "Negotiate";

    /// <summary>Режим за значенням секрету джерела.</summary>
    /// <param name="secret">Значення секрету; <c>null</c> — не налаштовано.</param>
    public static PiWebApiAuthMode ModeOf(string? secret)
    {
        var value = secret?.Trim();

        if (string.IsNullOrEmpty(value)
            || string.Equals(value, NegotiateKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return PiWebApiAuthMode.Negotiate;
        }

        var separator = value.IndexOf(' ', StringComparison.Ordinal);

        return separator > 0 && string.Equals(value[..separator], "Basic", StringComparison.OrdinalIgnoreCase)
            ? PiWebApiAuthMode.Basic
            : PiWebApiAuthMode.Bearer;
    }

    /// <summary>Заголовок <c>Authorization</c> для режимів із секретом; для Negotiate — <c>null</c>.</summary>
    /// <param name="secret">Значення секрету.</param>
    /// <remarks>⛔ Значення секрету не логується (ФВ-6.11).</remarks>
    public static AuthenticationHeaderValue? HeaderOf(string? secret)
    {
        if (ModeOf(secret) == PiWebApiAuthMode.Negotiate)
        {
            return null;
        }

        var value = secret!.Trim();
        var separator = value.IndexOf(' ', StringComparison.Ordinal);

        return separator > 0
            ? new AuthenticationHeaderValue(value[..separator], value[(separator + 1)..])
            : new AuthenticationHeaderValue("Bearer", value);
    }

    /// <summary>Первинний обробник HTTP під режим.</summary>
    /// <param name="mode">Режим автентифікації.</param>
    /// <remarks>
    /// ⚠ Для заголовкових режимів облікові дані процесу вимкнено явно: інакше
    /// невірний Basic/Bearer міг би «врятуватися» Kerberos-квитком служби, і
    /// помилка налаштування не проявилася б як помилка (<c>H-20</c>).
    /// <para>
    /// ⛔ Negotiate: <c>PreAuthenticate = false</c> — облікові дані йдуть лише у
    /// відповідь на виклик <c>401</c> цього хоста, а не «наперед»; і
    /// <c>AllowAutoRedirect = false</c> — перенаправлення на ІНШИЙ хост не
    /// повинно тягти за собою Windows-автентифікацію службового облікового
    /// запису: 3xx стає відмовою збору, а не тихим Kerberos/NTLM-обміном із
    /// чужим сервером.
    /// </para>
    /// </remarks>
    public static HttpMessageHandler CreatePrimaryHandler(PiWebApiAuthMode mode)
        => mode == PiWebApiAuthMode.Negotiate
            ? new SocketsHttpHandler
            {
                // Еквівалент HttpClientHandler.UseDefaultCredentials = true.
                Credentials = CredentialCache.DefaultCredentials,
                PreAuthenticate = false,
                AllowAutoRedirect = false,
                ConnectCallback = GuardedSocketConnect.ConnectAsync,
            }
            : new SocketsHttpHandler { Credentials = null, ConnectCallback = GuardedSocketConnect.ConnectAsync };
}
