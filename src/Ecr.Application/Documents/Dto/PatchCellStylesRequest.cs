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
/// <param name="Items">Комірки й нове оформлення; одна комірка — один раз. Може бути порожнім, якщо задано <paramref name="Scope"/>.</param>
/// <param name="Reset"><c>true</c> — зняти все оформлення з перелічених у <paramref name="Items"/> комірок.</param>
/// <param name="Scope">
/// Оформлення ОБЛАСТЮ «рядки × колонки» одним запитом (фарбувати рядок/таблицю миттєво).
/// <c>null</c> — лише <paramref name="Items"/>. Обидва можна передати разом: на спільній комірці
/// items сильніші за scope.
/// </param>
public sealed record PatchCellStylesRequest(
    int PeriodKey,
    IReadOnlyList<CellStyleChange>? Items = null,
    bool Reset = false,
    CellStyleScope? Scope = null)
{
    /// <summary>Стеля комірок на один запит (виділення клієнт розгортає в N комірок).</summary>
    public const int MaxItems = 2000;

    /// <summary>
    /// Стеля розгорнутих комірок <see cref="Scope"/> на один запит. Збережена, прав і запису —
    /// одна транзакція, чанками по 1000.
    /// </summary>
    public const int ScopeMaxCells = 50_000;

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

        Scope?.EnsureValid();

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

    internal static BusinessRuleException Refusal(
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

/// <summary>
/// Область оформлення «рядки × колонки»: сервер розгортає її на живі рядки таблиці й колонки,
/// які викликач бачить; права — ті самі, що в <c>items</c>.
/// </summary>
/// <remarks>
/// Рядки: рівно одне з <see cref="RowKeys"/> (непорожній) / <see cref="AllRows"/> = <c>true</c>.
/// Колонки: рівно одне з <see cref="ColumnCodes"/> (непорожній) / <see cref="AllColumns"/> = <c>true</c>.
/// Зміни — як у <see cref="CellStyleChange"/> (<c>null</c> — не чіпати, <c>""</c> — зняти, <c>#rrggbb</c> — задати);
/// <see cref="Reset"/> = <c>true</c> — зняти ВСЕ оформлення з області (поля змін ігноруються).
/// Розгортання не більше <see cref="PatchCellStylesRequest.ScopeMaxCells"/> комірок.
/// </remarks>
/// <param name="RowKeys">Ключі рядків; рядка немає в зрізі — <c>unknownCell</c>.</param>
/// <param name="AllRows"><c>true</c> — усі живі рядки екземпляра.</param>
/// <param name="ColumnCodes">Коди колонок; колонки немає чи вона прихована — <c>unknownCell</c>.</param>
/// <param name="AllColumns"><c>true</c> — усі колонки, які бачить викликач (приховані/заборонені не входять).</param>
/// <param name="BackgroundHex">Заливка <c>#rrggbb</c>; <c>null</c> — не чіпати; <c>""</c> — зняти.</param>
/// <param name="ForegroundHex">Колір тексту <c>#rrggbb</c>; <c>null</c> — не чіпати; <c>""</c> — зняти.</param>
/// <param name="IsBold">Жирність; <c>null</c> — не чіпати.</param>
/// <param name="Reset"><c>true</c> — зняти все оформлення з області.</param>
public sealed record CellStyleScope(
    IReadOnlyList<string>? RowKeys = null,
    bool AllRows = false,
    IReadOnlyList<string>? ColumnCodes = null,
    bool AllColumns = false,
    string? BackgroundHex = null,
    string? ForegroundHex = null,
    bool? IsBold = null,
    bool Reset = false)
{
    /// <summary>Форма області — до будь-якої роботи з базою.</summary>
    /// <exception cref="BusinessRuleException"><c>ECR-CELL-4224</c>.</exception>
    public void EnsureValid()
    {
        var rowKeys = RowKeys ?? [];
        var columnCodes = ColumnCodes ?? [];

        if (AllRows == (rowKeys.Count > 0))
        {
            throw PatchCellStylesRequest.Refusal(
                "scope: потрібно рівно одне з rowKeys / allRows.",
                "err.ECR-CELL-4224.scopeRows");
        }

        if (AllColumns == (columnCodes.Count > 0))
        {
            throw PatchCellStylesRequest.Refusal(
                "scope: потрібно рівно одне з columnCodes / allColumns.",
                "err.ECR-CELL-4224.scopeColumns");
        }

        // Верхня межа переліків: більше за стелю розгортання їх не буває (захист від гігантського тіла).
        if (rowKeys.Count > PatchCellStylesRequest.ScopeMaxCells || columnCodes.Count > PatchCellStylesRequest.ScopeMaxCells)
        {
            throw PatchCellStylesRequest.Refusal(
                $"scope: переліків понад {PatchCellStylesRequest.ScopeMaxCells} елементів.",
                "err.ECR-CELL-4224.tooManyCells",
                ("count", Math.Max(rowKeys.Count, columnCodes.Count).ToString(CultureInfo.InvariantCulture)),
                ("max", PatchCellStylesRequest.ScopeMaxCells.ToString(CultureInfo.InvariantCulture)));
        }

        if (rowKeys.Any(string.IsNullOrWhiteSpace) || columnCodes.Any(string.IsNullOrWhiteSpace))
        {
            throw PatchCellStylesRequest.Refusal(
                "scope: порожній rowKey або columnCode.",
                "err.ECR-CELL-4224.unknownCell",
                ("cellCount", "1"));
        }

        if (Reset)
        {
            return;
        }

        foreach (var color in new[] { BackgroundHex, ForegroundHex })
        {
            if (color is not null && color.Length > 0 && !PatchCellStylesRequest.IsHexColor(color))
            {
                throw PatchCellStylesRequest.Refusal(
                    "scope: колір не у формі #rrggbb.",
                    "err.ECR-CELL-4224.scopeInvalidColor");
            }
        }

        if (BackgroundHex is null && ForegroundHex is null && IsBold is null)
        {
            throw PatchCellStylesRequest.Refusal(
                "scope: не задано жодної зміни.",
                "err.ECR-CELL-4224.scopeEmptyChange");
        }
    }
}

/// <summary>Відповідь на <c>PATCH …/cell-styles</c>.</summary>
/// <param name="AppliedCells">Комірок, оформлення яких змінено (без no-op).</param>
/// <param name="ResetCells">Комірок, з яких знято все оформлення (рядок стилю видалено).</param>
/// <param name="DeniedCells">
/// Комірок області <c>scope</c>, відмовлених правами/станом (часткова відмова; решта застосована).
/// Для <c>items</c> відмова лишається повною (403). Колонки, яких викликач не бачить, не входять у лік.
/// </param>
/// <param name="DeniedByReason">
/// Причини відмови з лічильниками (не більше 10; без назв рядків і колонок), лише коли
/// <paramref name="DeniedCells"/> більше нуля.
/// </param>
/// <param name="SkippedCells">
/// Дозволених комірок без змін (оформлення вже таке саме; ідемпотентний повтор). Разом
/// <c>applied + reset + skipped + denied</c> = розгорнуто комірок.
/// </param>
public sealed record PatchCellStylesResponse(
    int AppliedCells,
    int ResetCells,
    int DeniedCells = 0,
    IReadOnlyDictionary<string, int>? DeniedByReason = null,
    int SkippedCells = 0);
