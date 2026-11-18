using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations.Scripting.Migration;

/// <summary>Версія методології в базі: що це за версія й чи її можна перетворювати.</summary>
/// <param name="VersionId">Id версії.</param>
/// <param name="MethodologyId">Id методології.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="Version">Номер версії (<c>V1</c>, <c>1.0</c> …).</param>
/// <param name="EffectiveFrom">Дата чинності; <c>null</c> — чернетка без дати.</param>
/// <param name="Status">Стан версії.</param>
/// <param name="Level">Рівень (<c>Configuration</c> чи <c>Module</c>).</param>
/// <param name="Engine">Рушій версії.</param>
/// <param name="Kind">Вид методології.</param>
public sealed record VersionRef(
    int VersionId,
    int MethodologyId,
    string MethodologyCode,
    string Version,
    DateOnly? EffectiveFrom,
    TemplateVersionStatus Status,
    CalculationLevel Level,
    MethodologyEngine Engine,
    MethodologyKind Kind);

/// <summary>Версія, зібрана з бази, з зауваженнями завантаження (вони додаються до зауважень перетворення).</summary>
/// <param name="Ref">Що за версія.</param>
/// <param name="Source">Дані для перетворювача.</param>
/// <param name="Issues">Зауваження завантаження; <c>Error</c> — перетворювати не можна.</param>
public sealed record LoadedVersion(VersionRef Ref, VersionSource Source, IReadOnlyList<ConversionIssue> Issues)
{
    /// <summary>Чи завантаження має помилки.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
}
