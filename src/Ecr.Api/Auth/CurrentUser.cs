using System.Globalization;
using System.Security.Claims;
using Ecr.Api.Middleware;
using Ecr.Application.Common;
using Ecr.Application.Localization;

namespace Ecr.Api.Auth;

/// <summary>
/// Реалізація <see cref="ICurrentUser"/> поверх <c>HttpContext</c>.
/// </summary>
/// <remarks>
/// Нижче рівня входу не видно, **як** користувач увійшов — доменний він чи
/// локальний (ФВ-6.2). Обидва провайдери дають одну cookie і один
/// <see cref="ICurrentUser"/>, тому use-cases не мають жодного розгалуження
/// за способом автентифікації.
/// </remarks>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <summary>Мова за замовчуванням, коли її неможливо визначити.</summary>
    private const string FallbackLanguage = "en";

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Береться саме <c>ecr:uid</c>, а не SID: у локального користувача SID
    /// не існує взагалі, а автором дії в аудиті завжди має бути наш
    /// <c>UserId</c> (D-37, D-86).
    /// </remarks>
    public int? UserId
    {
        get
        {
            var value = Principal?.FindFirstValue(AuthenticationSetup.UserIdClaim);
            return int.TryParse(value, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
    }

    /// <inheritdoc />
    public string? UserName => Principal?.FindFirstValue(ClaimTypes.Name);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Читається <c>ClaimTypes.GroupSid</c> — саме те, що кладе в токен
    /// Negotiate. На вході це весь квиток; далі — наша cookie, куди вхід
    /// переносить лише групи з призначеннями (<c>AuthController.SignInAsync</c>).
    /// У локального користувача таких заявок немає, і перелік
    /// порожній: це не помилка, а нормальний стан (`P-02`).
    /// </remarks>
    /// <inheritdoc />
    public long? SimulationSessionId
    {
        get
        {
            var value = Principal?.FindFirstValue(AuthenticationSetup.SimulationSessionClaim);
            return long.TryParse(value, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
    }

    public IReadOnlyList<string> GroupSids =>
        Principal?.FindAll(ClaimTypes.GroupSid).Select(c => c.Value).ToArray() ?? [];

    /// <inheritdoc />
    /// <remarks>
    /// Якщо ідентифікатора немає в <c>HttpContext.Items</c>, це означає, що
    /// <see cref="CorrelationIdMiddleware"/> не відпрацював — тобто конвеєр
    /// зібрано неправильно. Генерувати новий тут означало б приховати дефект:
    /// у логах був би один ідентифікатор, у відповіді інший.
    /// </remarks>
    public string CorrelationId
    {
        get
        {
            var context = accessor.HttpContext
                ?? throw new InvalidOperationException(
                    "ICurrentUser використано поза запитом: HttpContext немає.");

            return context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw) && raw is string id
                ? id
                : throw new InvalidOperationException(
                    "У HttpContext немає CorrelationId. CorrelationIdMiddleware має стояти " +
                    "ПЕРШИМ у конвеєрі — див. Program.cs.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Мова з профілю користувача, далі <c>Accept-Language</c>, далі мова за
    /// замовчуванням. Самі тексти беруться з <c>IUiStringCatalog</c>, а не
    /// хардкодом (D-95): додати мову має означати запис у реєстр, а не збірку.
    /// </remarks>
    public string Language
    {
        get
        {
            var profile = Principal?.FindFirstValue("ecr:lang");
            if (!string.IsNullOrWhiteSpace(profile))
            {
                return profile;
            }

            var accept = accessor.HttpContext?.Request.Headers.AcceptLanguage.ToString();
            if (string.IsNullOrWhiteSpace(accept))
            {
                return FallbackLanguage;
            }

            // Беремо перший тег без ваги: "ru-RU,ru;q=0.9,en;q=0.8" → "ru-RU".
            var first = accept.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .FirstOrDefault();
            var tag = first?.Split(';')[0].Trim();

            // ⚠ `LanguageCodes.FromTag`, а не зріз до первинного субтега: у
            // заголовку приходить ТЕГ BCP-47, а каталог і реєстр
            // (`sys_ecr.Language`) оперують внутрішніми КОДАМИ. Для казахської
            // це різні рядки — тег `kk`, код `kz`, — тож без переведення
            // анонімний запит із казахського браузера просив каталог мовою,
            // якої в реєстрі немає, і мовчки отримував мову за замовчуванням.
            var code = LanguageCodes.FromTag(tag);
            return string.IsNullOrEmpty(code) ? FallbackLanguage : code;
        }
    }

    private ClaimsPrincipal? Principal
        => accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;
}
