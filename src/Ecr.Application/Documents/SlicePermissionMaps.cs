// src/Ecr.Application/Documents/SlicePermissionMaps.cs
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Компактні мапи зрізу (<c>TableSliceDto.Permissions</c> і <c>Confirmations</c>): що сіре і чому, і що дозволене лише з
/// підтвердженням (<c>ФВ-2.16</c>, <c>#43</c>). Ключ — <c>"{rowKey}:{columnCode}"</c>.
/// </summary>
/// <remarks>
/// ⚡ Перф зрізу: рішень — рядки × колонки (HSE401: 6 428 × 31 ≈ 200k). Доти на КОЖНЕ рішення будувався рядок ключа
/// (інтерполяція), перевірялась читабельність колонки й шукався її код, навіть коли ключ у відповідь не йшов. Тепер
/// колонка розв'язується один раз (читабельність, код, «обчислювана»), а ключ будується лише для записаного елемента.
///
/// ⛔ S6: колонка, яку читач не бачить (<c>canReadColumn</c> = <c>false</c>), не дає ключа ні в причинах,
/// ні в підтвердженнях — причина відмови розкривала б її код. Семантика — рівно попередній цикл у
/// <see cref="GetTableSliceHandler"/>; тотожність стереже <c>SlicePermissionMapsTests</c> (порівняння з еталонною
/// копією старого циклу на строкатих наборах).
/// </remarks>
public static class SlicePermissionMaps
{
    /// <summary>Будує мапи причин відмови і підтверджень.</summary>
    /// <param name="decisions">Рішення на комірки зрізу (<c>CanEditSliceAsync</c>).</param>
    /// <param name="rowKeyById">Живі рядки зрізу: <c>TableRow.Id</c> → <c>RowKey</c>.</param>
    /// <param name="canReadColumn">Чи бачить читач колонку (S6).</param>
    /// <param name="columnCodeById">Коди колонок таблиці: <c>ColumnDef.Id</c> → код.</param>
    /// <param name="computedColumns">
    /// Видимі обчислювані колонки (Formula/Calculated): <c>CalculatedCell</c> на них клієнт виводить сам із типу колонки.
    /// </param>
    public static (Dictionary<string, string> Permissions, Dictionary<string, string> Confirmations) Build(
        IReadOnlyDictionary<CellAddress, EditDecision> decisions,
        IReadOnlyDictionary<long, string> rowKeyById,
        Func<int, bool> canReadColumn,
        IReadOnlyDictionary<int, string> columnCodeById,
        IReadOnlySet<int> computedColumns)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(rowKeyById);
        ArgumentNullException.ThrowIfNull(canReadColumn);
        ArgumentNullException.ThrowIfNull(columnCodeById);
        ArgumentNullException.ThrowIfNull(computedColumns);

        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        var confirmations = new Dictionary<string, string>(StringComparer.Ordinal);

        // Колонка → (код, обчислювана); код null — колонка не дає ключів (не читається чи невідома).
        var slots = new Dictionary<int, (string? Code, bool Computed)>();
        var reasonNames = new Dictionary<EditDenyReason, string>();

        foreach (var (address, decision) in decisions)
        {
            if (!rowKeyById.TryGetValue(address.TableRowId, out var rowKey))
            {
                continue;
            }

            if (!slots.TryGetValue(address.ColumnDefId, out var slot))
            {
                // ⚠ S6: причина відмови на заборонену колонку теж розкривала б її код.
                slot = canReadColumn(address.ColumnDefId)
                       && columnCodeById.TryGetValue(address.ColumnDefId, out var found)
                    ? (found, computedColumns.Contains(address.ColumnDefId))
                    : (null, false);
                slots[address.ColumnDefId] = slot;
            }

            if (slot.Code is not { } code)
            {
                continue;
            }

            if (!decision.IsAllowed)
            {
                if (decision.Reason == EditDenyReason.CalculatedCell && slot.Computed)
                {
                    continue;
                }

                if (!reasonNames.TryGetValue(decision.Reason, out var reason))
                {
                    reasonNames[decision.Reason] = reason = decision.Reason.ToString();
                }

                permissions[$"{rowKey}:{code}"] = reason;
                continue;
            }

            // ⛔ До цього поля `RequiresConfirmation` ніхто не читав: `AllowWithConfirmation` і звичайний дозвіл були на
            // виході з обробника нерозрізненні (`#43`).
            if (decision.RequiresConfirmation)
            {
                confirmations[$"{rowKey}:{code}"] = decision.Detail ?? string.Empty;
            }
        }

        return (permissions, confirmations);
    }
}
