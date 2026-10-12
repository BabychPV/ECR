// src/Ecr.Application/Documents/Dto/PatchCellStylesRequest.cs
using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Користувацьке оформлення комірок (заливка, колір тексту, жирність) —
/// <c>PATCH /api/v1/documents/{documentId}/tables/{tableInstanceId}/cell-styles</c>.
/// </summary>
/// <remarks>
/// ⚠ Це НЕ дані: колір не потрапляє в <c>CellValue</c>, не міняє
/// <c>RowVersion</c> рядка, не вмикає перерахунок і не пише в
/// <c>aud.CellChange</c> (там значення). Стиль живе в розрідженій таблиці
/// документа й періоду; шаблон версії його не несе.
///
/// Семантика поля в <see cref="CellStyleChange"/>: <c>null</c> — не чіпати,
/// <c>""</c> — зняти це поле, <c>#rrggbb</c> — задати. <see cref="Reset"/> —
/// зняти ВСЕ оформлення з перелічених комірок (поля змін тоді ігноруються).
/// </remarks>
/// <param name="PeriodKey">Період екземпляра таблиці (звіряється з періодом екземпляра).</param>
/// <param name="Items">Комірки й нове оформлення; одна комірка — один раз.</param>
/// <param name="Reset"><c>true</c> — зняти все оформлення з перелічених комірок.</param>
public sealed record PatchCellStylesRequest(
    int PeriodKey,
    IReadOnlyList<CellStyleChange> Items,
    bool Reset = false)
{
    /// <summary>Стеля комірок на один запит (виділення клієнт розгортає в N комірок).</summary>
    public const int MaxItems = 2000;

    private static readonly Regex HexColor = new(
        "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Чи рядок — колір у формі <c>#rrggbb</c> (без прозорості).</summary>
    public static bool IsHexColor(string? value) => value is not null && HexColor.IsMatch(value);

    /// <summary>
    /// Перевіряє форму запиту ДО будь-якої роботи з базою.
    /// </summary>
    /// <exception cref="BusinessRuleException"><c>ECR-CELL-4224</c>.</exception>
    public void EnsureValid()
    {
        var items = Items ?? [];

        if (items.Count > MaxItems)
        {
            throw Refusal(
                $"Запит несе {items.Count} комірок; стеля — {MaxItems}.",
                "err.ECR-CELL-4224.tooManyItems",
                ("count", items.Count.ToString(CultureInfo.InvariantCulture)),
                ("max", MaxItems.ToString(CultureInfo.InvariantCulture)));
        }

        var seen = new HashSet<(string, string)>();
        foreach (var item in items)
        {
            if (item is null
                || string.IsNullOrWhiteSpace(item.RowKey)
                || string.IsNullOrWhiteSpace(item.ColumnCode))
            {
                throw Refusal(
                    "Комірка без rowKey або columnCode.",
                    "err.ECR-CELL-4224.unknownCell",
                    ("cellCount", "1"));
            }

            if (!seen.Add((item.RowKey, item.ColumnCode)))
            {
                throw Refusal(
                    $"Комірка «{item.RowKey}:{item.ColumnCode}» повторюється в запиті.",
                    "err.ECR-CELL-4224.duplicateCell",
                    ("rowKey", item.RowKey),
                    ("columnCode", item.ColumnCode));
            }

            if (Reset)
            {
                continue;
            }

            foreach (var color in new[] { item.BackgroundHex, item.ForegroundHex })
            {
                // "" — зняти поле; null — не чіпати; решта — лише #rrggbb.
                if (color is not null && color.Length > 0 && !IsHexColor(color))
                {
                    throw Refusal(
                        $"Колір комірки «{item.RowKey}:{item.ColumnCode}» не у формі #rrggbb.",
                        "err.ECR-CELL-4224.invalidColor",
                        ("rowKey", item.RowKey),
                        ("columnCode", item.ColumnCode));
                }
            }

            if (item.BackgroundHex is null && item.ForegroundHex is null && item.IsBold is null)
            {
                throw Refusal(
                    $"Для комірки «{item.RowKey}:{item.ColumnCode}» не задано жодної зміни.",
                    "err.ECR-CELL-4224.emptyChange",
                    ("rowKey", item.RowKey),
                    ("columnCode", item.ColumnCode));
            }
        }
    }

    private static BusinessRuleException Refusal(
        string message, string messageKey, params (string Name, string Value)[] details)
    {
        // ⚠ Значення — рядками: шаблон каталогу підставляє лише поля типу string.
        var map = new Dictionary<string, object?>(StringComparer.Ordinal) { ["messageKey"] = messageKey };
        foreach (var (name, value) in details)
        {
            map[name] = value;
        }

        return new BusinessRuleException(ErrorCodes.CellStyleInvalid, message, map);
    }
}

/// <summary>Зміна оформлення однієї комірки.</summary>
/// <param name="RowKey">Ключ рядка.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="BackgroundHex">Заливка <c>#rrggbb</c>; <c>null</c> — не чіпати; <c>""</c> — зняти.</param>
/// <param name="ForegroundHex">Колір тексту <c>#rrggbb</c>; <c>null</c> — не чіпати; <c>""</c> — зняти.</param>
/// <param name="IsBold">Жирність; <c>null</c> — не чіпати.</param>
public sealed record CellStyleChange(
    string RowKey,
    string ColumnCode,
    string? BackgroundHex = null,
    string? ForegroundHex = null,
    bool? IsBold = null);

/// <summary>Відповідь на <c>PATCH …/cell-styles</c>.</summary>
/// <param name="AppliedCells">Комірок, оформлення яких змінено (без no-op).</param>
/// <param name="ResetCells">Комірок, з яких знято все оформлення (рядок стилю видалено).</param>
public sealed record PatchCellStylesResponse(int AppliedCells, int ResetCells);
