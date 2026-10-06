// src/Ecr.Application/Integration/SourceEvents/SourceEventCatalogHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration.SourceEvents;

/// <summary>Атрибут, який пробне читання подій має лишити (порожній перелік — усі).</summary>
/// <param name="Name">Ім'я атрибута з каталогу шаблону.</param>
/// <param name="Scope">Де лежить атрибут.</param>
public sealed record SourceEventProbeAttribute(string Name, SourceEventAttributeScope Scope);

/// <summary>Пробне читання подій (HSE301 A6).</summary>
/// <param name="Template">Шаблон подій із каталогу.</param>
/// <param name="FromUtc">Початок вікна; <c>null</c> — 30 днів назад.</param>
/// <param name="ToUtc">Кінець вікна; <c>null</c> — «зараз».</param>
/// <param name="Attributes">Атрибути; порожньо чи <c>null</c> — усі, що дало джерело.</param>
/// <param name="MaxEvents">Стеля подій; <c>null</c> — 20, не більше 100.</param>
public sealed record SourceEventProbeRequest(
    string? Template,
    DateTime? FromUtc,
    DateTime? ToUtc,
    IReadOnlyList<SourceEventProbeAttribute>? Attributes,
    int? MaxEvents);

/// <summary>Наслідок пробного читання подій.</summary>
/// <param name="FromUtc">Вікно, яке читали, початок.</param>
/// <param name="ToUtc">Вікно, яке читали, кінець.</param>
/// <param name="Events">Прочитані події.</param>
/// <param name="Truncated">Подій було більше за стелю проби.</param>
/// <param name="ErrorCode">Код часткової відмови адаптера; <c>null</c> — немає.</param>
public sealed record SourceEventProbeResult(
    DateTime FromUtc, DateTime ToUtc, IReadOnlyList<SourceEvent> Events, bool Truncated, string? ErrorCode);

/// <summary>Спільне читання подій джерела: адаптер, ліміт часу, нормалізація відмов.</summary>
internal static class SourceEventReading
{
    /// <summary>Виконує читання адаптера з обмеженням часу; відмову транспорту перетворює на «недоступне».</summary>
    public static async Task<T> RunAsync<T>(
        DataSource source,
        SourceCatalogPolicy policy,
        string timeoutKey,
        string unavailableKey,
        Func<CancellationToken, Task<T>> read,
        CancellationToken ct)
    {
        using var deadline = policy.StartDeadline();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        try
        {
            return await read(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Unavailable(source, policy, timeoutKey);
        }

        // ⚠ Та сама конвенція, що в каталозі й пробі шляху: неECR-відмова транспорту або відмова джерела —
        // «недоступне»; відмови ECR (не налаштовано запит, автентифікація) проходять як є.
        catch (Exception e) when (e is not EcrException and not OperationCanceledException
                                  || e is BusinessRuleException { ErrorCode: ErrorCodes.SourceUnavailable })
        {
            throw Unavailable(source, policy, unavailableKey);
        }
    }

    /// <summary>Адаптер транспорту джерела або відмова «недоступне».</summary>
    public static IExternalDataSource Adapter(
        IEnumerable<IExternalDataSource> adapters, DataSource source, SourceCatalogPolicy policy, string unavailableKey)
        => adapters.FirstOrDefault(a => a.Transport == source.Transport)
           ?? throw Unavailable(source, policy, unavailableKey);

    private static BusinessRuleException Unavailable(DataSource source, SourceCatalogPolicy policy, string messageKey)
        => new(
            ErrorCodes.SourceUnavailable,
            $"Джерело «{source.Code}» недоступне або не відповіло вчасно: події не прочитано.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
                ["code"] = source.Code,
                ["timeoutSeconds"] = (int)policy.Timeout.TotalSeconds,
            });
}

/// <summary>
/// Каталог шаблонів подій джерела й їхніх атрибутів — для конфігуратора мапінгу подій
/// (HSE301 A6, FEATURE-HSE301-VIEW §4.7.1). Право <c>Integration.View</c> або <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Нічого не пише. Без налаштованого запиту каталогу адаптер відмовляє <c>ECR-INT-0422</c>
/// (<c>.eventQueryNotConfigured</c>/<c>.queryKindNotSupported</c>): «не налаштовано» — стан, а не порожній список.
/// </remarks>
public sealed class ListEventTemplatesHandler(
    IDataSourceStore store,
    IEnumerable<IExternalDataSource> adapters,
    SourceCatalogPolicy policy,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Шаблони подій джерела <paramref name="id"/>.</summary>
    /// <param name="id">Джерело даних.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<SourceEventTemplate>> HandleAsync(int id, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAnyAsync(
                access, currentUser, [ListDataSourcesHandler.Permission, SaveDataSourceHandler.Permission], ct)
            .ConfigureAwait(false);

        var source = await ListDataSourcesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        var adapter = SourceEventReading.Adapter(adapters, source, policy, "err.ECR-INT-0503.catalogUnavailable");

        var templates = await SourceEventReading
            .RunAsync(
                source, policy, "err.ECR-INT-0503.catalogTimeout", "err.ECR-INT-0503.catalogUnavailable",
                token => adapter.DiscoverEventTemplatesAsync(source.Id, token), ct)
            .ConfigureAwait(false);

        return [.. templates.OrderBy(t => t.TemplateName, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>
/// Пробне читання подій шаблону за вікно — «що дасть запит» до налаштування мапінгу
/// (HSE301 A6). Право <c>Integration.Manage</c>, як у <see cref="ProbeSourcePathHandler"/>.
/// </summary>
/// <remarks>
/// ⛔ Нічого не зберігає: ні зв'язків, ні рядків, ні покриття — лише читання адаптером і відповідь. Стеля проби
/// (<see cref="MaxProbeEvents"/>) і вікно (<see cref="MaxWindowDays"/>) вужчі за синхронізацію: проба — це погляд,
/// а не другий канал вивантаження.
/// </remarks>
public sealed class ProbeSourceEventsHandler(
    IDataSourceStore store,
    IEnumerable<IExternalDataSource> adapters,
    SourceCatalogPolicy policy,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Стеля довжини імені шаблону чи атрибута.</summary>
    public const int MaxNameLength = 200;

    /// <summary>Типова кількість подій проби.</summary>
    public const int DefaultProbeEvents = 20;

    /// <summary>Максимальна кількість подій проби.</summary>
    public const int MaxProbeEvents = 100;

    /// <summary>Типове вікно проби назад від «зараз».</summary>
    public const int DefaultWindowDays = 30;

    /// <summary>Найширше вікно проби.</summary>
    public const int MaxWindowDays = 92;

    /// <summary>Читає події шаблону джерела <paramref name="id"/>.</summary>
    /// <param name="id">Джерело даних.</param>
    /// <param name="request">Що читати.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<SourceEventProbeResult> HandleAsync(int id, SourceEventProbeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var template = request.Template?.Trim();
        var to = ToUtc(request.ToUtc) ?? clock.UtcNow;
        var from = ToUtc(request.FromUtc) ?? to.AddDays(-DefaultWindowDays);
        var max = request.MaxEvents ?? DefaultProbeEvents;
        var attributes = request.Attributes ?? [];

        if (string.IsNullOrEmpty(template)
            || template.Length > MaxNameLength
            || from >= to
            || to - from > TimeSpan.FromDays(MaxWindowDays)
            || max is < 1 or > MaxProbeEvents
            || attributes.Any(a => a is null || string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > MaxNameLength || !Enum.IsDefined(a.Scope)))
        {
            throw ListDataSourcesHandler.Invalid(
                "err.ECR-REQ-0422.probeEventsInvalid",
                $"Проба подій: шаблон 1–{MaxNameLength} символів, вікно з початком до кінця (до {MaxWindowDays} днів), "
                + $"стеля подій 1–{MaxProbeEvents}.");
        }

        var source = await ListDataSourcesHandler.FindAsync(store, id, ct).ConfigureAwait(false);
        var adapter = SourceEventReading.Adapter(adapters, source, policy, "err.ECR-INT-0503.probeUnavailable");

        var query = new SourceEventQuery(
            source.Id,
            0,
            template,
            from,
            to,
            [.. attributes.Select(a => new SourceEventAttributeRef(a.Name.Trim(), a.Scope))],
            max);

        var read = await SourceEventReading
            .RunAsync(
                source, policy, "err.ECR-INT-0503.probeTimeout", "err.ECR-INT-0503.probeUnavailable",
                token => adapter.ReadEventsAsync(query, token), ct)
            .ConfigureAwait(false);

        return new SourceEventProbeResult(from, to, read.Events, read.Truncated, read.ErrorCode);
    }

    private static DateTime? ToUtc(DateTime? value) => value is { } v
        ? v.Kind switch
        {
            DateTimeKind.Utc => v,
            DateTimeKind.Local => v.ToUniversalTime(),
            _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        }
        : null;
}
