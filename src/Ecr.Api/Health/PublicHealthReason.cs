using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Коротка нейтральна причина стану для анонімного <c>/health/ready</c> (A2-11).
/// </summary>
/// <remarks>
/// ⛔ L1-11/Q-221: повний опис перевірки <c>db</c> несе RCSI-скрипт, імена
/// файлових груп, відбитки сертифікатів і ключі конфігурації — анонімно його не
/// віддаємо. Але <c>description: null</c> при <c>Degraded</c> лишав моніторинг
/// без причини (вона була видна лише в <c>/admin/health</c> з правом).
///
/// Тому перевірка кладе в <c>Data</c> під <see cref="DataKey"/> лише КОД
/// причини, а писар перекладає його у фіксовану фразу з
/// <see cref="Phrases"/> — але лише для кодів із <see cref="AnonymousReasons"/>
/// (за замовчуванням порожній: усе дає <see cref="Generic"/>, рішення R1),
/// а коди з <see cref="AdminOnly"/> (ключі сесій) — НІКОЛИ. Довільний текст із перевірки сюди не доходить за
/// побудовою: невідомий код дає загальну фразу, а не сам код. Ключ
/// <see cref="DataKey"/> службовий — писар не віддає його в жоден звіт.
///
/// ⚠ Фрази англійською й без каталогу: це сигнал для моніторингу/інсталятора,
/// а не текст інтерфейсу; людський локалізований опис — у <c>/health/db</c>.
/// </remarks>
public static class PublicHealthReason
{
    /// <summary>Службовий ключ <c>Data</c>, під яким перевірка кладе код причини.</summary>
    public const string DataKey = "publicReason";

    /// <summary>RCSI вимкнено.</summary>
    public const string RcsiDisabled = "rcsi-disabled";

    /// <summary>Бракує обов'язкових файлових груп.</summary>
    public const string FilegroupsMissing = "filegroups-missing";

    /// <summary>Ключі сесій не захищені сертифікатом.</summary>
    public const string SessionKeysUnprotected = "session-keys-unprotected";

    /// <summary>У кільці ключів є ключі під недоступним сертифікатом.</summary>
    public const string KeyCertificateUnavailable = "key-certificate-unavailable";

    /// <summary>Запас партицій нижче мінімуму.</summary>
    public const string PartitionsLow = "partitions-low";

    /// <summary>Обсяг сирих точок вище порога перегляду.</summary>
    public const string RawDataVolume = "raw-data-volume";

    /// <summary>База недоступна.</summary>
    public const string DatabaseUnavailable = "database-unavailable";

    /// <summary>Загальна фраза, коли причина не названа або невідома.</summary>
    public const string Generic = "details are available to administrators";

    /// <summary>Фраза на місці тексту винятку, який перевірка не перехопила сама.</summary>
    public const string CheckFailed = "check failed; details are in the service log";

    private static readonly Dictionary<string, string> Phrases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RcsiDisabled] = "RCSI is disabled",
            [FilegroupsMissing] = "required filegroups are missing",
            [SessionKeysUnprotected] = "session keys not protected",
            [KeyCertificateUnavailable] = "key ring certificate unavailable",
            [PartitionsLow] = "partitions running low",
            [RawDataVolume] = "raw data volume above review threshold",
            [DatabaseUnavailable] = "database unavailable",
        };

    /// <summary>
    /// ⛔ L1-11: коди, які НІКОЛИ не стають фразою для анонімного звіту — стан кільця ключів сесій
    /// (відкрите кільце чи недоступний сертифікат) безпека не називає навіть у «загальному» вигляді.
    /// Подробиця лишається в <c>/health/db</c> і <c>/admin/health</c> (за правом). Цей набір не залежить
    /// від <see cref="AnonymousReasons"/>: його не можна «ввімкнути» перемикачем R1.
    /// </summary>
    private static readonly HashSet<string> AdminOnly =
        new HashSet<string>(StringComparer.Ordinal) { SessionKeysUnprotected, KeyCertificateUnavailable };

    /// <summary>
    /// Рішення R1 (AUDIT-2026-10-09): які причини анонімний <c>/health/ready</c> може називати фразою.
    /// Варіант A (за замовчуванням, до рішення людини) — жодної, усе дає <see cref="Generic"/>.
    /// Варіант B — додати сюди <see cref="RcsiDisabled"/> і <see cref="FilegroupsMissing"/>
    /// (<see cref="AdminOnly"/> усе одно переможе). Змінюється одним рядком; тести кільця ключів від R1 не залежать,
    /// решта чекають <see cref="Generic"/> (варіант A) — за B їх оновити.
    /// </summary>
    private static readonly HashSet<string> AnonymousReasons =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Усі фрази, які може дати <see cref="Describe"/> (для сторожів витоку).</summary>
    public static IReadOnlyCollection<string> KnownDescriptions { get; } =
        [.. Phrases.Where(p => AnonymousReasons.Contains(p.Key) && !AdminOnly.Contains(p.Key)).Select(p => p.Value), Generic];

    /// <summary>
    /// Нейтральний опис для анонімного звіту: <c>null</c> для <c>Healthy</c>,
    /// інакше фраза з білого списку (<see cref="AnonymousReasons"/>) або <see cref="Generic"/>.
    /// </summary>
    public static string? Describe(HealthReportEntry entry)
    {
        if (entry.Status == HealthStatus.Healthy)
        {
            return null;
        }

        return entry.Data.TryGetValue(DataKey, out var code)
            && code is string text
            && !AdminOnly.Contains(text)
            && AnonymousReasons.Contains(text)
            && Phrases.TryGetValue(text, out var phrase)
                ? phrase
                : Generic;
    }
}
