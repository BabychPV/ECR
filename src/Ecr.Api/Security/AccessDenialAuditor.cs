using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.Routing;

namespace Ecr.Api.Security;

/// <summary>
/// Журналює відмови в доступі (<c>403</c>) у <c>aud.SecurityEvent</c> (ФВ-5.24).
/// </summary>
/// <remarks>
/// ⛔ Ця служба не має права зашкодити відповіді. Викликач — обробник помилок:
/// якщо запис аудиту впаде і виняток поїде далі, клієнт замість <c>403</c>
/// отримає обірвану відповідь або <c>500</c>, тобто журнал зробить те, від чого
/// мав захищати. Тому <see cref="RecordAsync"/> ковтає ВСЕ (збій йде в лог), а
/// запис ідеться в ОКРЕМОМУ scope зі своїм <c>DbContext</c> і методом
/// <see cref="IAuditWriter.WriteIndependentSecurityEventAsync"/>: відмова — це
/// СПРОБА, факт незалежно від того, чим скінчилася транзакція запиту (C4).
///
/// ⚠ Що журналюється і що ні — рішення, а не збіг:
/// <list type="bullet">
/// <item><b>Лише <c>403</c></b> (<c>AccessDeniedException</c>, крім
/// <c>ECR-AUTH-0401</c>). <c>401</c> — анонім або прострочений сеанс: шум без
/// відомого автора (<c>ChangedByUserId</c> — <c>NOT NULL</c>), і його масово
/// дають самі клієнти, чий сеанс скінчився. Відмову «View as» зі стелі D-210
/// уже пише <c>StartSimulationHandler</c>, тож вона тут не дублюється.</item>
/// <item>Відмова запиту з чужого сайту (<c>CsrfOriginMiddleware</c>, L1-04) —
/// теж <c>AccessDeniedException</c>, відмов політик авторизації ASP.NET немає:
/// усі <c>403</c> застосунку — це <c>AccessDeniedException</c>.</item>
/// <item><b>Без PII</b>: метод, ШАБЛОН маршруту (не шлях: у шляху бувають
/// значення), код помилки, назва права/причина (лише ідентифікатор без
/// пробілів), числові ідентифікатори з маршруту. Ні query, ні тіла, ні
/// заголовків, ні імені/email користувача, ні IP.</item>
/// </list>
///
/// ⚠ Захист від флуду: одна й та сама четвірка «користувач + метод + шаблон
/// маршруту + код» пишеться не частіше за раз на <see cref="DefaultWindow"/>;
/// пропущені між записами відмови не губляться мовчки, а додаються числом
/// <c>suppressed</c> до наступного запису. Кількість ключів обмежена
/// (<see cref="MaxTrackedKeys"/>): після переповнення й очищення застарілих
/// нові ключі не пишуться зовсім — атака тисячею облікових записів не має
/// права роздути ні пам'ять, ні журнал.
/// </remarks>
public sealed partial class AccessDenialAuditor : IAccessDenialAuditor
{
    /// <summary><c>aud.SecurityEvent.EventType</c> відмови в доступі.</summary>
    public const string EventType = "AccessDenied";

    /// <summary>Вікно, у якому та сама відмова одного користувача пишеться раз.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    /// <summary>Скільки ключів (користувач × маршрут × код) тримається в пам'яті.</summary>
    internal const int MaxTrackedKeys = 10_000;

    /// <summary>Скільки чекати відповіді бази, перш ніж відмовитися від запису.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    /// <summary><c>aud.SecurityEvent.CorrelationId</c> — <c>nvarchar(64)</c>.</summary>
    private const int CorrelationIdLength = 64;

    /// <summary>Довжина шаблону маршруту в подробицях: захист від аномально довгого.</summary>
    private const int RouteLength = 200;

    private const string Unrouted = "(unrouted)";

    /// <summary>Ідентифікатор права чи причини: літери, цифри, крапка, підкреслення.</summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    /// <summary>Метод HTTP: лише літери, до 16 символів.</summary>
    [GeneratedRegex("^[A-Za-z]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex HttpMethodName();

    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;
    private readonly ILogger<AccessDenialAuditor> _logger;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<DenialKey, Window> _windows = new();

    /// <summary>Створює службу з вікном за замовчуванням.</summary>
    /// <param name="scopes">Фабрика scope: запис ідеться поза scope запиту.</param>
    /// <param name="clock">Годинник.</param>
    /// <param name="logger">Куди йде збій запису.</param>
    public AccessDenialAuditor(IServiceScopeFactory scopes, IClock clock, ILogger<AccessDenialAuditor> logger)
        : this(scopes, clock, logger, DefaultWindow)
    {
    }

    /// <summary>Створює службу з явним вікном (для тестів).</summary>
    /// <param name="scopes">Фабрика scope.</param>
    /// <param name="clock">Годинник.</param>
    /// <param name="logger">Логер.</param>
    /// <param name="window">Вікно обмежувача.</param>
    public AccessDenialAuditor(
        IServiceScopeFactory scopes, IClock clock, ILogger<AccessDenialAuditor> logger, TimeSpan window)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
        _window = window;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Відмову в доступі не записано в журнал безпеки. Code={Code}, CorrelationId={CorrelationId}")]
    private partial void LogWriteFailed(Exception exception, string code, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Обмежувач журналу відмов переповнено ({Limit} ключів): нові відмови не пишуться до очищення.")]
    private partial void LogTrackerFull(int limit);

    /// <inheritdoc />
    public async Task RecordAsync(HttpContext context, AccessDeniedException exception, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        var correlationId = CorrelationOf(context);
        DenialKey? admitted = null;
        var suppressed = 0;

        try
        {
            if (exception.ErrorCode == ErrorCodes.Unauthorized
                || context.RequestServices.GetService<ICurrentUser>()?.UserId is not { } userId)
            {
                return;
            }

            var method = HttpMethodName().IsMatch(context.Request.Method)
                ? context.Request.Method.ToUpperInvariant()
                : "OTHER";
            var route = RouteOf(context);
            var key = new DenialKey(userId, method, route, exception.ErrorCode);

            var now = _clock.UtcNow;
            if (!TryAdmit(key, now, out suppressed))
            {
                return;
            }

            admitted = key;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(WriteTimeout);
            using var scope = _scopes.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

            await audit.WriteIndependentSecurityEventAsync(
                new SecurityEventRecord(
                    now,
                    EventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    DetailsJson: DetailsOf(context, exception, method, route, suppressed),
                    ChangedByUserId: userId,
                    CorrelationId: correlationId),
                timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Причина — у ⛔ типу: збій журналу не має права дійти до відповіді.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (admitted is { } failed)
            {
                // Запису немає — вікно не спожито, щоб наступна відмова повторила спробу.
                Release(failed, suppressed);
            }

            LogWriteFailed(ex, exception.ErrorCode, correlationId ?? string.Empty);
        }
    }

    /// <summary>Чи можна писати цю відмову зараз; <paramref name="suppressed"/> — скільки пропущено від минулого запису.</summary>
    private bool TryAdmit(DenialKey key, DateTime now, out int suppressed)
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

        return _windows.GetOrAdd(key, static _ => new Window()).TryOpen(now, _window, out suppressed);
    }

    private void Release(DenialKey key, int suppressed)
    {
        if (_windows.TryGetValue(key, out var window))
        {
            window.Reopen(suppressed);
        }
    }

    private static string? CorrelationOf(HttpContext context)
    {
        var raw = context.Items.TryGetValue(Middleware.CorrelationIdMiddleware.ItemKey, out var value)
            ? value as string
            : null;

        return raw is { Length: > CorrelationIdLength } ? raw[..CorrelationIdLength] : raw;
    }

    /// <summary>Шаблон маршруту (<c>api/v1/projects/{projectId}/…</c>); шляху з підстановками тут немає.</summary>
    private static string RouteOf(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } raw })
        {
            return raw.Length > RouteLength ? raw[..RouteLength] : raw;
        }

        return Unrouted;
    }

    private static string DetailsOf(
        HttpContext context, AccessDeniedException exception, string method, string route, int suppressed)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = exception.ErrorCode,
            ["method"] = method,
            ["route"] = route,
        };

        // ⛔ Білий список подробиць винятку, а не «усе, що є»: там бувають готові речення
        // про конкретний рядок чи комірку. Право й причина відмови — лише якщо вони
        // виглядають ідентифікатором (`System.ViewHealth`, `NoGrant`), а не текстом.
        if (exception.Details is { } extra)
        {
            if (extra.TryGetValue("permission", out var permission)
                && permission is string p && Identifier().IsMatch(p))
            {
                details["permission"] = p;
            }

            if (extra.TryGetValue("reason", out var reason)
                && reason is string r && Identifier().IsMatch(r))
            {
                details["reason"] = r;
            }
        }

        // Ресурс — лише ЧИСЛОВІ значення маршруту з іменем `…Id`: рядкове значення
        // (код довідника, ключ рядка) може містити що завгодно.
        foreach (var (name, value) in context.Request.RouteValues)
        {
            if (!(name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
                || !long.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            if (string.Equals(name, "projectId", StringComparison.OrdinalIgnoreCase))
            {
                details["projectId"] = id;
            }
            else if (!details.ContainsKey("resourceId"))
            {
                details["resourceType"] = name;
                details["resourceId"] = id;
            }
        }

        if (suppressed > 0)
        {
            details["suppressed"] = suppressed;
        }

        return JsonSerializer.Serialize(details);
    }

    private readonly record struct DenialKey(int UserId, string Method, string Route, string Code);

    /// <summary>Стан вікна одного ключа.</summary>
    private sealed class Window
    {
        private readonly object _gate = new();
        private DateTime? _openedAt;
        private int _suppressed;

        public bool TryOpen(DateTime now, TimeSpan window, out int suppressed)
        {
            lock (_gate)
            {
                if (_openedAt is { } opened && now - opened < window)
                {
                    _suppressed++;
                    suppressed = 0;
                    return false;
                }

                suppressed = _suppressed;
                _suppressed = 0;
                _openedAt = now;
                return true;
            }
        }

        public void Reopen(int suppressed)
        {
            lock (_gate)
            {
                _openedAt = null;
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

/// <summary>Журнал відмов у доступі (ФВ-5.24). Виклик ніколи не кидає.</summary>
public interface IAccessDenialAuditor
{
    /// <summary>Записує відмову, якщо вона підлягає журналу.</summary>
    /// <param name="context">Запит, що закінчився <c>403</c>.</param>
    /// <param name="exception">Виняток відмови.</param>
    /// <param name="ct">Скасування; запис усе одно обмежений власним тайм-аутом.</param>
    public Task RecordAsync(HttpContext context, AccessDeniedException exception, CancellationToken ct);
}
