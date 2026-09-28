// src/Ecr.Application/Security/IAccessDecisionService.cs

using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>
/// Єдина точка рішень про доступ. Поєднує RBAC, стан періоду, правила періодів
/// шаблону, статус документа, структурні і бізнес-обмеження (ФВ-6.8).
/// </summary>
/// <summary>Крок маршруту погодження, якого чекає аркуш.</summary>
/// <param name="StepId">Ідентифікатор кроку.</param>
/// <param name="Ordinal">Порядковий номер кроку в маршруті, від 1.</param>
/// <param name="RoleId">Роль, яка затверджує на цьому кроці.</param>
/// <param name="NextStepId">Наступний крок; <c>null</c> — цей останній.</param>
/// <param name="TotalSteps">Скільки кроків у маршруті — для підпису «крок 2 з 3».</param>
public sealed record ApprovalStepView(
    int StepId, int Ordinal, int RoleId, int? NextStepId, int TotalSteps);

/// <summary>Доступ до рядка, якого ще немає.</summary>
/// <param name="Row">
/// Рішення без огляду на колонку: період, проєкт, аркуш, вікно доступу, грант
/// на рівні проєкту / аркуша / таблиці.
/// </param>
/// <param name="Columns">
/// Рішення по кожній колонці таблиці для цього рядка. Порожньо, коли в
/// таблиці немає жодної колонки.
/// </param>
public sealed record NewRowAccess(
    EditDecision Row, IReadOnlyDictionary<int, EditDecision> Columns);

/// <summary>Адреси одного екземпляра для пакетного рішення про запис.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період, за яким шукаються рядки, — як у <c>CanEditCellsAsync</c>.</param>
/// <param name="Addresses">Адреси комірок батчу цього екземпляра.</param>
public sealed record CellsAccessRequest(
    long TableInstanceId, PeriodKey PeriodKey, IReadOnlyCollection<CellAddress> Addresses);

public interface IAccessDecisionService
{
    /// <summary>Будує профіль прав користувача. Викликається раз на сесію.</summary>
    public Task<AccessProfile> BuildProfileAsync(int userId, CancellationToken ct);

    /// <summary>
    /// Точково скидає кешований профіль користувача, не чіпаючи його штамп
    /// безпеки — отже, і не розлоговуючи його активну сесію.
    /// </summary>
    /// <remarks>
    /// ⛔ Не заміна <c>RotateStampsForRoleAsync</c>: та навмисно розлоговує
    /// ВСІХ носіїв ролі негайно (ФВ-6.7) — правильна поведінка, коли
    /// адміністратор відкликає чужий доступ. Цей метод — для протилежного
    /// випадку: користувач щойно сам собі (побічно) розширив доступ власною
    /// дією (наприклад, створенням проєкту) і має побачити це в ТІЙ САМІЙ
    /// сесії, без примусового виходу.
    /// </remarks>
    /// <param name="userId">Користувач, чий кешований профіль застарів.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task InvalidateProfileAsync(int userId, CancellationToken ct);

    /// <summary>Чи може користувач читати документ.</summary>
    public Task<EditDecision> CanReadDocumentAsync(AccessProfile profile, long documentId, CancellationToken ct);

    /// <summary>Чи може користувач редагувати конкретну комірку.</summary>
    public Task<EditDecision> CanEditCellAsync(
        AccessProfile profile, long documentId, CellAddress address, CancellationToken ct);

    /// <summary>
    /// Пакетна перевірка для відкриття таблиці: повертає рішення на кожну
    /// комірку зрізу одним проходом. Поштучний виклик <see cref="CanEditCellAsync"/>
    /// у циклі — антипатерн і не вкладається в бюджет.
    /// </summary>
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct);

    /// <summary>
    /// Те саме, що <see cref="CanEditSliceAsync"/>, але для КІЛЬКОХ зрізів
    /// одним викликом — за сталу кількість звернень до бази, а не за
    /// кількість таблиць (P8, перегляд імпорту книги).
    /// </summary>
    /// <param name="profile">Профіль прав користувача.</param>
    /// <param name="tableInstanceIds">Екземпляри таблиць.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Екземпляр → рішення на кожну комірку його зрізу. Кожен запитаний
    /// екземпляр присутній; неіснуючий — <c>ECR-DOC-0404</c>, як і в
    /// <see cref="CanEditSliceAsync"/>.
    /// </returns>
    /// <remarks>
    /// ⛔ Рішення по кожному екземпляру ТОТОЖНІ поштучному
    /// <see cref="CanEditSliceAsync"/> — поштучний і є цим методом з одним
    /// екземпляром (тест еквівалентності —
    /// <c>AccessDecisionBatchEquivalenceTests</c>).
    /// </remarks>
    public Task<IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>> CanEditSlicesAsync(
        AccessProfile profile, IReadOnlyCollection<long> tableInstanceIds, CancellationToken ct);

    /// <summary>
    /// Пакетна перевірка для запису: рішення лише для <paramref name="addresses"/>,
    /// а не для всього зрізу.
    /// </summary>
    /// <param name="profile">Профіль прав користувача.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="periodKey">Період екземпляра — той самий, що й у кожній адресі батчу.</param>
    /// <param name="addresses">Адреси комірок батчу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Адреса → рішення. Адреса, чийого рядка чи колонки не існує (рядок
    /// видалено чи створено паралельним запитом між читаннями), у словнику
    /// відсутня — так само, як і в <see cref="CanEditSliceAsync"/>; викликач
    /// трактує відсутність запису як відмову сам.
    /// </returns>
    /// <remarks>
    /// ⛔ <c>DIRECTIVE-14-ARCH.md</c>, <c>WR-03</c>. <see cref="CanEditSliceAsync"/>
    /// читає ВСІ рядки екземпляра і будує словник <c>rows × columns</c>, а
    /// <c>PatchCellsHandler</c> використовує з нього лише адреси батчу — на
    /// таблиці 500×60 це до 30 000 зайвих рішень заради, наприклад, однієї
    /// зміненої комірки. Цей метод фільтрує рядки одразу за
    /// <paramref name="periodKey"/> і за ідентифікаторами рядків із
    /// <paramref name="addresses"/>, і рахує рішення лише для запитаних
    /// комірок — тим самим обчислювачем правил, що й <see cref="CanEditSliceAsync"/>.
    ///
    /// ⚠ Не заміна <see cref="CanEditSliceAsync"/>: той лишається для читання
    /// (відкриття таблиці, де рішення потрібні на кожну комірку зрізу
    /// одразу) і переробляється окремою задачею <c>RD-02</c>.
    /// </remarks>
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanEditCellsAsync(
        AccessProfile profile, long tableInstanceId, PeriodKey periodKey,
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct);

    /// <summary>
    /// Рішення для рядків, яких у зрізі ще <b>немає</b> — тобто для створення.
    /// </summary>
    /// <param name="profile">Профіль прав користувача.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці, куди додається рядок.</param>
    /// <param name="rowKeys">Ключі рядків, які збираються створити.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ключ рядка → рішення на рядок і на кожну колонку.</returns>
    /// <remarks>
    /// ⛔ Окремий метод потрібен тому, що <see cref="CanEditSliceAsync"/>
    /// принципово не може відповісти на це питання: він ключує рішення
    /// <c>CellAddress</c>, у якій є <c>RowId</c>, а в рядка, якого ще немає,
    /// його немає. Саме через це створення обходило модель доступу цілком:
    /// адрес не збиралося, перевірка пропускалася, і запис у ЗАКРИТИЙ період
    /// проходив із кодом <c>200</c>.
    ///
    /// ⚠ Рішення на рядок і на колонки — різні питання, і потрібні обидва.
    /// «Чи можна тут писати взагалі» (період, проєкт, аркуш, вікно доступу)
    /// не залежить від колонки, і саме його питає <c>CreateRowHandler</c>: у
    /// момент створення рядка колонок ще ніхто не назвав. А от гранти бувають
    /// на колонку (<c>ResourceKind.Column</c>), тож пакетний запис у щойно
    /// створений рядок мусить звірятися по кожній адресі окремо.
    ///
    /// ⚠ Один виклик на ВЕСЬ батч, як і у зрізу: бюджет прав — 50 мс
    /// (<c>ФВ-6.10</c>), і виклик на рядок його не витримає.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, NewRowAccess>> CanCreateRowsAsync(
        AccessProfile profile, long tableInstanceId, IReadOnlyCollection<string> rowKeys, CancellationToken ct);

    /// <summary>
    /// <see cref="CanEditCellsAsync"/> для КІЛЬКОХ екземплярів одним викликом —
    /// за сталу кількість звернень до бази, а не за кількість таблиць (P8,
    /// застосування імпорту книги).
    /// </summary>
    /// <param name="profile">Профіль прав користувача.</param>
    /// <param name="requests">Екземпляр, його період і адреси; екземпляри не повторюються.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Екземпляр → рішення на його адреси, рівно як у <see cref="CanEditCellsAsync"/>:
    /// кожен запитаний екземпляр присутній; адреса без рядка цього екземпляра
    /// в запитаному періоді чи без колонки його таблиці — відсутня.
    /// </returns>
    /// <remarks>
    /// ⛔ Поштучний <see cref="CanEditCellsAsync"/> і є цим методом з одним
    /// екземпляром (тест еквівалентності — <c>AccessDecisionBatchEquivalenceTests</c>).
    /// </remarks>
    public Task<IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>> CanEditCellsBatchAsync(
        AccessProfile profile, IReadOnlyCollection<CellsAccessRequest> requests, CancellationToken ct);

    /// <summary>
    /// <see cref="CanCreateRowsAsync"/> для КІЛЬКОХ екземплярів одним викликом
    /// (P8, застосування імпорту книги).
    /// </summary>
    /// <param name="profile">Профіль прав користувача.</param>
    /// <param name="rowKeysByInstance">Екземпляр → ключі рядків, які збираються створити.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Екземпляр → те, що дав би <see cref="CanCreateRowsAsync"/>; кожен запитаний присутній.</returns>
    /// <remarks>
    /// ⛔ Поштучний <see cref="CanCreateRowsAsync"/> і є цим методом з одним екземпляром.
    /// </remarks>
    public Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, NewRowAccess>>> CanCreateRowsBatchAsync(
        AccessProfile profile,
        IReadOnlyDictionary<long, IReadOnlyCollection<string>> rowKeysByInstance,
        CancellationToken ct);

    /// <summary>Чи може користувач подати аркуш за період на затвердження.</summary>
    public Task<EditDecision> CanSubmitAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Чи може користувач затвердити аркуш за період.</summary>
    public Task<EditDecision> CanApproveAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>Чи може користувач повернути поданий/затверджений аркуш у <c>Draft</c>.</summary>
    /// <remarks>
    /// ⛔ Q-173 (аудит фази 2, авторизація). Раніше <c>ReopenDocumentHandler</c>
    /// перевіряв лише глобальне <c>Document.Reopen</c> — жодного рішення,
    /// прив'язаного до документа, на відміну від <see cref="CanSubmitAsync"/>
    /// і <see cref="CanApproveAsync"/>. Операція, що скасовує подання, вимагала
    /// МЕНШЕ перевірок за саме подання.
    /// </remarks>
    public Task<EditDecision> CanReopenAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);

    /// <summary>
    /// Крок маршруту погодження, якого чекає аркуш (<c>ФВ-5.17</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Повертає <c>null</c>, коли маршруту немає — і це нормальний,
    /// найчастіший стан: затвердження одноетапне, як було до маршрутів.
    /// </remarks>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<ApprovalStepView?> CurrentApprovalStepAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct);
}
