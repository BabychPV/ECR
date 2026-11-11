// src/Ecr.Application/Ports/ITemplateLayoutRegistry.cs
namespace Ecr.Application.Ports;

/// <summary>
/// D-474: реєстр версій шаблону з розкладкою <c>YearWithMonthColumns</c> — у пам'яті процесу, щоб
/// <see cref="IMonthColumnRouter"/> відповідав «чи маршрутизується таблиця» без звернення до бази.
/// </summary>
/// <remarks>
/// <para>
/// Версій із річною розкладкою одиниці, тож реєстр — два невеликі набори (версії та їхні аркуші). Порожній
/// реєстр і вимкнений прапор <c>Collection:MonthColumnRouting</c> разом означають: роутер не ходить до бази
/// взагалі, як і до D-474.
/// </para>
/// <para>
/// ⚠ Оновлюється тричі: на старті процесу, періодично (інший процес — Worker — дізнається про нову версію
/// з затримкою до інтервалу) і негайно після зміни розкладки, клонування чи публікації в цьому процесі.
/// </para>
/// </remarks>
public interface ITemplateLayoutRegistry
{
    /// <summary>Чи є хоч одна версія з річною розкладкою.</summary>
    public bool HasAny { get; }

    /// <summary>Чи має версія річну розкладку.</summary>
    /// <param name="templateVersionId">Версія шаблону.</param>
    public bool IsYearVersion(int templateVersionId);

    /// <summary>Чи належить аркуш версії з річною розкладкою.</summary>
    /// <param name="sheetDefId">Аркуш.</param>
    public bool IsYearSheet(int sheetDefId);

    /// <summary>Перечитує реєстр із бази.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task RefreshAsync(CancellationToken ct);
}
