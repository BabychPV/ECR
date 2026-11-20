using System.Collections.Concurrent;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;

namespace Ecr.Api.Security;

/// <summary>
/// Пише успішні входи, невдалі спроби й блокування в <c>aud.SecurityEvent</c> (PSS-24, рішення людини 20.11 п. 7).
/// </summary>
/// <remarks>
/// ⛔ Журнал незмінний на рівні БД (<c>TR_SecurityEvent_Immutable</c>, <c>11-audit-tables.sql</c>) і живе не менше
/// мінімальної глибини аудиту (120 місяців, PSS-27): на відміну від <c>sec.LoginAttempt</c>, який лишається
/// оперативним і чиститься за строком <see cref="Ecr.Infrastructure.Security.LoginAttemptRetention"/>.
///
/// ⛔ Ця служба не має права зашкодити входові: запис іде в ОКРЕМОМУ scope зі своїм <c>DbContext</c> і з тайм-аутом, а
/// будь-який збій ковтається й іде в лог (<see cref="IAccessDenialAuditor"/> — той самий прийом). Спроба входу — факт,
/// що не залежить від того, чим скінчився запит.
///
/// ⛔ Подробиці — лише <c>userName</c>, <c>provider</c>, <c>outcome</c>, код причини, адреса й лічильник
/// <c>suppressed</c>. Ні пароля, ні його довжини, ні заголовків, ні тіла запиту. Час — <c>ChangedAt</c>, кореляція —
/// <c>CorrelationId</c> (той самий, що в <c>X-Correlation-Id</c>).
///
/// ⚠ Невдалі спроби ОБМЕЖУЮТЬСЯ від флуду: однакова трійка «ім'я + причина + адреса» пишеться не більше
/// <see cref="MaxPerKeyPerWindow"/> разів на <see cref="DefaultWindow"/>, решта рахується і додається числом
/// <c>suppressed</c> до наступного запису — а не губиться мовчки. Ключів у пам'яті не більше
/// <see cref="MaxTrackedKeys"/>: поки таблицю не очищено від застарілих, нові ключі не пишуться (тисяча різних імен не
/// має права роздути ні пам'ять, ні журнал). Успішний вхід і блокування запису НЕ обмежуються: це рідкісні й завжди
/// важливі події (самі входи вже обмежені <c>LoginRateLimiting</c> за адресою).
/// </remarks>
public sealed partial class LoginAuditJournal : ILoginAuditJournal
{
    /// <summary>Вікно, у якому однакова невдала спроба пишеться не більше <see cref="MaxPerKeyPerWindow"/> разів.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    /// <summary>Скільки однакових невдалих спроб (ім'я + причина + адреса) пишеться за вікно.</summary>
    public const int MaxPerKeyPerWindow = 20;

    /// <summary>Скільки ключів тримається в пам'яті.</summary>
    internal const int MaxTrackedKeys = 10_000;

    private const int CorrelationIdLength = 64;
    private const int UserNameLength = 200;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ICorrelationIdAccessor _correlation;
    private readonly ILogger<LoginAuditJournal> _logger;
    private readonly TimeSpan _window;
    private readonly int _maxPerWindow;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<FloodKey, Window> _windows = new();

    /// <summary>Службу з типовим вікном і межею.</summary>
    /// <param name="scopes">Фабрика scope: запис ідеться поза scope запиту.</param>
    /// <param name="correlation">Кореляція поточного запиту.</param>
    /// <param name="clock">Годинник вікна (час запису бере сам запис події).</param>
    /// <param name="logger">Куди йде збій запису.</param>
    public LoginAuditJournal(
        IServiceScopeFactory scopes, ICorrelationIdAccessor correlation, IClock clock, ILogger<LoginAuditJournal> logger)
        : this(scopes, correlation, clock, logger, DefaultWindow, MaxPerKeyPerWindow)
    {
    }

    /// <summary>Службу з явними вікном і межею (для тестів).</summary>
    /// <param name="scopes">Фабрика scope.</param>
    /// <param name="correlation">Кореляція запиту.</param>
    /// <param name="clock">Годинник.</param>
    /// <param name="logger">Логер.</param>
    /// <param name="window">Вікно обмежувача.</param>
    /// <param name="maxPerWindow">Скільки однакових невдалих спроб пишеться за вікно.</param>
    public LoginAuditJournal(
        IServiceScopeFactory scopes, ICorrelationIdAccessor correlation, IClock clock, ILogger<LoginAuditJournal> logger,
        TimeSpan window, int maxPerWindow)
    {
        _scopes = scopes;
        _correlation = correlation;
        _clock = clock;
        _logger = logger;
        _window = window;
        _maxPerWindow = maxPerWindow;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Подію входу не записано в журнал безпеки. EventType={EventType}, CorrelationId={CorrelationId}")]
    private partial void LogWriteFailed(Exception exception, string eventType, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Обмежувач журналу входів переповнено ({Limit} ключів): нові невдалі спроби не пишуться до очищення.")]
    private partial void LogTrackerFull(int limit);

    /// <inheritdoc />
    public async Task RecordAsync(LoginAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var correlationId = Truncate(_correlation.CorrelationId, CorrelationIdLength);
        FloodKey? admitted = null;
        var suppressed = 0;

        try
        {
            var userName = Truncate(entry.UserName, UserNameLength) ?? string.Empty;

            if (entry.EventType == LoginAuditEventTypes.Failed)
            {
                var key = new FloodKey(userName.ToUpperInvariant(), entry.Reason ?? string.Empty, entry.IpAddress ?? string.Empty);
                if (!TryAdmit(key, _clock.UtcNow, out suppressed))
                {
                    return;
                }

                admitted = key;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WriteTimeout);
            using var scope = _scopes.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

            await audit.WriteIndependentSecurityEventAsync(
                new SecurityEventRecord(
                    entry.At,
                    entry.EventType,
                    TargetUserId: entry.UserId,
                    TargetRoleId: null,
                    DetailsJson: DetailsOf(entry, userName, suppressed),
                    ChangedByUserId: entry.UserId ?? LoginAuditEventTypes.AnonymousActorId,
                    CorrelationId: correlationId),
                timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Причина — у ⛔ типу: збій журналу не має права дійти до відповіді входу.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (admitted is { } failed)
            {
                // Запису немає — місце у вікні не спожито, щоб наступна спроба повторила запис.
                Release(failed, suppressed);
            }

            LogWriteFailed(ex, entry.EventType, correlationId ?? string.Empty);
        }
    }

    private static string DetailsOf(LoginAuditEntry entry, string userName, int suppressed)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["userName"] = userName,
            ["provider"] = entry.Provider.ToString(),
            ["outcome"] = entry.EventType switch
            {
                LoginAuditEventTypes.Succeeded => "Succeeded",
                LoginAuditEventTypes.AccountLocked => "Locked",
                _ => "Failed",
            },
        };

        if (entry.Reason is { Length: > 0 } reason)
        {
            details["reason"] = reason;
        }

        if (entry.IpAddress is { Length: > 0 } ip)
        {
            details["ip"] = ip;
        }

        if (suppressed > 0)
        {
            details["suppressed"] = suppressed;
        }

        return JsonSerializer.Serialize(details);
    }

    private static string? Truncate(string? value, int length)
        => value is not null && value.Length > length ? value[..length] : value;

    private bool TryAdmit(FloodKey key, DateTime now, out int suppressed)
    {
        suppressed = 0;

        if (!_windows.ContainsKey(key) && _windows.Count >= MaxTrackedKeys)
        {
            foreach (var pair in _windows)
            {
                if (pair.Value.IsExpired(now, _window))
                {
                    _windows.TryRemove(pair.Key, out _);
                }
            }

            if (_windows.Count >= MaxTrackedKeys)
            {
                LogTrackerFull(MaxTrackedKeys);
                return false;
            }
        }

        return _windows.GetOrAdd(key, static _ => new Window()).TryOpen(now, _window, _maxPerWindow, out suppressed);
    }

    private void Release(FloodKey key, int suppressed)
    {
        if (_windows.TryGetValue(key, out var window))
        {
            window.Return(suppressed);
        }
    }

    private readonly record struct FloodKey(string UserName, string Reason, string Ip);

    /// <summary>Стан вікна одного ключа: скільки записано й скільки пропущено.</summary>
    private sealed class Window
    {
        private readonly object _gate = new();
        private DateTime? _openedAt;
        private int _written;
        private int _suppressed;

        public bool TryOpen(DateTime now, TimeSpan window, int max, out int suppressed)
        {
            lock (_gate)
            {
                suppressed = 0;

                if (_openedAt is not { } opened || now - opened >= window)
                {
                    _openedAt = now;
                    _written = 0;
                }

                if (_written >= max)
                {
                    _suppressed++;
                    return false;
                }

                _written++;
                suppressed = _suppressed;
                _suppressed = 0;
                return true;
            }
        }

        public void Return(int suppressed)
        {
            lock (_gate)
            {
                if (_written > 0)
                {
                    _written--;
                }

                _suppressed += suppressed;
            }
        }

        public bool IsExpired(DateTime now, TimeSpan window)
        {
            lock (_gate)
            {
                return _openedAt is not { } opened || now - opened >= window;
            }
        }
    }
}
