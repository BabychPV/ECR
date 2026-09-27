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
public sealed record PatchCellsRequest(
    long TableInstanceId,
    int PeriodKey,
    string Origin,
    IReadOnlyList<PatchRow> Rows)
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
}

/// <summary>
/// Рядок у пакетній зміні.
/// </summary>
/// <param name="RowKey">Ідентичність рядка.</param>
/// <param name="BaseVersion">
/// Версія рядка, від якої відштовхується клієнт (hex <c>rowversion</c>).
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
