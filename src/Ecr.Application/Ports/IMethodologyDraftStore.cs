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
    /// <summary>Ідентифікатори всіх активних методологій, за кодом.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Саме ідентифікатори, а не самі методології: перелік читає ЦІЛУ
    /// методологію (з версіями) за кожним із них далі, і повертати тут те саме
    /// вдруге означало б завантажити граф двічі.
    /// </remarks>
    public Task<IReadOnlyList<int>> ListActiveIdsAsync(CancellationToken ct);

    /// <summary>Методологія за кодом; <c>null</c> — такої немає.</summary>
    /// <param name="code">Код методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Методологія або <c>null</c>.</returns>
    /// <remarks>
    /// ⛔ Потрібна саме заради ВІДМОВИ створення. Код методології — те, чим на
    /// неї посилаються імпорти (<c>!Name</c> через <c>MethodologyImport</c>) і
    /// прив'язки; два однакові коди роблять посилання неоднозначним, а
    /// унікального індексу на <c>calc.Methodology.Code</c> у схемі немає.
    /// </remarks>
    public Task<Methodology?> FindByCodeAsync(string code, CancellationToken ct);

    /// <summary>Ставить нову методологію в чергу на вставку.</summary>
    /// <param name="methodology">Методологія-контейнер без версій.</param>
    public void AddMethodology(Methodology methodology);

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

    /// <summary>Константи версії з цим кодом — відстежувані; порожньо, якщо їх немає.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="code">Код константи.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Усі варіанти константи з цим кодом.</returns>
    /// <remarks>
    /// ⛔ Повертається ПЕРЕЛІК, а не «перша». Кандидатів на один код у версії
    /// буває кілька — звуження за категорією і речовиною (ФВ-16.5), — і «перша
    /// з кількох» означала б, що запис базового значення мовчки править варіант,
    /// заведений для однієї установки. Вибір із кількох робить викликач, і
    /// сьогодні він єдино чесний: відмовляє, бо адреса запиту (код) на кілька
    /// варіантів не вказує.
    /// </remarks>
    public Task<IReadOnlyList<MethodologyConstant>> GetConstantsByCodeAsync(
        int methodologyVersionId, string code, CancellationToken ct);

    /// <summary>Правило версії за кодом — відстежуване; <c>null</c>, якщо його немає.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="code">Код правила.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Правило або <c>null</c>.</returns>
    public Task<MethodologyRule?> FindRuleAsync(
        int methodologyVersionId, string code, CancellationToken ct);

    /// <summary>Вихід версії за кодом — відстежуваний; <c>null</c>, якщо його немає.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="code">Код виходу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Вихід або <c>null</c>.</returns>
    public Task<MethodologyOutput?> FindOutputAsync(
        int methodologyVersionId, string code, CancellationToken ct);

    /// <summary>Тест версії за кодом — відстежуваний; <c>null</c>, якщо його немає.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="code">Код тесту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Тест або <c>null</c>.</returns>
    public Task<MethodologyTestCaseEntity?> FindTestCaseAsync(
        int methodologyVersionId, string code, CancellationToken ct);

    /// <summary>Усі правила версії, включно з вимкненими.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Правила в порядку пріоритету.</returns>
    /// <remarks>
    /// ⛔ Фільтра <c>IsActive</c> тут НЕМАЄ — і саме цим читання для
    /// РЕДАГУВАННЯ відрізняється від <see cref="IMethodologyStore.GetRulesAsync"/>.
    /// Вимкнене правило, невидиме в редакторі, неможливо ні ввімкнути назад, ні
    /// пояснити, чому рядки документа не рахуються.
    /// </remarks>
    public Task<IReadOnlyList<MethodologyRule>> GetAllRulesAsync(
        int methodologyVersionId, CancellationToken ct);

    /// <summary>Усі тести версії — золотий набір (ФВ-13.7).</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Тести в порядку коду.</returns>
    public Task<IReadOnlyList<MethodologyTestCaseEntity>> GetTestCaseEntitiesAsync(
        int methodologyVersionId, CancellationToken ct);

    /// <summary>Ставить формулу в чергу на вставку; зберігає <c>IUnitOfWork</c>.</summary>
    /// <param name="formula">Формула, створена <c>MethodologyVersion.AddFormula</c>.</param>
    public void Add(MethodologyFormula formula);

    /// <summary>Ставить константу в чергу на вставку; зберігає <c>IUnitOfWork</c>.</summary>
    /// <param name="constant">Константа версії-чернетки.</param>
    public void Add(MethodologyConstant constant);

    /// <summary>Ставить правило в чергу на вставку; зберігає <c>IUnitOfWork</c>.</summary>
    /// <param name="rule">Правило версії-чернетки.</param>
    public void Add(MethodologyRule rule);

    /// <summary>Ставить вихід у чергу на вставку; зберігає <c>IUnitOfWork</c>.</summary>
    /// <param name="output">Оголошений вихід версії-чернетки.</param>
    public void Add(MethodologyOutput output);

    /// <summary>Ставить тест у чергу на вставку; зберігає <c>IUnitOfWork</c>.</summary>
    /// <param name="testCase">Тест золотого набору версії-чернетки.</param>
    public void Add(MethodologyTestCaseEntity testCase);

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
