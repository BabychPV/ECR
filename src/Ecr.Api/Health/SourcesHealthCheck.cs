using Ecr.Application.Ports;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Стан зовнішніх джерел: коли востаннє був успішний збір і чи немає прогалин
/// у покритті.
/// </summary>
/// <remarks>
/// Ознака здоров'я інтеграції — <b>журнал покриття</b>, а не тиша (ІНТ-3.3):
/// система, яка «нічого не повідомляє», і система, яка «нічого не зібрала»,
/// ззовні однакові.
///
/// ⛔ До ЕТАПУ 7 перевірка повертала <c>Degraded</c> із текстом «збір
/// з'явиться на Етапі 5». Етап 5 закритий, збір працює, порт
/// <c>ICollectionStore</c> віддає і останній прогін, і найстарішу прогалину —
/// а <c>/health/ready</c> усе одно був жовтим **завжди**. Перевірка, яка
/// ніколи не змінює відповіді, не перевіряє нічого.
/// </remarks>
public sealed class SourcesHealthCheck(ICollectionStore? sources) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        // ⚠ Порт лишається необов'язковим: перевірка, яку неможливо створити,
        // валить увесь `/health/ready` винятком контейнера (`Q-051`).
        if (sources is null)
        {
            return HealthCheckResult.Unhealthy("Сховище збору не зареєстроване в контейнері.");
        }

        var entities = await sources.ListSourceEntitiesAsync(cancellationToken).ConfigureAwait(false);
        var active = entities.Where(e => e.IsActive).ToList();

        // ⚠ Жодного активного джерела — це `Healthy`, а не `Degraded`. Система
        // без інтеграції працездатна: дані вводять руками. Жовтий колір тут
        // означав би «щось не так» там, де все за налаштуванням.
        if (active.Count == 0)
        {
            return HealthCheckResult.Healthy(
                "Активних джерел збору немає.",
                data: Data(0, 0, 0));
        }

        var failed = active.Count(e => e.LastRun?.Status is "Failed");
        var withGaps = active.Count(e => e.OldestGap is not null);

        // ⛔ Джерело, яке НІКОЛИ не запускалося, рахується як прогалина, а не
        // як «поки що нічого». Саме так виглядає забуте налаштування: воно
        // мовчить, і мовчання приймають за спокій.
        var neverRan = active.Count(e => e.LastRun is null);

        if (failed > 0)
        {
            return HealthCheckResult.Unhealthy(
                $"Джерел з невдалим останнім збором: {failed}.",
                data: Data(active.Count, failed, withGaps + neverRan));
        }

        if (withGaps + neverRan > 0)
        {
            return HealthCheckResult.Degraded(
                $"Джерел із прогалиною в покритті: {withGaps + neverRan}.",
                data: Data(active.Count, 0, withGaps + neverRan));
        }

        return HealthCheckResult.Healthy(
            "Усі активні джерела зібрані без прогалин.",
            data: Data(active.Count, 0, 0));
    }

    private static Dictionary<string, object> Data(int active, int failed, int gaps)
        => new(StringComparer.Ordinal)
        {
            ["activeSources"] = active,
            ["failedSources"] = failed,
            ["sourcesWithGaps"] = gaps,
        };
}
