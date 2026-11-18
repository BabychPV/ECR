using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Рядок фіксованої таблиці. Ідентичність — <see cref="RowKey"/>;
/// <see cref="Ordinal"/> відповідає лише за порядок на екрані.
/// </summary>
/// <remarks>
/// Це головна відмінність від чинного рішення, де ідентичність рядка була
/// позиційною (<c>Attribute_0010</c> = «перший рядок діапазону»), і вставка
/// рядка в Excel робила всі раніше збережені дані неправильними.
/// </remarks>
public sealed class RowDef : Entity<int>
{
    private RowDef() { }

    public RowDef(int tableDefId, RowKey rowKey, int ordinal, LocalizedText label, RowKind rowKind)
    {
        TableDefId = tableDefId;
        RowKeyValue = rowKey.Value;
        Ordinal = ordinal;
        LabelL10n = label;
        RowKind = rowKind;
    }

    public int TableDefId { get; private set; }

    /// <summary>Стабільна бізнес-ідентичність (<c>"7001001"</c>).</summary>
    public string RowKeyValue { get; private set; } = null!;

    /// <summary>Порядок відображення. Змінюється після публікації — це презентаційна правка.</summary>
    public int Ordinal { get; private set; }

    public LocalizedText LabelL10n { get; private set; } = null!;
    public RowKind RowKind { get; private set; }
    public int? ParentRowDefId { get; private set; }
    public bool IsReadOnly { get; private set; }
    public int? StyleId { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    /// <summary>
    /// ✎ B175: значення комірок рядка за замовчуванням — JSON-об'єкт <c>{"&lt;код колонки&gt;":"&lt;текст&gt;"}</c>
    /// (<c>cfg.RowDef.CellDefaultsJson</c>); <c>null</c> — немає. Дзеркало <see cref="ColumnDef.DefaultValue"/> на
    /// рівні рядка: Lookup — КОД запису довідника; <c>""</c> — прибрати дефолт колонки в цьому рядку. Підставляється
    /// лише при ЧИТАННІ (D-283 Q10=A), ніколи не пишеться в комірки.
    /// </summary>
    public string? CellDefaultsJson
    {
        get => _cellDefaultsJson;
        private set
        {
            _cellDefaultsJson = value;
            _cellDefaults = null;
        }
    }

    private string? _cellDefaultsJson;
    private IReadOnlyDictionary<string, string>? _cellDefaults;

    /// <summary>
    /// Типізований доступ до <see cref="CellDefaultsJson"/>: код колонки → текст (порожньо — дефолтів немає).
    /// Непридатний JSON (рука в базі) читається як «немає» — показ і розрахунок не падають через конфігурацію.
    /// </summary>
    public IReadOnlyDictionary<string, string> CellDefaults => _cellDefaults ??= ParseCellDefaults(_cellDefaultsJson);

    /// <summary>
    /// ✎ B175: задає дефолти комірок рядка (<c>null</c>/порожньо — прибрати). Коди й значення перевіряє обробник
    /// (колонки цієї таблиці, тип, довідник) — сутність не бачить колонок таблиці.
    /// </summary>
    public void SetCellDefaults(IReadOnlyDictionary<string, string>? defaults)
    {
        if (defaults is null || defaults.Count == 0)
        {
            CellDefaultsJson = null;
            return;
        }

        // Детермінований порядок ключів: та сама мапа — той самий текст (аудит і порівняння клонів без шуму).
        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, value) in defaults)
        {
            ordered[code] = value ?? string.Empty;
        }

        CellDefaultsJson = System.Text.Json.JsonSerializer.Serialize(ordered);
    }

    private static IReadOnlyDictionary<string, string> ParseCellDefaults(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { Count: > 0 } map
                ? new Dictionary<string, string>(map, StringComparer.Ordinal)
                : Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return Empty;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Змінює порядок — презентаційна операція.</summary>
    public void Reorder(int ordinal) => Ordinal = ordinal;

    /// <summary>
    /// Змінює підпис рядка.
    /// </summary>
    /// <remarks>
    /// ⛔ До появи <c>PUT …/tables/{tableId}/rows/{code}</c> (<c>W5.2</c>)
    /// сеттера не існувало: рядок фіксованої таблиці заводили лише тести й
    /// офлайновий генератор. Той самий випадок, що й
    /// <see cref="SheetDef.Rename"/> у <c>W5.0</c>.
    ///
    /// ⚠ <c>LabelL10n</c> — поле презентаційного шару (<see
    /// cref="Services.ChangeClassifier.PresentationFields"/>).
    /// </remarks>
    public void Rename(LocalizedText label)
    {
        ArgumentNullException.ThrowIfNull(label);
        LabelL10n = label;
    }

    /// <summary>
    /// Заборона ручного вводу в рядок (наприклад, підсумковий рядок балансу).
    /// </summary>
    public void SetReadOnly(bool readOnly) => IsReadOnly = readOnly;

    /// <summary>
    /// Задає стиль рядка (<c>null</c> — стиль колонки без перекриття). Презентаційна операція за зразком
    /// <see cref="ColumnDef.SetPresentation"/>: <see cref="StyleId"/> — у
    /// <see cref="Services.ChangeClassifier.PresentationFields"/>, тож не потребує нової версії шаблону.
    /// Належність стилю версії перевіряє обробник (ключа на <c>cfg.StyleDef</c> немає).
    /// </summary>
    public void SetStyle(int? styleId) => StyleId = styleId;

    /// <summary>
    /// Прив'язує рядок до батьківського в ієрархії; <c>null</c> — корінь.
    /// </summary>
    /// <remarks>
    /// ⚠ Належність батька тій самій таблиці й відсутність циклу перевіряє
    /// ВИКЛИК (обробник): сеттер сутності не бачить графа версії, а без нього
    /// таку перевірку не зробити.
    /// </remarks>
    public void SetParent(int? parentRowDefId) => ParentRowDefId = parentRowDefId;

    /// <summary>
    /// Логічне видалення: фізично запис лишається, бо на нього посилаються
    /// комірки документів навіть у чернетці (ФВ-7.6) — той самий підхід, що
    /// й <see cref="SheetDef.SoftDelete"/>.
    /// </summary>
    public void SoftDelete(int userId, DateTime utcNow)
    {
        IsDeleted = true;
        DeletedAt = utcNow;
        DeletedByUserId = userId;
    }
}
