using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Часова шкала версій шаблону проєкту (RPT-STABLE Part B): ЄДИНА точка відповіді «яка версія шаблону діє за період».
/// </summary>
/// <remarks>
/// ⛔ Версію періоду вирішує лише цей порт (і SQL-функція <c>doc.fn_VersionOf</c> для запитів-приєднань).
/// <c>Project.TemplateVersionId</c> — це «чинна/остання» версія, за якою створюються НОВІ періоди; читати її для
/// відповіді про конкретний період заборонено сторожем <c>ProjectTemplateVersionReadGuardTests</c>.
/// ⚠ Читання йдуть крізь короткий кеш (малий відсортований список на проєкт); писарі (створення документів,
/// аркушів, екземплярів) беруть <see cref="VersionOfFreshAsync"/> — свіжо з бази, щоб не писати за застарілою шкалою.
/// </remarks>
public interface IProjectVersionTimeline
{
    /// <summary>Версія шаблону, що діє за період (кешоване читання).</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період (<c>Рік*100+№</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<int> VersionOfAsync(int projectId, int periodKey, CancellationToken ct);

    /// <summary>Те саме, але свіжо з бази (для писарів і перевірок під блоком проєкту).</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<int> VersionOfFreshAsync(int projectId, int periodKey, CancellationToken ct);

    /// <summary>Шкала проєкту за зростанням початку; порожня — проєкт має одну версію (чинну).</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<TimelineEntry>> GetAsync(int projectId, CancellationToken ct);

    /// <summary>Чинна (остання) версія проєкту — для НОВИХ періодів; <c>null</c> — проєкту немає.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<int?> CurrentVersionAsync(int projectId, CancellationToken ct);

    /// <summary>Скидає кеш проєкту (після переносу версії / зміни шкали).</summary>
    /// <param name="projectId">Проєкт.</param>
    public void Invalidate(int projectId);
}
