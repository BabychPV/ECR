using Ecr.Domain.Enums;

namespace Ecr.Domain.Services;

/// <summary>
/// Класифікує структурну зміну. Від класу залежить, чи дозволена вона взагалі
/// і чи потрібна міграція документів (ФВ-7.4).
/// </summary>
public sealed class ChangeClassifier
{
    /// <summary>Поля презентаційного шару — їх можна міняти в опублікованій версії.</summary>
    public static readonly IReadOnlySet<string> PresentationFields = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Entities.Configuration.ColumnDef.HeaderL10n),
        nameof(Entities.Configuration.ColumnDef.Ordinal),
        nameof(Entities.Configuration.ColumnDef.DisplayFormat),
        nameof(Entities.Configuration.ColumnDef.IsHidden),
        nameof(Entities.Configuration.ColumnDef.StyleId),
        nameof(Entities.Configuration.RowDef.LabelL10n),
        nameof(Entities.Configuration.SheetDef.NameL10n),
        nameof(Entities.Configuration.SheetDef.IsVisible),
        nameof(Entities.Configuration.TableDef.NameL10n)
    };

    /// <summary>
    /// Поля, зміна яких потребує стратегії міграції: тип, точність, одиниця,
    /// довідник. Дані лишаються, але їхнє <b>тлумачення</b> змінюється.
    /// </summary>
    public static readonly IReadOnlySet<string> GuardedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Entities.Configuration.ColumnDef.DataType),
        nameof(Entities.Configuration.ColumnDef.Precision),
        nameof(Entities.Configuration.ColumnDef.Scale),
        nameof(Entities.Configuration.ColumnDef.UnitId),
        nameof(Entities.Configuration.ColumnDef.LookupRegistryDefId),
        nameof(Entities.Configuration.ColumnDef.IsRequired)
    };

    /// <summary>
    /// Поля ідентичності. Їх зміна розриває зв'язок наявних даних із описом:
    /// комірка посилається на <c>ColumnDef.Code</c>, рядок — на <c>RowKey</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> IdentityFields = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Entities.Configuration.ColumnDef.Code),
        nameof(Entities.Configuration.RowDef.RowKeyValue),
        nameof(Entities.Configuration.TableDef.Code),
        nameof(Entities.Configuration.SheetDef.Code)
    };

    /// <summary>Класифікує зміну поля сутності.</summary>
    /// <param name="entityType">Тип сутності (<c>ColumnDef</c>, <c>RowDef</c>…).</param>
    /// <param name="fieldName">Назва поля.</param>
    /// <param name="hasDocuments">Чи існують документи на цій версії.</param>
    /// <remarks>
    /// Порядок перевірок значущий. Презентація йде першою, бо це єдиний клас,
    /// дозволений в опублікованій версії. Ідентичність — перед «охоронюваними»,
    /// бо перейменування коду не рятує жодна стратегія міграції: дані просто
    /// перестають знаходитися.
    /// </remarks>
    public ChangeClass Classify(string entityType, string fieldName, bool hasDocuments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        if (PresentationFields.Contains(fieldName))
        {
            return ChangeClass.Presentation;
        }

        if (IdentityFields.Contains(fieldName))
        {
            // Без документів перейменувати код можна безпечно: посилатися на
            // нього ще нічому. З документами — це Breaking, і за ФВ-7.4
            // операція відхиляється (ECR-SCHM-0409), а не попереджає.
            return hasDocuments ? ChangeClass.Breaking : ChangeClass.Safe;
        }

        if (GuardedFields.Contains(fieldName))
        {
            return ChangeClass.Guarded;
        }

        // Решта структурних полів без документів безпечна; з документами
        // потребує розгляду, але не є руйнівною.
        return hasDocuments ? ChangeClass.Guarded : ChangeClass.Safe;
    }

    /// <summary>
    /// Класифікує <b>додавання</b> сутності.
    /// </summary>
    /// <remarks>
    /// Додавання наявних даних не зачіпає: у нової колонки просто немає
    /// комірок, і клієнт бере <c>DefaultValue</c>. Тому це <c>Safe</c>
    /// незалежно від наявності документів.
    /// </remarks>
    public ChangeClass ClassifyAddition(string entityType) => ChangeClass.Safe;

    /// <summary>
    /// Класифікує <b>видалення</b> сутності.
    /// </summary>
    /// <remarks>
    /// Видалення — завжди soft delete (ФВ-7.6): фізично запис лишається, бо на
    /// нього посилаються дані. Але для версії з документами це все одно
    /// <c>Breaking</c>: колонка зникає зі структури, а її комірки лишаються
    /// висіти без опису.
    /// </remarks>
    public ChangeClass ClassifyDeletion(string entityType, bool hasDocuments)
        => hasDocuments ? ChangeClass.Breaking : ChangeClass.Safe;
}
