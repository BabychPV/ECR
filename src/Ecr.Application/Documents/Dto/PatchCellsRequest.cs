// src/Ecr.Application/Documents/Dto/PatchCellsRequest.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Пакетна зміна комірок. Часткове застосування заборонене: конфлікт у
/// будь-якому рядку відхиляє весь батч (B04 §2.3).
/// </summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Ключ періоду.</param>
/// <param name="Origin">Джерело зміни: <c>UserEdit</c>, <c>Import</c>, <c>Recalculation</c>.</param>
/// <param name="Rows">Рядки зі змінами.</param>
/// <param name="Confirmed">
/// Людина підтвердила правку комірок, що вимагають підтвердження
/// (<c>ФВ-2.16</c>, <c>AllowWithConfirmation</c>). Без нього батч правки
/// людини (<c>UserEdit</c>), у якому є хоч одна така комірка, відхиляється
/// ЦІЛКОМ — <c>ECR-ACCS-0403</c> із причиною <c>ConfirmationRequired</c>.
/// Один прапорець на батч, а не перелік адрес: діалог на клієнті теж один на
/// пакет (вставка, протягування), а адреси батчу й так несе сам запит.
/// </param>
public sealed record PatchCellsRequest(
    long TableInstanceId,
    int PeriodKey,
    string Origin,
    IReadOnlyList<PatchRow> Rows,
    bool? Confirmed = null)
{
    /// <summary>Стеля комірок на один батч — нових і наявних рядків разом (`WR-11`).</summary>
    /// <remarks>
    /// ⚠ Найбільша законна вставка — 500 рядків × 60 колонок = 30 000 комірок;
    /// стеля дає запас над нею і водночас не пускає запит, який тримав би
    /// транзакцію запису й пам'ять сервера без меж.
    /// </remarks>
    public const int MaxCells = 50_000;

    /// <summary>
    /// Відхиляє батч понад <see cref="MaxCells"/> — ДО будь-якої роботи з базою.
    /// </summary>
    /// <remarks>
    /// ⛔ `WR-11`: до цього межі не було ніде — ні в запиті, ні в контролері,
    /// ні в обробнику. Кличуть і контролер (до першого читання), і обробник
    /// (його кличе ще й імпорт Excel напряму).
    ///
    /// ⚠ Числа — рядками: <c>ResolveGenericMessageAsync</c> підставляє в шаблон
    /// каталогу лише поля типу <c>string</c>.
    /// </remarks>
    /// <exception cref="Errors.BusinessRuleException"><c>ECR-REQ-0422</c>.</exception>
    public void EnsureWithinCellLimit()
    {
        // ⚠ `?.`: тіло з JSON може прийти без `rows` чи `cells` — це не
        // привід для `500` саме тут; порожній батч — no-op далі по шляху.
        var count = Rows?.Sum(row => row?.Cells?.Count ?? 0) ?? 0;
        if (count <= MaxCells)
        {
            return;
        }

        throw new Errors.BusinessRuleException(
            Ecr.Domain.Errors.ErrorCodes.RequestInvalid,
            $"Батч несе {count} комірок; стеля — {MaxCells}.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-REQ-0422.patchTooLarge",
                ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["max"] = MaxCells.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <summary>
    /// Відхиляє батч, де той самий <c>RowKey</c> стоїть двічі або колонка
    /// повторюється в межах рядка, — ДО будь-якої роботи з базою.
    /// </summary>
    /// <remarks>
    /// ⛔ L6-10: до цього такий батч доходив до <c>ToDictionary</c> (обов'язкові
    /// входи), до <c>MERGE</c> (8672 — та сама комірка двічі) чи до первинного
    /// ключа рядка і давав <c>500</c>. Тепер — керована <c>422</c> з переліком:
    /// котра з двох правок «правильна», сервер не вгадує.
    ///
    /// ⚠ Порівняння — <c>Ordinal</c>, як у мапах обробника (<c>RowKey</c>, код
    /// колонки): саме ці дублі й ламали його. Перелік — рядком через кому:
    /// шаблон каталогу підставляє лише поля типу <c>string</c>.
    /// </remarks>
    /// <exception cref="Errors.BusinessRuleException"><c>ECR-REQ-0422</c>.</exception>
    public void EnsureNoDuplicates()
    {
        if (Rows is null)
        {
            return;
        }

        var rowKeys = Rows
            .Where(row => row?.RowKey is not null)
            .GroupBy(row => row.RowKey, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (rowKeys.Count > 0)
        {
            throw new Errors.BusinessRuleException(
                Ecr.Domain.Errors.ErrorCodes.RequestInvalid,
                $"Рядки повторюються в батчі: {string.Join(", ", rowKeys)}.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-REQ-0422.patchDuplicateRowKey",
                    ["rowKeys"] = string.Join(", ", rowKeys),
                });
        }

        foreach (var row in Rows)
        {
            var columns = (row?.Cells ?? [])
                .Where(cell => cell?.ColumnCode is not null)
                .GroupBy(cell => cell.ColumnCode, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (columns.Count > 0)
            {
                throw new Errors.BusinessRuleException(
                    Ecr.Domain.Errors.ErrorCodes.RequestInvalid,
                    $"У рядку «{row!.RowKey}» колонки повторюються: {string.Join(", ", columns)}.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["messageKey"] = "err.ECR-REQ-0422.patchDuplicateCell",
                        ["rowKey"] = row.RowKey,
                        ["columnCodes"] = string.Join(", ", columns),
                    });
            }
        }
    }
}

/// <summary>
/// Рядок у пакетній зміні.
/// </summary>
/// <param name="RowKey">Ідентичність рядка.</param>
/// <param name="BaseVersion">
/// Версія рядка, від якої відштовхується клієнт (<c>rowversion</c> у Base64, як віддає зріз;
/// порівнюється з урахуванням регістру).
/// <c>null</c> означає <b>створення</b> нового рядка (R-B2).
/// </param>
/// <param name="Cells">Зміни комірок.</param>
public sealed record PatchRow(
    string RowKey,
    string? BaseVersion,
    IReadOnlyList<PatchCell> Cells);

/// <summary>
/// Зміна однієї комірки. Три різні операції (R-B4):
/// значення — записати; <c>Value = null</c> — стерти (рядок видаляється);
/// <c>IsEmpty = true</c> — явна порожнеча; поле відсутнє в запиті — не чіпати.
/// </summary>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="Value">Значення; <c>null</c> = стерти.</param>
/// <param name="IsEmpty">Явна порожнеча.</param>
public sealed record PatchCell(
    string ColumnCode,
    object? Value,
    bool IsEmpty = false);
