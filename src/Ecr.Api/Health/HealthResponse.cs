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

                    // ⛔ L1-11 (Q-221): опис перевірки «db» несе ті самі подробиці (RCSI-скрипт, файлові групи,
                    // відбитки сертифікатів) — анонімному /health/ready його теж не віддаємо.
                    // A2-11: замість null — коротка фраза з білого списку (PublicHealthReason).
                    Description(e.Value, redactedChecks, redacted: redactedChecks?.Contains(e.Key) == true),
                    e.Value.Duration.TotalMilliseconds,

                    // ⚠ Виняток НЕ віддається клієнту: у ньому бувають імена
                    // об'єктів БД і фрагменти запитів. Клієнту — сам факт, у
                    // логи — подробиці (ФВ-6.11).
                    redactedChecks?.Contains(e.Key) == true
                        ? new Dictionary<string, object>()
                        : WithoutServiceKeys(e.Value.Data)))
                .ToList());

        return JsonSerializer.SerializeAsync(context.Response.Body, payload, Options, context.RequestAborted);
    }

    /// <summary>Опис перевірки для звіту.</summary>
    /// <remarks>
    /// ⛔ A2-11: якщо перевірка кинула виняток сама, фреймворк кладе в опис
    /// <c>ex.Message</c> — а там бувають ім'я сервера, логін, шлях, фрагмент
    /// запиту. В анонімний <c>/health/ready</c> такий текст не йде: замість нього
    /// нейтральна фраза, подробиці — у журналі. Свідомий текст перевірки поруч
    /// із винятком (<c>Degraded(текст, ex)</c>) лишається: він не містить
    /// повідомлення винятку.
    /// </remarks>
    private static string? Description(HealthReportEntry entry, IReadOnlySet<string>? redactedChecks, bool redacted)
    {
        if (redacted)
        {
            return PublicHealthReason.Describe(entry);
        }

        if (redactedChecks is not null && entry.Description is { } text && CarriesExceptionText(text, entry.Exception))
        {
            return PublicHealthReason.CheckFailed;
        }

        return entry.Description;
    }

    private static bool CarriesExceptionText(string description, Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(ex.Message) && description.Contains(ex.Message, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Подробиці без службового ключа <see cref="PublicHealthReason.DataKey"/>.</summary>
    private static IReadOnlyDictionary<string, object> WithoutServiceKeys(IReadOnlyDictionary<string, object> data)
        => data.ContainsKey(PublicHealthReason.DataKey)
            ? data.Where(p => !string.Equals(p.Key, PublicHealthReason.DataKey, StringComparison.Ordinal))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            : data;
}
