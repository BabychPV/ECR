using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Група заголовків колонок таблиці (багаторівнева «шапка»: «Викиди · Q1 2026» → «Січень» → колонки).
/// До 3 рівнів. Належить ТАБЛИЦІ; код унікальний у таблиці й стабільний при клонуванні версії.
/// </summary>
/// <remarks>
/// Презентаційний шар: правиться й в опублікованій версії (одним запитом «замінити все дерево»).
/// Значень не стосується: звіти й подання не змінюються.
/// </remarks>
public sealed class ColumnHeaderGroupDef : Entity<int>
{
    private ColumnHeaderGroupDef() { }

    public ColumnHeaderGroupDef(int tableDefId, EcrCode code, LocalizedText header, int ordinal, int? parentGroupId = null)
    {
        TableDefId = tableDefId;
        Code = code.Value;
        HeaderL10n = header;
        Ordinal = ordinal;
        ParentGroupId = parentGroupId;
    }

    /// <summary>Максимальна глибина дерева груп (рівнів груп над колонками).</summary>
    public const int MaxDepth = 3;

    public int TableDefId { get; private set; }

    /// <summary>Батьківська група тієї ж таблиці; <c>null</c> — корінь.</summary>
    public int? ParentGroupId { get; private set; }

    public string Code { get; private set; } = null!;
    public LocalizedText HeaderL10n { get; private set; } = null!;

    /// <summary>Порядок серед сусідів (однакового батька).</summary>
    public int Ordinal { get; private set; }

    /// <summary>Оновлює підпис, порядок і батька (зміна дерева «замінити все»).</summary>
    public void Update(LocalizedText header, int ordinal, int? parentGroupId)
    {
        ArgumentNullException.ThrowIfNull(header);
        HeaderL10n = header;
        Ordinal = ordinal;
        ParentGroupId = parentGroupId;
    }

    /// <summary>Перев'язка батька (клонування версії).</summary>
    public void SetParent(int? parentGroupId) => ParentGroupId = parentGroupId;
}
