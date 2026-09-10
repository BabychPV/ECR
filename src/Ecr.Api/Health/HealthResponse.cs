using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Формат відповіді health-ендпоінтів.
/// </summary>
/// <remarks>
/// Стандартний писар віддає саме слово <c>Healthy</c> і більше нічого. Для
/// <c>/health/db</c> цього замало: адміністратору потрібні режим редакції,
/// RCSI, файлові групи і запас партицій — інакше він не зрозуміє, чому нічна
/// операція поводиться інакше, ніж на тесті (АРХ-7 п. 5).
///
/// ⚠ Файла немає в дереві `05-skeleton.md` §1 (`Q-050`): писар потрібен, бо
/// DoD Етапу 1 вимагає, щоб <c>/health/db</c> «показував режим редакції», а
/// показати його стандартним writer'ом неможливо.
/// </remarks>
public static class HealthResponse
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Перевірки, чиї подробиці НЕ йдуть в анонімно доступні звіти.</summary>
    /// <remarks>
    /// ⛔ Q-221: <c>DatabaseHealthCheck</c> зареєстрований з тегами <c>["db",
    /// "ready"]</c> — тобто той самий екземпляр, з тими самими <c>Data</c>
    /// (редакція SQL Server, RCSI, файлові групи, запас партицій), потрапляє
    /// і в <c>/health/db</c> (тепер під <c>RequireAuthorization</c>), і в
    /// <c>/health/ready</c> (навмисно анонімний — його читає інсталятор і
    /// моніторинг, D-139). Гейт на самому <c>/health/db</c> нічого не
    /// закриває, доки писар сліпо копіює <c>Data</c> В ОБИДВА звіти: та сама
    /// «подробиця» просто дублюється в ендпоінт, де на неї ще ніхто не
    /// поставив авторизацію — і продовжила б витікати навіть після фіксу
    /// самого <c>/health/db</c>.
    /// </remarks>
    private static readonly IReadOnlySet<string> ReadyReportRedactedChecks =
        new HashSet<string>(StringComparer.Ordinal) { "db" };

    /// <summary>Пише звіт у форматі JSON, з усіма подробицями (для <c>/health/db</c>).</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
        => WriteAsync(context, report, redactedChecks: null);

    /// <summary>
    /// Пише звіт у форматі JSON для <c>/health/ready</c>: подробиці перевірок
    /// із <see cref="ReadyReportRedactedChecks"/> порожні — сам статус і опис
    /// лишаються (моніторингу потрібен саме він), детальні дані — ні.
    /// </summary>
    public static Task WriteReadyAsync(HttpContext context, HealthReport report)
        => WriteAsync(context, report, ReadyReportRedactedChecks);

    private static Task WriteAsync(HttpContext context, HealthReport report, IReadOnlySet<string>? redactedChecks)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json; charset=utf-8";

        // ⛔ НАЗВАНИЙ тип, а не анонімний об'єкт. Анонімний неможливо описати в
        // OpenAPI: у схемі його немає, згенерувати клієнтський тип нема з чого,
        // і клієнт пише свій — саме звідси `A7-04`/`A7-36`, коли сервер писав
        // `checks`, а клієнт читав `entries`, і дашборд відкривався порожнім.
        var payload = new HealthReportDto(
            report.Status.ToString(),
            report.TotalDuration.TotalMilliseconds,
            report.Entries
                .Select(e => new HealthCheckDto(
                    e.Key,
                    e.Value.Status.ToString(),
                    e.Value.Description,
                    e.Value.Duration.TotalMilliseconds,

                    // ⚠ Виняток НЕ віддається клієнту: у ньому бувають імена
                    // об'єктів БД і фрагменти запитів. Клієнту — сам факт, у
                    // логи — подробиці (ФВ-6.11).
                    redactedChecks?.Contains(e.Key) == true
                        ? new Dictionary<string, object>()
                        : e.Value.Data))
                .ToList());

        return JsonSerializer.SerializeAsync(context.Response.Body, payload, Options, context.RequestAborted);
    }
}
