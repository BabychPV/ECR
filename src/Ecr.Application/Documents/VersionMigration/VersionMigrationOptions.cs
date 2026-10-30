namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Налаштування переносу версії шаблону (RPT-STABLE Part B).</summary>
/// <param name="FromMonthEnabled">
/// Чи дозволено перенос «з місяця X» (<see cref="VersionMigrationRange.FromMonth"/>). Типово <c>false</c> (рішення
/// координатора 30.10): до завершення B2 (переклад решти читачів на шкалу версій) прапор вимкнений, запит
/// <c>range = FromMonth</c> дає <c>422 err.ECR-SCHM-0422.migrateFromMonthDisabled</c>; «увесь проєкт» без змін.
/// </param>
public sealed record VersionMigrationOptions(bool FromMonthEnabled)
{
    /// <summary>Ключ конфігурації.</summary>
    public const string FromMonthEnabledKey = "Documents:VersionMigration:FromMonthEnabled";

    /// <summary>Типове значення: вимкнено.</summary>
    public static VersionMigrationOptions Default { get; } = new(false);
}
