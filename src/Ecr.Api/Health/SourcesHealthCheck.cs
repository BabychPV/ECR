using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
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
public sealed class SourcesHealthCheck(
    ICollectionStore? sources,
    IUiStringCatalog catalog,
    ICurrentUser currentUser,
    ISecretProvider? secrets = null,
    IEndpointNetwork? network = null,
    HealthResultCache? cache = null) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        // ⛔ P1-03 (AUDIT-2026-10-09b): `/health/ready` анонімний, а ця перевірка — найважча
        // з усіх: до 5000 сутностей, до 20 000 прогонів і gaps-and-islands по
        // `itg.CollectionCoverage` за 45 днів на КОЖНУ пробу, у власному scope і з'єднанні.
        // Як і `JobsHealthCheck`: результат живе `HealthResultCache.Ttl`, одночасні промахи
        // чекають одного обчислення; ключ — мова, нормалізована реєстром (L1-02).
        if (cache is null)
        {
            return await ComputeAsync(cancellationToken).ConfigureAwait(false);
        }

        var language = await catalog.ResolveLanguageAsync(currentUser.Language, cancellationToken).ConfigureAwait(false);
        return await cache.GetOrAddAsync($"ready:sources:{language}", ComputeAsync, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HealthCheckResult> ComputeAsync(CancellationToken cancellationToken)
    {
        // ⚠ Порт лишається необов'язковим: перевірка, яку неможливо створити,
        // валить увесь `/health/ready` винятком контейнера (`Q-051`).
        if (sources is null)
        {
            var notRegistered = await Text(
                "health.sources.notRegistered", "The collection store is not registered in the container.",
                null, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Unhealthy(notRegistered);
        }

        var entities = await sources.ListSourceEntitiesAsync(cancellationToken).ConfigureAwait(false);
        var active = entities.Where(e => e.IsActive).ToList();

        // ⚠ Жодного активного джерела — це `Healthy`, а не `Degraded`. Система
        // без інтеграції працездатна: дані вводять руками. Жовтий колір тут
        // означав би «щось не так» там, де все за налаштуванням.
        if (active.Count == 0)
        {
            var noneActive = await Text(
                "health.sources.noneActive", "No active collection sources.", null, cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Healthy(noneActive, data: Data(0, 0, 0));
        }

        var failed = active.Count(e => e.LastRun?.Status is "Failed");

        // ⛔ Сутність, яка НІКОЛИ не запускалася, рахується як прогалина, а не
        // як «поки що нічого». Саме так виглядає забуте налаштування: воно
        // мовчить, і мовчання приймають за спокій.
        // D5: рахуємо УНІКАЛЬНІ сутності (union): та, що має прогалину І ще не
        // запускалась, раніше рахувалась двічі.
        var gaps = active.Count(e => e.OldestGap is not null || e.LastRun is null);

        // Negotiate-джерело без PiWebApi:AllowedHosts: службові облікові дані
        // підуть на будь-який хост, не заборонений блок-листом (SSRF).
        // ⚠ Рахується ДО гілок збою/прогалин: інакше свіже Negotiate-джерело
        // (ще не запускалося = «прогалина») ховало б цю причину за coverage gap.
        var openNegotiate = await CountNegotiateWithoutAllowlistAsync(active, cancellationToken).ConfigureAwait(false);
        var negotiateSuffix = string.Empty;

        if (openNegotiate > 0)
        {
            negotiateSuffix = " " + await Text(
                "health.sources.negotiateNoAllowlist",
                "Sources with Windows authentication and no allowed-hosts list: {count}. Set PiWebApi:AllowedHosts in the EcrApi configuration and restart EcrApi.",
                Param("count", openNegotiate.ToString(CultureInfo.InvariantCulture)), cancellationToken)
                .ConfigureAwait(false);
        }

        if (failed > 0)
        {
            var failedText = await Text(
                "health.sources.failedCount", "Collection entities with a failed last run: {count}.",
                Param("count", failed.ToString(CultureInfo.InvariantCulture)), cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Unhealthy(failedText + negotiateSuffix, data: Data(active.Count, failed, gaps));
        }

        if (gaps > 0)
        {
            var gapsText = await Text(
                "health.sources.gapsCount", "Collection entities with a coverage gap: {count}.",
                Param("count", gaps.ToString(CultureInfo.InvariantCulture)), cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Degraded(gapsText + negotiateSuffix, data: Data(active.Count, 0, gaps));
        }

        if (openNegotiate > 0)
        {
            return HealthCheckResult.Degraded(negotiateSuffix.TrimStart(), data: Data(active.Count, 0, 0));
        }

        var allCollected = await Text(
            "health.sources.allCollectedNoGaps", "All active sources are collected with no gaps.",
            null, cancellationToken).ConfigureAwait(false);
        return HealthCheckResult.Healthy(allCollected, data: Data(active.Count, 0, 0));
    }

    private async Task<int> CountNegotiateWithoutAllowlistAsync(
        IReadOnlyList<SourceEntityStatus> active, CancellationToken ct)
    {
        if (sources is null || secrets is null || network?.AllowedHosts is { Count: > 0 })
        {
            return 0;
        }

        var count = 0;

        foreach (var id in active
                     .Where(e => string.Equals(e.Transport, nameof(ExternalTransport.PiWebApi), StringComparison.OrdinalIgnoreCase))
                     .Select(e => e.DataSourceId)
                     .Distinct())
        {
            var source = await sources.FindDataSourceAsync(id, ct).ConfigureAwait(false);

            if (source is null)
            {
                continue;
            }

            var bound = secrets.Find(source.SecretName);

            if (string.IsNullOrWhiteSpace(bound)
                || string.Equals(bound.Trim(), "Negotiate", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private Task<string> Text(
        string key, string fallback, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
        => HealthCatalogText.ResolveAsync(catalog, currentUser, key, fallback, parameters, ct);

    private static Dictionary<string, string> Param(string name, string value) => new(1) { [name] = value };

    private static Dictionary<string, object> Data(int active, int failed, int gaps)
        => new(StringComparer.Ordinal)
        {
            ["activeSources"] = active,
            ["failedSources"] = failed,
            ["sourcesWithGaps"] = gaps,
        };
}
