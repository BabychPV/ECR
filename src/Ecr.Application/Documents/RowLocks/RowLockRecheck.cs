using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents.RowLocks;

/// <summary>
/// ✎ B75 (F13 пункт 4): блокування рядків — повторно ПІД замком аркуша, у транзакції запису.
/// </summary>
/// <remarks>
/// ⛔ Рішення про запис (<c>AccessDecisionService</c>) приймається ПОЗА транзакцією. Блокування, створене між ним і
/// записом, інакше пропустило б правку. Тому записувачі (PATCH, книга, колір, створення й видалення рядка) після
/// спільного замка аркуша × періоду перечитують блокування тут, а <c>POST …/row-locks</c> бере той самий замок
/// ВИНЯТКОВО: блокування або зафіксоване до замка записувача (і тоді видно тут), або чекає, доки запис закомітиться.
/// <para>
/// ⚠ Перечитування — лише коли замок бачив незняті блокування документа за період
/// (<see cref="ISheetEditGate.RowLocksSeen"/>, тим самим запитом, що й стан аркуша): документ без блокувань не платить
/// жодного звернення. Інтеграція й симуляція — як у рішенні (Q2; симуляцію відхилено раніше).
/// </para>
/// </remarks>
public static class RowLockRecheck
{
    /// <summary>Рядки (копія, аркуш, таблиця, ключ), заблоковані ІНШИМИ людьми; порожньо — перевіряти нічого.</summary>
    /// <param name="gate">Замок аркуша, уже взятий цією транзакцією за <paramref name="periodKey"/>.</param>
    /// <param name="store">Сховище блокувань; <c>null</c> — прямі конструктори тестів (перевірки немає).</param>
    /// <param name="profile">Хто пише.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період екземпляра.</param>
    /// <param name="utcNow">Годинник записувача.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlySet<RowLockKey>> LockedAsync(
        ISheetEditGate gate, IRowLockStore? store, AccessProfile profile, long documentId, int periodKey,
        DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(profile);

        if (store is null || profile.IsIntegrationWriter || profile.IsSimulation || !gate.RowLocksSeen(documentId, periodKey))
        {
            return Empty;
        }

        var locks = await store.LockedByOthersAsync(documentId, periodKey, profile.UserId, utcNow, ct).ConfigureAwait(false);
        return locks.Count == 0 ? Empty : locks.ToHashSet();
    }

    /// <summary>Відмова, якщо хоч один із рядків заблоковано: та сама форма, що й відмова рішення про комірки.</summary>
    /// <param name="locked">Результат <see cref="LockedAsync"/>.</param>
    /// <param name="sheetCopyId">Копія аркуша екземпляра.</param>
    /// <param name="sheetCode">Код аркуша.</param>
    /// <param name="tableCode">Код таблиці.</param>
    /// <param name="rowKeys">Ключі рядків, які пишуться.</param>
    /// <param name="deniedCount">Скільки комірок/рядків відхилено (для <c>deniedCount</c>).</param>
    /// <exception cref="AccessDeniedException"><c>ECR-ACCS-0403</c>, <c>deniedCells</c>, <c>reason = RowLocked</c>.</exception>
    public static void ThrowIfLocked(
        IReadOnlySet<RowLockKey> locked, long? sheetCopyId, string? sheetCode, string tableCode,
        IEnumerable<string> rowKeys, int deniedCount)
    {
        ArgumentNullException.ThrowIfNull(locked);
        ArgumentNullException.ThrowIfNull(rowKeys);

        if (!AnyLocked(locked, sheetCopyId, sheetCode, tableCode, rowKeys))
        {
            return;
        }

        throw new AccessDeniedException(
            "ECR-ACCS-0403",
            $"Заборонених комірок у батчі: {deniedCount}. Причина першої: {EditDenyReason.RowLocked}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.deniedCells",
                ["deniedCount"] = deniedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reason"] = nameof(EditDenyReason.RowLocked),
            });
    }

    /// <summary>Чи заблоковано хоч один із рядків таблиці (аркуш невідомий — <c>null</c> — збігається з будь-яким: fail-closed).</summary>
    /// <param name="locked">Результат <see cref="LockedAsync"/>.</param>
    /// <param name="sheetCopyId">Копія аркуша.</param>
    /// <param name="sheetCode">Код аркуша.</param>
    /// <param name="tableCode">Код таблиці.</param>
    /// <param name="rowKeys">Ключі рядків.</param>
    public static bool AnyLocked(
        IReadOnlySet<RowLockKey> locked, long? sheetCopyId, string? sheetCode, string tableCode, IEnumerable<string> rowKeys)
    {
        ArgumentNullException.ThrowIfNull(locked);
        ArgumentNullException.ThrowIfNull(rowKeys);

        return locked.Count > 0 && rowKeys.Any(k => locked.Any(l =>
            l.SheetCopyId == sheetCopyId
            && (sheetCode is null || string.Equals(l.SheetCode, sheetCode, StringComparison.Ordinal))
            && string.Equals(l.TableCode, tableCode, StringComparison.Ordinal)
            && string.Equals(l.RowKey, k, StringComparison.Ordinal)));
    }

    private static readonly IReadOnlySet<RowLockKey> Empty =
        new HashSet<RowLockKey>();
}
