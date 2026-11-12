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

/// <summary>Роль поточного кроку, відома викликачу наперед (AN-59): маршрут не читається вдруге.</summary>
/// <param name="RoleId">Роль кроку; <c>null</c> — маршруту немає, затвердження одноетапне.</param>
public sealed record KnownStepRole(int? RoleId);

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

    /// <summary>Проєкт документа; <c>null</c> — документа немає.</summary>
    /// <remarks>
    /// Потрібен для проєктного функціонального права (ФВ-6.14,
    /// <see cref="AccessProfile.Has(string, int)"/>) у точках, що знають лише документ.
    /// </remarks>
    public Task<int?> DocumentProjectIdAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// Що з документа профіль бачить нижче рівня проєкту — таблиці й колонки
    /// (S6, ФВ-6.6: заборона виграє на будь-якому рівні й на читанні).
    /// </summary>
    /// <remarks>
    /// ⚠ НЕ заміна <see cref="CanReadDocumentAsync"/>: видимість документа
    /// викликач перевіряє першою (невидимий документ — 404), а ця межа лише
    /// звужує вже видимий.
    /// </remarks>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<DocumentReadScope> ReadScopeAsync(AccessProfile profile, long documentId, CancellationToken ct);

    /// <summary>
    /// D-443: межі читання КІЛЬКОХ документів одним викликом — для переліку, де документи, відкриті наданням
    /// учасника, мають кожен свої межі (межі одного проєкту на них не годяться).
    /// </summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="documentIds">Документи; відсутні в результаті не повертаються.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Документ → межі (з наданнями учасника цього документа).</returns>
    /// <remarks>⚠ Сталу кількість звернень до бази на весь виклик, а не на документ.</remarks>
    public Task<IReadOnlyDictionary<long, DocumentReadScope>> ReadScopesAsync(
        AccessProfile profile, IReadOnlyCollection<long> documentIds, CancellationToken ct);

    /// <summary>
    /// D-443: чи має користувач ДІЮЧЕ надання учасника в документі (виконавець передачі, не прострочене, делегатор
    /// активний). Без ознаки <see cref="AccessProfile.HasDocumentParticipations"/> — <c>false</c> без бази.
    /// </summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>⛔ Заборону проєкту не враховує: це робить викликач (<see cref="AccessProfile.DeniesProject"/>).</remarks>
    public Task<bool> ParticipatesAsync(AccessProfile profile, long documentId, CancellationToken ct);

    /// <summary>D-443: з поданих документів — ті, у яких користувач має діюче надання учасника (один запит).</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="documentIds">Документи.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlySet<long>> ParticipatedDocumentsAsync(
        AccessProfile profile, IReadOnlyCollection<long> documentIds, CancellationToken ct);

    /// <summary>
    /// D-443: чи має користувач діюче надання хоча б в одному документі проєкту (календар періодів проєкту для
    /// сторінки документа учасника).
    /// </summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<bool> ParticipatesInProjectAsync(AccessProfile profile, int projectId, CancellationToken ct);

    /// <summary>
    /// D-444 (AN-131): УСІ документи, де користувач має діюче надання учасника, з наданнями кожного — для черги
    /// «До погодження», зведення переліку й пошуку. Ті самі факти й умови, що й рішення про доступ
    /// (<see cref="DocumentParticipation.Active"/>).
    /// </summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Документ → діючі надання (лише непорожні).</returns>
    /// <remarks>
    /// ⚠ Без ознаки <see cref="AccessProfile.HasDocumentParticipations"/> чи для інтеграції — порожньо БЕЗ бази (бюджет
    /// запитів решти користувачів не змінюється); учасник — два запити. ⛔ Заборону проєкту не враховує: це робить
    /// викликач (<see cref="AccessProfile.DeniesProject"/>), бо проєкт документа знає він.
    /// </remarks>
    public Task<IReadOnlyDictionary<long, IReadOnlyList<ParticipationGrant>>> ActiveParticipationsAsync(
        AccessProfile profile, CancellationToken ct);

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
    /// Чи можна СТЕРТИ значення цих комірок (видалення рядка, D3-A): те саме рішення, що й
    /// <see cref="CanEditCellsAsync"/> — грант на колонку, період, правила доступу до періоду, передача зрізу, —
    /// АЛЕ без ознак колонки «обчислювана»/«лише для читання».
    /// </summary>
    /// <remarks>
    /// ⛔ S-2: <c>CanEdit</c> віддає <c>CalculatedCell</c>/<c>ColumnReadOnly</c> РАНІШЕ за перевірку гранта, тож
    /// явний Deny на таку колонку лишався б непоміченим. Ознаки колонки тут не заважають (людина не вводила
    /// ці значення руками), а грант на колонку перевіряється завжди.
    /// ⚠ Результат ТАКИЙ САМИЙ за формою: <c>RequiresConfirmation</c>/<c>OutOfWindowMark</c> повертаються як є —
    /// рішення про них приймає викликач (видалення каналу підтвердження не має).
    /// </remarks>
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanClearCellsAsync(
        AccessProfile profile, long tableInstanceId, PeriodKey periodKey,
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct);

    /// <summary>
    /// Рішення «чи можна змінити ОФОРМЛЕННЯ (колір) комірки» на КОЖНУ комірку зрізу (CELL-STYLE P1A): те саме
    /// рішення, що й <see cref="CanClearCellsAsync"/> (його приймає <c>PATCH …/cell-styles</c>), але для всього
    /// зрізу одним викликом — для ознаки «можна фарбувати» у відповіді зрізу.
    /// </summary>
    /// <remarks>
    /// ⛔ Окремий метод, а не <see cref="CanEditSliceAsync"/>: ознаки колонки «обчислювана»/«лише читання»
    /// забороняють ЗНАЧЕННЯ, але не колір (майже весь Land — calculated/readOnly), тож рішення на редагування
    /// не годиться як «можна фарбувати».
    /// </remarks>
    public Task<IReadOnlyDictionary<CellAddress, EditDecision>> CanStyleSliceAsync(
        AccessProfile profile, long tableInstanceId, CancellationToken ct);

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

    /// <summary>
    /// C-7 / D-285: чи має користувач повноваження подання цього аркуша (рівень Submit або Write + <c>Document.Submit</c>)
    /// у ПОВНОМУ контексті рішення — проєкт, код аркуша, період, участь (D-443, D-214) — без перевірки стану аркуша.
    /// </summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія аркуша (RS); <c>null</c> — оригінал.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Для відкликання (<c>RecallSheetHandler</c>): <c>CanSubmitAsync</c> відмовляє на поданому аркуші ще до гранта,
    /// а контекст без коду аркуша, періоду й участі не бачив звужених ролей і учасників — «відкликання не має
    /// вимагати від автора більше, ніж подання».
    /// </remarks>
    public Task<bool> HasSubmitGrantAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId, CancellationToken ct);

    /// <summary>
    /// AN-59: профілі кількох користувачів одним пакетом (стала кількість звернень до бази, а не за користувача). Профіль
    /// той самий, що й <see cref="BuildProfileAsync"/>; вимкнений чи відсутній користувач у результаті відсутній.
    /// </summary>
    /// <param name="userIds">Користувачі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>⚠ Типова реалізація (тестові обгортки) — цикл поштучних; справжня служба читає пакетом.</remarks>
    public async Task<IReadOnlyDictionary<int, AccessProfile>> BuildProfilesAsync(
        IReadOnlyCollection<int> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var result = new Dictionary<int, AccessProfile>();
        foreach (var id in userIds.Distinct())
        {
            try
            {
                result[id] = await BuildProfileAsync(id, ct).ConfigureAwait(false);
            }
            catch (Errors.AccessDeniedException)
            {
                // Вимкнений обліковий запис профілю не має.
            }
        }

        return result;
    }

    /// <summary>
    /// AN-59: <see cref="SheetCopyAccess.CanApproveOfAsync"/> для КІЛЬКОХ профілів одного аркуша (копії) — те саме
    /// рішення на кожен профіль, але умови аркуша й крок маршруту читаються раз, а залежне від профілю — множинними
    /// запитами. Результат — у порядку <paramref name="profiles"/>.
    /// </summary>
    /// <param name="profiles">Профілі.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія; <c>null</c> — оригінал.</param>
    /// <param name="knownStep">
    /// Роль поточного кроку, уже відома викликачу (він обчислив її тим самим маршрутом, ApprovalRoute.StepAt), — щоб не
    /// читати маршрут вдруге; <c>null</c> — крок читається тут.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Поштучне <see cref="CanApproveAsync"/> справжньої служби і є цим методом з одним профілем (тест еквівалентності
    /// <c>CanApproveManyEquivalenceTests</c>). Типова реалізація — цикл поштучних.
    /// </remarks>
    public async Task<IReadOnlyList<EditDecision>> CanApproveManyAsync(
        IReadOnlyList<AccessProfile> profiles, long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId,
        KnownStepRole? knownStep, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var result = new List<EditDecision>(profiles.Count);
        foreach (var profile in profiles)
        {
            result.Add(await this.CanApproveOfAsync(profile, documentId, sheetDefId, periodKey, sheetCopyId, ct).ConfigureAwait(false));
        }

        return result;
    }

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

    /// <summary><see cref="CanSubmitAsync"/> для КОПІЇ аркуша (RS).</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія; не <c>null</c> (оригінал — перевантаження без копії, див. <see cref="SheetCopyAccess"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>⚠ Типова реалізація (тестові обгортки) копій не знає; справжня — <c>AccessDecisionService</c>.</remarks>
    public Task<EditDecision> CanSubmitCopyAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, long sheetCopyId, CancellationToken ct)
        => throw new NotSupportedException("Рішення про копію аркуша не реалізовано цією службою.");

    /// <summary><see cref="CanApproveAsync"/> для КОПІЇ аркуша (RS).</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<EditDecision> CanApproveCopyAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, long sheetCopyId, CancellationToken ct)
        => throw new NotSupportedException("Рішення про копію аркуша не реалізовано цією службою.");

    /// <summary><see cref="CanReopenAsync"/> для КОПІЇ аркуша (RS).</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<EditDecision> CanReopenCopyAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey, long sheetCopyId, CancellationToken ct)
        => throw new NotSupportedException("Рішення про копію аркуша не реалізовано цією службою.");

    /// <summary><see cref="CurrentApprovalStepAsync"/> для КОПІЇ аркуша (RS).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetCopyId">Копія.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<ApprovalStepView?> CurrentCopyApprovalStepAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, long sheetCopyId, CancellationToken ct)
        => throw new NotSupportedException("Рішення про копію аркуша не реалізовано цією службою.");
}

/// <summary>
/// RS: виклик рішень і блокувань аркуша з <c>long? sheetCopyId</c> — оригінал (<c>null</c>) іде ТИМ САМИМ
/// перевантаженням, що й до копій, копія — своїм.
/// </summary>
/// <remarks>
/// ⚠ Чому не одне перевантаження з <c>long?</c>: тести підміняють службу й блокування (NSubstitute, обгортки), і виклик
/// нового члена з <c>null</c> повз їхні налаштування повернув би типове значення замість налаштованого.
/// </remarks>
public static class SheetCopyAccess
{
    /// <summary>Подання аркуша або копії.</summary>
    public static Task<EditDecision> CanSubmitOfAsync(
        this IAccessDecisionService access, AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey,
        long? sheetCopyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        return sheetCopyId is { } copy
            ? access.CanSubmitCopyAsync(profile, documentId, sheetDefId, periodKey, copy, ct)
            : access.CanSubmitAsync(profile, documentId, sheetDefId, periodKey, ct);
    }

    /// <summary>Затвердження аркуша або копії.</summary>
    public static Task<EditDecision> CanApproveOfAsync(
        this IAccessDecisionService access, AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey,
        long? sheetCopyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        return sheetCopyId is { } copy
            ? access.CanApproveCopyAsync(profile, documentId, sheetDefId, periodKey, copy, ct)
            : access.CanApproveAsync(profile, documentId, sheetDefId, periodKey, ct);
    }

    /// <summary>Повернення в роботу аркуша або копії.</summary>
    public static Task<EditDecision> CanReopenOfAsync(
        this IAccessDecisionService access, AccessProfile profile, long documentId, int sheetDefId, PeriodKey periodKey,
        long? sheetCopyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        return sheetCopyId is { } copy
            ? access.CanReopenCopyAsync(profile, documentId, sheetDefId, periodKey, copy, ct)
            : access.CanReopenAsync(profile, documentId, sheetDefId, periodKey, ct);
    }

    /// <summary>Крок маршруту аркуша або копії.</summary>
    public static Task<ApprovalStepView?> CurrentApprovalStepOfAsync(
        this IAccessDecisionService access, long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        return sheetCopyId is { } copy
            ? access.CurrentCopyApprovalStepAsync(documentId, sheetDefId, periodKey, copy, ct)
            : access.CurrentApprovalStepAsync(documentId, sheetDefId, periodKey, ct);
    }

    /// <summary>Спільне блокування правки аркуша або копії.</summary>
    public static Task<DocumentStatus> EnterEditOfAsync(
        this Ports.ISheetEditGate gate, long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return sheetCopyId is null
            ? gate.EnterEditAsync(documentId, sheetDefId, periodKey, ct)
            : gate.EnterCopyEditAsync(documentId, sheetDefId, periodKey, sheetCopyId.Value, ct);
    }

    /// <summary>Спільне блокування правки аркуша або копії без черги.</summary>
    public static Task<DocumentStatus> EnterEditNoWaitOfAsync(
        this Ports.ISheetEditGate gate, long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return sheetCopyId is null
            ? gate.EnterEditNoWaitAsync(documentId, sheetDefId, periodKey, ct)
            : gate.EnterCopyEditNoWaitAsync(documentId, sheetDefId, periodKey, sheetCopyId.Value, ct);
    }

    /// <summary>Виняткове блокування подання аркуша або копії.</summary>
    public static Task EnterSubmitOfAsync(
        this Ports.ISheetEditGate gate, long documentId, int sheetDefId, PeriodKey periodKey, long? sheetCopyId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return sheetCopyId is null
            ? gate.EnterSubmitAsync(documentId, sheetDefId, periodKey, ct)
            : gate.EnterCopySubmitAsync(documentId, sheetDefId, periodKey, sheetCopyId.Value, ct);
    }
}
