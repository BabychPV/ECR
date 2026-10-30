namespace Ecr.Application.Documents.VersionMigration;

/// <summary>Обсяг переносу версії шаблону: весь проєкт чи лише з обраного місяця (RPT-STABLE Part B).</summary>
public enum VersionMigrationRange
{
    /// <summary>Увесь проєкт у будь-який час (поведінка до Part B): усі періоди переписуються на нову версію.</summary>
    WholeProject = 0,

    /// <summary>
    /// З місяця <c>fromPeriodKey</c>: періоди &lt; X лишаються на старих Id (разом з поданими/затвердженими), періоди ≥ X
    /// переходять на нову версію; нові періоди йдуть на нову версію.
    /// </summary>
    FromMonth = 1,
}

/// <summary>Попередження переносу (не відмова): зміст для діалогу — ключ повідомлення і, за потреби, період.</summary>
/// <param name="Code">Короткий код: <c>effectiveFromShiftedToJanuary</c>.</param>
/// <param name="MessageKey">Ключ UiString (<c>migration.warn.*</c>) у каталозі en/ru/kz.</param>
/// <param name="PeriodKey">Період, якого стосується (напр. ефективний початок).</param>
public sealed record VersionMigrationWarning(string Code, string MessageKey, int? PeriodKey = null);
