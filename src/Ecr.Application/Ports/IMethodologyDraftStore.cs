// src/Ecr.Application/Ports/IMethodologyDraftStore.cs
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Ports;

/// <summary>
/// Сховище **редагованої** частини методології (ФВ-9.15): усі версії, включно
/// з чернетками, і формули, які в чернетці правлять.
/// </summary>
/// <remarks>
/// ⛔ Порт окремий від <see cref="IMethodologyStore"/> навмисно. Той обслуговує
/// РОЗРАХУНОК і показує лише опубліковане: <c>GetPublishedVersionsAsync</c>
/// фільтрує за <c>Status = Published</c>, бо рахувати чернеткою не можна
/// ніколи. Додати сюди «а ще віддай чернетки» означало б, що одна необережна
/// зміна фільтра пускає незавершену версію в числа звіту.
///
/// ⚠ Дзеркальна помилка теж реальна: доки чернеток не віддавав НІХТО,
/// редагувати їх було ніде, а кнопка «Опублікувати» в переліку методологій
/// показувалася для версій, яких перелік не містив за побудовою.
/// </remarks>
public interface IMethodologyDraftStore
{
    /// <summary>
    /// Методологія з усіма версіями — **відстежувана**, щоб додану чернетку
    /// було чим зберегти.
    /// </summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Агрегат або <c>null</c>, якщо методології немає.</returns>
    /// <remarks>
    /// ⚠ Саме агрегат: унікальність номера версії перевіряє
    /// <c>Methodology.AddVersion</c>, і без сусідніх версій ця перевірка не
    /// має чого порівнювати.
    /// </remarks>
    public Task<Methodology?> FindAsync(int methodologyId, CancellationToken ct);

    /// <summary>
    /// Усі версії методології: чернетки, опубліковані і виведені з обігу.
    /// </summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Версії в порядку номера.</returns>
    public Task<IReadOnlyList<MethodologyVersion>> GetAllVersionsAsync(
        int methodologyId, CancellationToken ct);

    /// <summary>Версія за ідентифікатором — відстежувана.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Версія або <c>null</c>.</returns>
    public Task<MethodologyVersion?> FindVersionAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>Формула версії за кодом — відстежувана; <c>null</c>, якщо її немає.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="code">Код формули.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Формула або <c>null</c>.</returns>
    public Task<MethodologyFormula?> FindFormulaAsync(
        int methodologyVersionId, string code, CancellationToken ct);

    /// <summary>Ставить формулу в чергу на вставку; зберігає <c>IUnitOfWork</c>.</summary>
    /// <param name="formula">Формула, створена <c>MethodologyVersion.AddFormula</c>.</param>
    public void Add(MethodologyFormula formula);

    /// <summary>Ставить формулу в чергу на видалення; зберігає <c>IUnitOfWork</c>.</summary>
    /// <param name="formula">Формула, дозволена <c>MethodologyVersion.RemoveFormula</c>.</param>
    public void Remove(MethodologyFormula formula);

    /// <summary>
    /// Зберігає нову чернетку і переносить у неї **весь** вміст версії-джерела.
    /// </summary>
    /// <param name="draft">Чернетка, вже додана до агрегату.</param>
    /// <param name="copyFromVersionId">
    /// Версія-джерело; <c>null</c> — порожня чернетка.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор створеної чернетки.</returns>
    /// <remarks>
    /// ⛔ Копіювання живе в сховищі, бо дочірні записи посилаються на версію
    /// ЧИСЛОМ, а не навігацією: їхній зовнішній ключ можна проставити лише
    /// після того, як база призначила ключ чернетці. Це той самий випадок, що
    /// й <c>TemplateVersionStore.CloneAsync</c>.
    ///
    /// ⛔ «Весь вміст» — вимога, а не обіцянка. Клон, який переніс формули і
    /// забув константи, дає версію, що рахує тими самими виразами по порожніх
    /// коефіцієнтах: публікація її не спинить, бо зелений тест перевіряє
    /// результат, а результат теж порахується — просто інший. За повнотою
    /// стежить архітектурний сторож
    /// <c>Клон_версії_методології_переносить_кожен_набір_дочірніх_записів</c>.
    /// </remarks>
    public Task<int> SaveDraftAsync(MethodologyVersion draft, int? copyFromVersionId, CancellationToken ct);
}
