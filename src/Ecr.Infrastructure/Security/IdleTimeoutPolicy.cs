using Ecr.Application.Ports;
using Ecr.Application.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Чинна межа бездіяльності з коротким кешем (<c>D-471</c>).
/// </summary>
/// <remarks>
/// ⚠ Кеш — 30 с на вузол. Перевірка cookie звертається сюди на кожному автентифікованому запиті, і похід у базу
/// на кожен запит коштував би дорожче за саму перевірку. Збереження з екрана скидає кеш ЦЬОГО вузла одразу;
/// на інших вузлах (D-32) нове значення діє не пізніше ніж за 30 с — без перезапуску.
///
/// ⛔ Збій читання бази не вимикає межу: діє останнє відоме значення, а без нього — за замовчуванням (30 хв).
/// </remarks>
public sealed class IdleTimeoutPolicy(IServiceScopeFactory scopes, IConfiguration configuration, Domain.Abstractions.IClock clock)
    : IIdleTimeoutPolicy
{
    /// <summary>Ключ конфігурації одноразового стенда: нижня межа в хвилинах (1–5).</summary>
    public const string MinimumMinutesKey = "Auth:IdleTimeout:MinimumMinutes";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private int? _cached;
    private DateTime _expiresAt = DateTime.MinValue;

    /// <inheritdoc />
    public int MinimumMinutes { get; } = ReadMinimum(configuration);

    /// <inheritdoc />
    public async ValueTask<int> GetMinutesAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cached is { } fresh && clock.UtcNow < _expiresAt)
            {
                return fresh;
            }
        }

        int minutes;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var stored = await scope.ServiceProvider.GetRequiredService<ISessionSettingsStore>()
                .ReadIdleTimeoutMinutesAsync(ct).ConfigureAwait(false);
            minutes = Clamp(stored ?? SessionSettings.DefaultIdleTimeoutMinutes);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            lock (_gate)
            {
                return _cached ?? SessionSettings.DefaultIdleTimeoutMinutes;
            }
        }

        lock (_gate)
        {
            _cached = minutes;
            _expiresAt = clock.UtcNow + CacheLifetime;
        }

        return minutes;
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_gate)
        {
            _expiresAt = DateTime.MinValue;
        }
    }

    // ⚠ Значення з бази теж обмежується межами: рядок, змінений руками в обхід екрана (0, 100000), не вимикає
    // межу і не робить її безкінечною.
    private int Clamp(int minutes)
        => Math.Clamp(minutes, MinimumMinutes, SessionSettings.MaxIdleTimeoutMinutes);

    private static int ReadMinimum(IConfiguration configuration)
    {
        var configured = configuration.GetValue(MinimumMinutesKey, SessionSettings.MinIdleTimeoutMinutes);
        return configured is >= 1 and <= SessionSettings.MinIdleTimeoutMinutes
            ? configured
            : SessionSettings.MinIdleTimeoutMinutes;
    }
}
