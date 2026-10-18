// src/Ecr.Domain/Entities/Configuration/PermitSheetBinding.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Прив'язка таблиці аркуша шаблону до таблиці типу дозволу (PM-02): які колонки
/// аркуша є ключем рядка, лімітом чи довідковими й як обирається дозвіл.
/// </summary>
/// <remarks>
/// <para>
/// Одна таблиця аркуша в одній версії шаблону прив'язується не більше ніж до одного
/// дозволу (<c>UQ_PermitSheetBinding_Sheet</c>).
/// </para>
/// <para>
/// <see cref="HeaderFieldDefId"/> — поле шапки версії шаблону (<see cref="HeaderFieldDef"/>,
/// <c>cfg.HeaderFieldDef</c>): шапка в моделі живе на рівні версії, тож це і є правильний
/// FK. Потрібне рівно для <see cref="PermitSelectionRule.FromHeaderLookup"/>.
/// </para>
/// </remarks>
public sealed class PermitSheetBinding : Entity<int>
{
    private readonly List<PermitSheetBindingColumn> _columns = [];

    private PermitSheetBinding() { }

    /// <summary>Створює активну прив'язку без колонок.</summary>
    /// <param name="code">Код прив'язки; унікальний, не змінюється.</param>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="sheetTableDefId">Таблиця аркуша.</param>
    /// <param name="permitTypeDefId">Тип дозволу.</param>
    /// <param name="permitTypeTableId">Таблиця типу дозволу.</param>
    /// <param name="mode">Режим прив'язки.</param>
    /// <param name="selectionRule">Правило вибору дозволу.</param>
    /// <param name="headerFieldDefId">Поле шапки (обов'язкове для <see cref="PermitSelectionRule.FromHeaderLookup"/>).</param>
    /// <exception cref="ArgumentException">Правило вибору не узгоджене з полем шапки.</exception>
    public PermitSheetBinding(
        EcrCode code,
        int templateVersionId,
        int sheetTableDefId,
        int permitTypeDefId,
        int permitTypeTableId,
        PermitBindingMode mode,
        PermitSelectionRule selectionRule,
        int? headerFieldDefId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(templateVersionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sheetTableDefId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitTypeDefId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitTypeTableId);

        Code = code.Value;
        TemplateVersionId = templateVersionId;
        SheetTableDefId = sheetTableDefId;
        PermitTypeDefId = permitTypeDefId;
        PermitTypeTableId = permitTypeTableId;
        IsActive = true;
        Reconfigure(mode, selectionRule, headerFieldDefId);
    }

    /// <summary>Код прив'язки.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Версія шаблону.</summary>
    public int TemplateVersionId { get; private set; }

    /// <summary>Таблиця аркуша.</summary>
    public int SheetTableDefId { get; private set; }

    /// <summary>Тип дозволу.</summary>
    public int PermitTypeDefId { get; private set; }

    /// <summary>Таблиця типу дозволу.</summary>
    public int PermitTypeTableId { get; private set; }

    /// <summary>Режим прив'язки.</summary>
    public PermitBindingMode Mode { get; private set; }

    /// <summary>Правило вибору дозволу.</summary>
    public PermitSelectionRule SelectionRule { get; private set; }

    /// <summary>Поле шапки версії шаблону, з якого береться дозвіл.</summary>
    public int? HeaderFieldDefId { get; private set; }

    /// <summary>Чи діє прив'язка.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Токен оптимістичної конкуренції (<c>If-Match</c>); ставить база.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Колонки аркуша з їхніми ролями; змінюються лише через агрегат.</summary>
    public IReadOnlyList<PermitSheetBindingColumn> Columns => _columns;

    /// <summary>Змінює режим і правило вибору; таблиці й тип лишаються.</summary>
    /// <exception cref="ArgumentException">Правило вибору не узгоджене з полем шапки.</exception>
    public void Reconfigure(PermitBindingMode mode, PermitSelectionRule selectionRule, int? headerFieldDefId)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Невідомий режим прив'язки.");
        }

        if (!Enum.IsDefined(selectionRule))
        {
            throw new ArgumentOutOfRangeException(nameof(selectionRule), selectionRule, "Невідоме правило вибору.");
        }

        // Правило «з шапки» без поля шапки не має звідки брати дозвіл; поле шапки
        // при іншому правилі нікого не читає й лише вводить в оману. Те саме
        // тримає CK_PermitBinding_Header.
        if ((selectionRule == PermitSelectionRule.FromHeaderLookup) != (headerFieldDefId is not null))
        {
            throw new ArgumentException(
                "Поле шапки обов'язкове для правила FromHeaderLookup і заборонене для решти.",
                nameof(headerFieldDefId));
        }

        Mode = mode;
        SelectionRule = selectionRule;
        HeaderFieldDefId = headerFieldDefId;
    }

    /// <summary>Вмикає чи вимикає прив'язку.</summary>
    public void SetActive(bool isActive) => IsActive = isActive;

    /// <summary>Додає колонку аркуша з роллю.</summary>
    /// <param name="column">Колонка; код поля й колонка аркуша не повторюються в прив'язці.</param>
    /// <exception cref="InvalidOperationException">Код поля чи колонка аркуша вже є в прив'язці.</exception>
    public void AddColumn(PermitSheetBindingColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        // ⚠ Дубль ловиться тут, а не лише унікальним індексом: індекс віддав би
        // помилку провайдера на SaveChanges, без назви поля в тексті.
        if (_columns.Exists(c => string.Equals(c.PermitFieldCode, column.PermitFieldCode, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Поле дозволу «{column.PermitFieldCode}» у прив'язці «{Code}» уже має колонку.");
        }

        if (_columns.Exists(c => c.SheetColumnDefId == column.SheetColumnDefId))
        {
            throw new InvalidOperationException(
                $"Колонка аркуша {column.SheetColumnDefId} у прив'язці «{Code}» уже використана.");
        }

        _columns.Add(column);
    }
}
