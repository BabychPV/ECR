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

    /// <summary>Класифікує зміну поля сутності.</summary>
    /// <param name="entityType">Тип сутності (<c>ColumnDef</c>, <c>RowDef</c>…).</param>
    /// <param name="fieldName">Назва поля.</param>
    /// <param name="hasDocuments">Чи існують документи на цій версії.</param>
    public ChangeClass Classify(string entityType, string fieldName, bool hasDocuments)
        => throw new NotImplementedException(
            "TODO: якщо fieldName у PresentationFields → Presentation; " +
            "додавання нової сутності → Safe; " +
            "зміна DataType/Precision/UnitId/LookupRegistryDefId → Guarded; " +
            "видалення колонки/рядка або зміна Code/RowKey при hasDocuments → Breaking. " +
            "Breaking у версії з документами = відмова операції, не попередження (ФВ-7.4).");
}
