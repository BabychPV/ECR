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
/// ⛔ Приватність (рішення координатора): для імені БЕЗ облікового запису (<see cref="LoginAuditEntry.UserId"/> = null) у
/// журнал йде НЕ сире ім'я, а маска (<see cref="ILoginNameMasker"/>: два перші символи, довжина, ключований хеш) — люди
/// іноді вводять пароль у поле імені, а цей журнал не можна ні виправити, ні прибрати. Сире ім'я лишається в
/// <c>sec.LoginAttempt</c>, яка чиститься за строком.
///
/// ⚠ Автор. Невдала спроба й блокування мають автором анонімного актора (-1): людина не ввійшла, і приписувати
/// відмову користувачеві, чий пароль вгадують, було б неправдою; користувач — у <c>TargetUserId</c>. Лише
/// <c>LoginSucceeded</c> має автором самого користувача.
///
/// ⚠ Невдалі спроби ОБМЕЖУЮТЬСЯ від флуду: однакова трійка «ім'я + причина + адреса» пишеться не більше
/// <see cref="MaxPerKeyPerWindow"/> разів на <see cref="DefaultWindow"/>, решта рахується. Лічильник пропущених не
/// губиться: його несе перший запис нового вікна того ж ключа, а якщо ключ більше не повертається — РАЗ НА ВІКНО
/// «підмітання» пише підсумкову подію <see cref="LoginAuditEventTypes.SuppressedSummary"/>. Ключів у пам'яті не більше
/// <see cref="MaxTrackedKeys"/>: поки таблицю не очищено від застарілих, нові ключі не пишуться, але раз на вікно в
/// журнал іде <see cref="LoginAuditEventTypes.AuditOverflow"/> з кількістю відкинутих. Успішний вхід і блокування запису
/// НЕ обмежуються: це рідкісні й завжди важливі події (самі входи вже обмежені <c>LoginRateLimiting</c> за адресою).
/// </remarks>
public sealed partial class LoginAuditJournal : ILoginAuditJournal
{
    /// <summary>Вікно, у якому однакова невдала спроба пишеться не більше <see cref="MaxPerKeyPerWindow"/> разів.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    /// <summary>Скільки однакових невдалих спроб (ім'я + причина + адреса) пишеться за вікно.</summary>
    public const int MaxPerKeyPerWindow = 20;

    /// <summary>Скільки ключів тримається в пам'яті.</summary>
    public const int MaxTrackedKeys = 10_000;

    private const int CorrelationIdLength = 64;
    private const int UserNameLength = 200;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ICorrelationIdAccessor _correlation;
    private readonly ILoginNameMasker _masker;
    private readonly ILogger<LoginAuditJournal> _logger;
    private readonly TimeSpan _window;
    private readonly int _maxPerWindow;
    private readonly int _maxKeys;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<FloodKey, Window> _windows = new();
    private readonly object _sweepGate = new();
    private DateTime _lastSweep;
    private DateTime _lastOverflowReport;
    private int _dropped;

    /// <summary>Службу з типовим вікном і межами.</summary>
    /// <param name="scopes">Фабрика scope: запис ідеться поза scope запиту.</param>
    /// <param name="correlation">Кореляція поточного запиту.</param>
    /// <param name="masker">Маскування імені без облікового запису.</param>
    /// <param name="clock">Годинник вікна (час запису бере сама подія).</param>
    /// <param name="logger">Куди йде збій запису.</param>
    public LoginAuditJournal(
        IServiceScopeFactory scopes, ICorrelationIdAccessor correlation, ILoginNameMasker masker, IClock clock,
        ILogger<LoginAuditJournal> logger)
        : this(scopes, correlation, masker, clock, logger, DefaultWindow, MaxPerKeyPerWindow, MaxTrackedKeys)
    {
    }

    /// <summary>Службу з явними вікном і межами (для тестів).</summary>
    /// <param name="scopes">Фабрика scope.</param>
    /// <param name="correlation">Кореляція запиту.</param>
    /// <param name="masker">Маскування імені.</param>
    /// <param name="clock">Годинник.</param>
    /// <param name="logger">Логер.</param>
    /// <param name="window">Вікно обмежувача.</param>
    /// <param name="maxPerWindow">Скільки однакових невдалих спроб пишеться за вікно.</param>
    /// <param name="maxKeys">Скільки ключів тримається в пам'яті.</param>
    public LoginAuditJournal(
        IServiceScopeFactory scopes, ICorrelationIdAccessor correlation, ILoginNameMasker masker, IClock clock,
        ILogger<LoginAuditJournal> logger, TimeSpan window, int maxPerWindow, int maxKeys)
    {
        _scopes = scopes;
        _correlation = correlation;
        _masker = masker;
        _clock = clock;
        _logger = logger;
        _window = window;
        _maxPerWindow = maxPerWindow;
        _maxKeys = maxKeys;
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
                var now = _clock.UtcNow;
                await SweepAsync(now, correlationId, ct).ConfigureAwait(false);

                var key = new FloodKey(userName.ToUpperInvariant(), entry.Reason ?? string.Empty, entry.IpAddress ?? string.Empty);
                var verdict = TryAdmit(key, now, out suppressed);
                if (verdict == Admission.Rejected)
                {
                    return;
                }

                if (verdict == Admission.Overflow)
                {
                    await ReportOverflowAsync(now, correlationId, ct).ConfigureAwait(false);
                    return;
                }

                admitted = key;
            }

            // ⛔ Маска замість сирого імені — для імені без облікового запису.
            LoginNameMask? mask = entry.UserId is null
                ? await _masker.MaskAsync(userName, ct).ConfigureAwait(false)
                : null;

            await WriteAsync(
                new SecurityEventRecord(
                    entry.At,
                    entry.EventType,
                    TargetUserId: entry.UserId,
                    TargetRoleId: null,
                    DetailsJson: DetailsOf(entry, userName, mask, suppressed),
                    // ⚠ Автор: лише успіх — користувач; відмова й блокування — анонімний актор (див. remarks).
                    ChangedByUserId: entry.EventType == LoginAuditEventTypes.Succeeded
                        ? entry.UserId ?? LoginAuditEventTypes.AnonymousActorId
                        : LoginAuditEventTypes.AnonymousActorId,
                    CorrelationId: correlationId),
                ct).ConfigureAwait(false);
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

    private async Task WriteAsync(SecurityEventRecord record, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WriteTimeout);
        using var scope = _scopes.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

        await audit.WriteIndependentSecurityEventAsync(record, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Раз на вікно: прибирає застарілі ключі й пише ОДНУ підсумкову подію про пропущені спроби ключів, що більше не
    /// повертаються (інакше їхній лічильник <c>suppressed</c> зникав би разом з вікном).
    /// </summary>
    private async Task SweepAsync(DateTime now, string? correlationId, CancellationToken ct)
    {
        lock (_sweepGate)
        {
            if (now - _lastSweep < _window)
            {
                return;
            }

            _lastSweep = now;
        }

        var total = 0;
        var keys = 0;
        foreach (var pair in _windows)
        {
            // ⚠ Старше ДВОХ вікон, а не одного: ключ, що повернувся в наступному вікні, сам виносить свій лічильник
            // першим записом (`suppressed`), і підмітання не має відбирати його в нього.
            if (pair.Value.TryTakeExpired(now, _window + _window, out var lost))
            {
                _windows.TryRemove(pair.Key, out _);
                if (lost > 0)
                {
                    total += lost;
                    keys++;
                }
            }
        }

        if (total > 0)
        {
            await WriteAsync(
                new SecurityEventRecord(
                    now, LoginAuditEventTypes.SuppressedSummary, TargetUserId: null, TargetRoleId: null,
                    DetailsJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["suppressed"] = total,
                        ["keys"] = keys,
                        ["windowSeconds"] = (int)_window.TotalSeconds,
                    }),
                    ChangedByUserId: LoginAuditEventTypes.AnonymousActorId, CorrelationId: correlationId),
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>Таблиця ключів повна: раз на вікно — подія з кількістю відкинутих спроб.</summary>
    private async Task ReportOverflowAsync(DateTime now, string? correlationId, CancellationToken ct)
    {
        int dropped;
        lock (_sweepGate)
        {
            _dropped++;
            if (now - _lastOverflowReport < _window)
            {
                return;
            }

            _lastOverflowReport = now;
            dropped = _dropped;
            _dropped = 0;
        }

        LogTrackerFull(_maxKeys);
        await WriteAsync(
            new SecurityEventRecord(
                now, LoginAuditEventTypes.AuditOverflow, TargetUserId: null, TargetRoleId: null,
                DetailsJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["limit"] = _maxKeys,
                    ["dropped"] = dropped,
                    ["windowSeconds"] = (int)_window.TotalSeconds,
                }),
                ChangedByUserId: LoginAuditEventTypes.AnonymousActorId, CorrelationId: correlationId),
            ct).ConfigureAwait(false);
    }

    private static string DetailsOf(LoginAuditEntry entry, string userName, LoginNameMask? mask, int suppressed)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (mask is null)
        {
            details["userName"] = userName;
        }
        else
        {
            details["userNamePrefix"] = mask.Prefix;
            details["userNameLength"] = mask.Length;
            if (mask.Hash.Length > 0)
            {
                details["userNameHash"] = mask.Hash;
            }
        }

        details["provider"] = entry.Provider.ToString();
        details["outcome"] = entry.EventType switch
        {
            LoginAuditEventTypes.Succeeded => "Succeeded",
            LoginAuditEventTypes.AccountLocked => "Locked",
            _ => "Failed",
        };

        if (entry.Reason is { Length: > 0 } reason)
        {
            details["reason"] = reason;
        }

        if (entry.IpAddress is { Length: > 0 } ip)
        {
            details["ip"] = ip;
        }

        // ⚠ Адреса сокета — лише коли відрізняється від адреси клієнта (за проксі: проксі й справжній клієнт).
        if (entry.SocketIp is { Length: > 0 } socket && !string.Equals(socket, entry.IpAddress, StringComparison.Ordinal))
        {
            details["socketIp"] = socket;
        }

        if (suppressed > 0)
        {
            details["suppressed"] = suppressed;
        }

        return JsonSerializer.Serialize(details);
    }

    private static string? Truncate(string? value, int length)
        => value is not null && value.Length > length ? value[..length] : value;

    private Admission TryAdmit(FloodKey key, DateTime now, out int suppressed)
    {
        suppressed = 0;

        if (!_windows.ContainsKey(key) && _windows.Count >= _maxKeys)
        {
            // Підмітання раз на вікно вже було; тут лише дочищаємо застарілі без підсумкової події (з нульовим
            // лічильником) — повне й після цього означає перевантаження.
            foreach (var pair in _windows)
            {
                if (pair.Value.TryTakeExpired(now, _window, out var lost))
                {
                    if (lost == 0)
                    {
                        _windows.TryRemove(pair.Key, out _);
                    }
                    else
                    {
                        pair.Value.Keep(lost); // лічильник виносить найближче підмітання, а не губиться тут
                    }
                }
            }

            if (_windows.Count >= _maxKeys)
            {
                return Admission.Overflow;
            }
        }

        return _windows.GetOrAdd(key, static _ => new Window()).TryOpen(now, _window, _maxPerWindow, out suppressed)
            ? Admission.Admitted
            : Admission.Rejected;
    }

    private void Release(FloodKey key, int suppressed)
    {
        if (_windows.TryGetValue(key, out var window))
        {
            window.Return(suppressed);
        }
    }

    private enum Admission
    {
        Admitted,
        Rejected,
        Overflow,
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

        /// <summary>Повертає забраний лічильник пропущених (його виносить підмітання).</summary>
        public void Keep(int suppressed)
        {
            lock (_gate)
            {
                _suppressed += suppressed;
            }
        }

        /// <summary>Вікно закінчилося: забирає лічильник пропущених (він більше нікому не належить).</summary>
        public bool TryTakeExpired(DateTime now, TimeSpan window, out int lost)
        {
            lock (_gate)
            {
                if (_openedAt is { } opened && now - opened < window)
                {
                    lost = 0;
                    return false;
                }

                lost = _suppressed;
                _suppressed = 0;
                return true;
            }
        }
    }
}
