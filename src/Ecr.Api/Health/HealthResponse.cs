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

    /// <summary>Пише звіт у форматі JSON.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = e.Value.Duration.TotalMilliseconds,

                // ⚠ Виняток НЕ віддається клієнту: у ньому бувають імена
                // об'єктів БД і фрагменти запитів. Клієнту — сам факт, у логи —
                // подробиці (ФВ-6.11).
                data = e.Value.Data,
            }),
        };

        return JsonSerializer.SerializeAsync(context.Response.Body, payload, Options, context.RequestAborted);
    }
}
