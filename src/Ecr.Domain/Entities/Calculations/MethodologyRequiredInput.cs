// src/Ecr.Domain/Entities/Calculations/MethodologyRequiredInput.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Обов'язкова вхідна колонка методології — gate ПЕРЕД збереженням клітинки
/// (директива «обов'язкові вхідні колонки методології»).
/// </summary>
/// <remarks>
/// ⛔ НЕ розширення <see cref="Configuration.ColumnDef.IsRequired"/>. Той
/// прапорець каже, що колонка обов'язкова взагалі, незалежно від методології
/// — це інша вісь. Ця сутність каже: «для методології X колонка Y — вхід
/// формули, і без неї результат методології не має сенсу» — і та сама колонка
/// може бути обов'язковою для однієї методології таблиці й необов'язковою для
/// сусідньої, прив'язаної до тієї самої таблиці.
///
/// ⚠ Форма — за зразком <see cref="MethodologyRule"/>: окрема таблиця,
/// уникальність у межах версії (<c>(MethodologyVersionId, ColumnDefId)</c>),
/// заводиться і правиться лише через <see cref="MethodologyVersion"/>.
/// </remarks>
public sealed class MethodologyRequiredInput : Entity<int>
{
    private MethodologyRequiredInput() { }

    /// <summary>Заводить вимогу.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="columnDefId">Колонка документа, обов'язкова як вхід.</param>
    /// <param name="severity">Блокує чи лише попереджає збереження.</param>
    /// <param name="hint">
    /// Текст поверх типового шаблону «Колонка «X» обов'язкова для методології
    /// «Y»»; <c>null</c> — типового шаблону достатньо (ASK директиви).
    /// </param>
    public MethodologyRequiredInput(
        int methodologyVersionId, int columnDefId, RequiredInputSeverity severity, LocalizedText? hint)
    {
        MethodologyVersionId = methodologyVersionId;
        ColumnDefId = columnDefId;
        Severity = severity;
        HintL10n = hint;
    }

    /// <summary>Версія методології, якій належить вимога.</summary>
    public int MethodologyVersionId { get; private set; }

    /// <summary>Колонка документа, обов'язкова як вхід цієї методології.</summary>
    public int ColumnDefId { get; private set; }

    /// <summary>Блокує збереження чи лише попереджає.</summary>
    public RequiredInputSeverity Severity { get; private set; }

    /// <summary>Текст поверх типового шаблону; <c>null</c> — типового достатньо.</summary>
    public LocalizedText? HintL10n { get; private set; }

    /// <summary>Переписує критичність і підказку наявної вимоги.</summary>
    /// <param name="severity">Нова критичність.</param>
    /// <param name="hint">Новий текст поверх типового шаблону.</param>
    /// <remarks>
    /// ⛔ Метод <c>internal</c>: єдиний вхід — <see cref="MethodologyVersion.EditRequiredInput"/>,
    /// бо вимога не знає, опублікована її версія чи ні.
    /// </remarks>
    internal void Update(RequiredInputSeverity severity, LocalizedText? hint)
    {
        Severity = severity;
        HintL10n = hint;
    }
}
