// src/Ecr.Api/Security/CspSettings.cs

namespace Ecr.Api.Security;

/// <summary>
/// Налаштування Content-Security-Policy: <c>Security:Csp:*</c> (<c>S14</c>).
/// </summary>
/// <remarks>
/// ⛔ Предмет. Повна політика (<c>default-src</c>/<c>script-src</c>/<c>style-src</c>…) їде
/// ЛИШЕ звітною: довести, що клієнт живий під її примусовою версією, можна лише
/// браузерним прогоном (<c>e2e-stand.ps1</c>), а під enforce він не проганявся. Але
/// звітна політика без адресата — це рядок у консолі браузера, який ніхто не
/// читає: вимірювати нічого. Тому <see cref="ReportUri"/> веде на власний
/// ендпоінт (<c>POST /api/v1/csp-report</c>), який пише один рядок журналу на
/// звіт і лічильник <c>ecr.csp.violations</c>.
///
/// ⚠ <see cref="Enforce"/> — лише ЗАДІЛ: типово <c>false</c>, і на релізі-кандидаті
/// його ніхто не вмикає. Коли ввімкнено, повна політика їде як примусова, а
/// звітний заголовок зникає (той самий текст двічі дав би кожне порушення в
/// двох звітах).
/// </remarks>
/// <param name="ReportOnly">Чи віддавати звітну політику (типово так).</param>
/// <param name="Enforce">Чи застосовувати повну політику примусово (типово ні).</param>
/// <param name="ReportUri">Куди браузер шле звіти; порожньо — без звітування.</param>
public sealed record CspSettings(bool ReportOnly, bool Enforce, string ReportUri)
{
    /// <summary>Типова адреса приймача звітів.</summary>
    /// <remarks>
    /// ⚠ Дорівнює маршруту <c>CspReportController</c> — це доводить тест
    /// <c>CspReportControllerTests</c>, а не збіг у тексті.
    /// </remarks>
    public const string DefaultCspReportUri = "/api/v1/csp-report";

    /// <summary>Довжина, довші за яку адреси не мають сенсу в заголовку.</summary>
    private const int MaxReportUriLength = 512;

    /// <summary>Читає налаштування із зведеної конфігурації.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    public static CspSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var reportUri = configuration.GetValue("Security:Csp:ReportUri", DefaultCspReportUri) ?? string.Empty;

        return new CspSettings(
            configuration.GetValue("Security:Csp:ReportOnly", defaultValue: true),
            configuration.GetValue("Security:Csp:Enforce", defaultValue: false),
            reportUri.Trim());
    }

    /// <summary>
    /// Чи годиться значення як <c>report-uri</c>: порожнє (вимкнено), відносний шлях
    /// <c>/…</c> або абсолютна адреса http(s).
    /// </summary>
    /// <remarks>
    /// ⛔ Значення дописується в ЗАГОЛОВОК політики. <c>;</c> відкрив би нову
    /// директиву, пробіл чи кома — обрив значення, керівний символ — розщеплення
    /// заголовка; тому їх немає в жодному з дозволених видів.
    /// </remarks>
    /// <param name="value">Значення ключа.</param>
    public static bool IsValidReportUri(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (value.Length > MaxReportUriLength || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ';' or ',' or '\'' or '"'))
        {
            return false;
        }

        // `//host/…` — протокольно-відносна адреса чужого хоста, не відносний шлях.
        if (value[0] == '/')
        {
            return !value.StartsWith("//", StringComparison.Ordinal);
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
