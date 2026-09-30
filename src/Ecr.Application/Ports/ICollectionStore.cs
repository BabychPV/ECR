// src/Ecr.Application/Ports/ICollectionStore.cs

using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Стан і результати збору із зовнішніх джерел.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b> До цього
/// <c>Ecr.Adapters.PiAf.CollectionRunner</c> і <c>CatchUpPlanner</c> були
/// типізовані напряму на <c>EcrDbContext</c>, хоча <c>05-skeleton.md</c> §4
/// дозволяє адаптерам знати лише <c>Domain</c> і <c>Application</c>.
///
/// Джерело істини щодо того, за які інтервали дані вже є, — це
/// <c>itg.CollectionCoverage</c>, а не <c>Watermark</c> у розкладі:
/// watermark — оптимізація, а не стан, і його втрата не має коштувати даних
/// (ER-I-03).
/// </remarks>
public interface ICollectionStore
{
    /// <summary>Сутність джерела; <c>null</c>, якщо її немає або вона вимкнена.</summary>
    public Task<SourceEntity?> FindSourceEntityAsync(int sourceEntityId, CancellationToken ct);

    /// <summary>Джерело — воно визначає транспорт. Вибір транспорту це налаштування, не гілка коду (ФВ-11.2).</summary>
    public Task<DataSource?> FindDataSourceAsync(int dataSourceId, CancellationToken ct);

    /// <summary>Створює <c>itg.CollectionRun</c> і повертає його ідентифікатор.</summary>
    public Task<long> StartRunAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc,
        bool isCatchUp, int? triggeredByUserId, CancellationToken ct);

    /// <summary>Завершує прогін. Відмова джерела — теж завершення, зі статусом і кодом.</summary>
    public Task FinishRunAsync(
        long collectionRunId, string status, int pointsRetrieved,
        string? errorMessage, CancellationToken ct);

    /// <summary>
    /// Upsert точок за природним ключем <c>(SourceEntityId, SourcePath, Timestamp)</c> —
    /// повторний запуск того самого діапазону не дублює даних (ФВ-11.3).
    /// Значення зберігаються <b>в одиниці джерела</b> (ФВ-16.9).
    /// </summary>
    /// <returns>Скільки точок фактично записано.</returns>
    public Task<int> UpsertRawPointsAsync(
        long collectionRunId, int sourceEntityId,
        IReadOnlyList<SourceDataPoint> points, CancellationToken ct);

    /// <summary>Записує покриті інтервали в <c>itg.CollectionCoverage</c>.</summary>
    public Task WriteCoverageAsync(
        long collectionRunId, int sourceEntityId,
        IReadOnlyList<TimeInterval> covered, CancellationToken ct);

    /// <summary>
    /// Записує в журнал покриття ПОДІЮ зі статусом про інтервал, який збір
    /// прочитати не зміг (<c>CollectionCoverage.SourceDataRefused</c>).
    /// </summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourcePath">Атрибут, що відмовив; <c>null</c> — уся сутність.</param>
    /// <param name="fromUtc">Початок непрочитаного інтервалу.</param>
    /// <param name="toUtc">Кінець непрочитаного інтервалу.</param>
    /// <param name="status">Статус події з <c>CollectionCoverage.KnownStatuses</c>.</param>
    /// <param name="errorCode">Код відмови — частина ключа дедуплікації.</param>
    /// <param name="reason">Пояснення конвертом (<c>Q-326</c>) — лягає в <c>Details</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c> — подію записано; <c>false</c> — така сама вже є.</returns>
    /// <remarks>
    /// ⛔ Подія НЕ є покриттям: рядок має статус, а острови покриття й
    /// наздоганяння читають лише <c>Status IS NULL</c> — інтервал лишається
    /// прогалиною і після запису події.
    /// <para>
    /// ⚠ Ідемпотентно: та сама (сутність, атрибут, інтервал, статус, код) на
    /// кожному наступному прогоні нової події не дає — інакше щогодинне
    /// наздоганяння засипало б журнал копіями однієї відмови.
    /// </para>
    /// </remarks>
    public Task<bool> RecordCoverageEventAsync(
        int sourceEntityId, string? sourcePath, DateTime fromUtc, DateTime toUtc,
        string status, string errorCode, JobProgressMessageEnvelope reason, CancellationToken ct);

    /// <summary>
    /// Покриті інтервали від <paramref name="notBefore"/> — основа для пошуку
    /// прогалин. Ознака здоров'я інтеграції — саме журнал покриття, а не тиша (ІНТ-3.3).
    /// </summary>
    public Task<IReadOnlyList<TimeInterval>> GetCoverageAsync(
        int sourceEntityId, DateTime notBefore, CancellationToken ct);

    /// <summary>
    /// Мапінги полів сутності — саме вони несуть <b>оголошену</b> одиницю
    /// джерела.
    /// </summary>
    /// <remarks>
    /// ⚠ Без цього збирач не має з чим порівняти UOM, який джерело повернуло
    /// фактично, — і вимога «зміна UOM атрибута зупиняє збір» (ФВ-16.9,
    /// <c>ECR-INT-0422</c>) лишилася б написаною, але нічиєю.
    /// </remarks>
    public Task<IReadOnlyList<EntityFieldMap>> GetFieldMapsAsync(
        int sourceEntityId, CancellationToken ct);

    /// <summary>
    /// Заводить мапінг поля джерела (<c>ext.EntityFieldMap</c>) і повертає його
    /// зі присвоєним <c>Id</c> (Прогалина 1 директиви паритету: до цього
    /// порту не було жодного шляху ЗАПИСУ — лише статичні фабрики домену й
    /// READ-ONLY перегляд).
    /// </summary>
    public Task<EntityFieldMap> AddFieldMapAsync(EntityFieldMap map, CancellationToken ct);

    /// <summary>
    /// Мапінг за ідентифікатором — <b>відстежуваний</b>; <c>null</c>, якщо
    /// його немає (<c>BE-27</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Саме відстежуваний. Копія без відстеження прийняла б
    /// <c>Pause()</c>/<c>Resume()</c>/<c>AcceptSourceUnitChange()</c> і не
    /// записала б нічого: дія «пройшла б» і зникла.
    ///
    /// ⚠ Призупинені сюди теж потрапляють — інакше відновити мапінг було б
    /// неможливо. Фільтр за <c>IsActive</c> стоїть у
    /// <see cref="GetFieldMapsAsync"/>, тобто на шляху ЗБОРУ, а не на шляху
    /// керування.
    /// </remarks>
    public Task<EntityFieldMap?> FindFieldMapAsync(int fieldMapId, CancellationToken ct);

    /// <summary>
    /// Ставить мапінг на паузу з позначкою «джерело змінило одиницю»
    /// (<c>ФВ-16.9</c>); час позначки — годинник сховища.
    /// </summary>
    /// <param name="fieldMapId">Мапінг.</param>
    /// <param name="actualUnitCode">Одиниця, яку віддає джерело.</param>
    /// <param name="actualUnitId">Її id у довіднику; <c>null</c> — немає.</param>
    /// <param name="ct">Скасування.</param>
    public Task PauseForSourceUnitChangeAsync(
        int fieldMapId, string actualUnitCode, int? actualUnitId, CancellationToken ct);

    /// <summary>Видаляє мапінг остаточно (<c>BE-27</c>).</summary>
    /// <remarks>
    /// ⚠ Видалення фізичне, і це безпечно рівно тому, що викликач пропускає
    /// сюди лише мапінг БЕЗ зібраних даних (<see cref="CountCollectedAsync"/>):
    /// <c>ext.RawDataPoint</c> не посилається на мапінг зовнішнім ключем —
    /// точки адресуються парою «сутність + шлях», — тож м'яке видалення дало
    /// б третій стан («видалений, але видимий») там, де вже є пауза.
    /// </remarks>
    public Task RemoveFieldMapAsync(EntityFieldMap map, CancellationToken ct);

    /// <summary>
    /// Скільки точок джерело вже віддало за полем цього мапінгу і коли —
    /// наслідок видалення (<c>BE-27</c>).
    /// </summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourceField">Поле в джерелі (воно ж <c>SourcePath</c> точки).</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Рахується за парою «сутність + шлях», а не за посиланням на мапінг:
    /// саме цією парою (разом із міткою часу) адресується <c>ext.RawDataPoint</c>.
    /// </remarks>
    public Task<CollectedFieldStats> CountCollectedAsync(
        int sourceEntityId, string sourceField, CancellationToken ct);

    /// <summary>Код одиниці довідника; <c>null</c>, якщо її немає.</summary>
    /// <remarks>
    /// ⚠ Потрібен журналу безпеки: через рік питання буде «з якої одиниці на
    /// яку», і числовий ідентифікатор на нього не відповідає — довідник до
    /// того часу може вже не мати цього рядка.
    /// </remarks>
    public Task<string?> FindUnitCodeAsync(int unitId, CancellationToken ct);

    /// <summary>Колонка-ціль існує і не м'яко видалена.</summary>
    public Task<bool> ColumnDefExistsAsync(int columnDefId, CancellationToken ct);

    /// <summary>
    /// Проєкти, у які може писати мапінг на цю колонку (S3 аудиту безпеки).
    /// </summary>
    /// <remarks>
    /// ⛔ Обидва шляхи в ОДНОМУ запиті: проєкти з екземплярами таблиці колонки
    /// (наявні документи, зокрема на старих версіях шаблону) і проєкти, чия
    /// поточна версія шаблону цю колонку містить (документи, яких ще немає).
    /// Колонки — ідентифікатори версії шаблону, а версію ділять проєкти, тож
    /// один мапінг пише в кожен із них.
    /// </remarks>
    /// <param name="columnDefId">Колонка-ціль.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатори проєктів без повторів; порожньо — колонку ніхто не використовує.</returns>
    public Task<IReadOnlyList<int>> FindProjectIdsUsingColumnAsync(int columnDefId, CancellationToken ct);

    /// <summary>Одиниця межі інтеграції (ФВ-16.9) існує.</summary>
    public Task<bool> UnitExistsAsync(int unitId, CancellationToken ct);

    /// <summary>
    /// Перелік сутностей збору з ознаками здоров'я.
    /// </summary>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Перелік потрібен конфігуратору: імена сутностей джерела <b>обираються
    /// зі списку</b>, а не вводяться руками (ФВ-13.13). Друкарська помилка в
    /// шляху AF виявляється не при налаштуванні, а через місяць порожнім
    /// збором.
    /// <para>
    /// ⚠ Разом із кожною сутністю віддається <b>найстаріша непокрита
    /// прогалина</b>. Ознака здоров'я інтеграції — журнал покриття, а не тиша
    /// (ІНТ-3.3): джерело, яке щоночі успішно віддає нуль точок, і джерело,
    /// яке віддає дані, у переліку останніх прогонів виглядають однаково.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<SourceEntityStatus>> ListSourceEntitiesAsync(CancellationToken ct);

    /// <summary>
    /// У з'єднання вже є сутність збору з цим кодом — активна чи вимкнена
    /// (унікальний індекс <c>UQ_SourceEntity</c> не дивиться на <c>IsActive</c>).
    /// </summary>
    public Task<bool> SourceEntityCodeExistsAsync(int dataSourceId, string code, CancellationToken ct);

    /// <summary>Заводить сутність збору (<c>ФВ-13.11</c>) і повертає її з присвоєним <c>Id</c>.</summary>
    public Task<SourceEntity> AddSourceEntityAsync(SourceEntity entity, CancellationToken ct);

    /// <summary>
    /// Зберігає зміни відстежуваної сутності, прочитаної через
    /// <see cref="FindSourceEntityAsync"/>.
    /// </summary>
    public Task SaveSourceEntityAsync(SourceEntity entity, CancellationToken ct);

    /// <summary>Довідник існує.</summary>
    public Task<bool> RegistryDefExistsAsync(int registryDefId, CancellationToken ct);

    /// <summary>Довідник, якому належить поле; <c>null</c> — поля немає.</summary>
    /// <remarks>
    /// ⚠ Одним запитом відповідає і на «чи є поле», і на «чиє воно» — мапінг
    /// на поле довідника перевіряє обидва (<c>ФВ-8.11</c>).
    /// </remarks>
    public Task<int?> FindRegistryFieldOwnerAsync(int registryFieldDefId, CancellationToken ct);
}

/// <summary>Сутність збору разом зі станом останнього прогону.</summary>
/// <param name="Id">Ідентифікатор сутності.</param>
/// <param name="Code">Код у джерелі.</param>
/// <param name="DisplayName">Підпис для конфігуратора.</param>
/// <param name="EntityPath">Шлях в ієрархії джерела.</param>
/// <param name="Transport">Транспорт джерела (ФВ-11.2).</param>
/// <param name="IsActive">Чи ввімкнено збір.</param>
/// <param name="LastRun">Останній прогін; <c>null</c> — не збирали жодного разу.</param>
/// <param name="OldestGap">Початок найстарішої непокритої прогалини; <c>null</c> — покриття суцільне.</param>
/// <param name="DataSourceId">З'єднання, якому належить сутність.</param>
/// <param name="DataSourceCode">Код цього з'єднання.</param>
/// <param name="OnMissingInSource">Політика синку: зникнення елемента в джерелі (<c>D-212</c>).</param>
/// <param name="ValidFromAttribute">Атрибут початку чинності; <c>null</c> — не синхронізується.</param>
/// <param name="ValidToAttribute">Атрибут кінця чинності; <c>null</c> — не синхронізується.</param>
/// <param name="ValidToInclusive">Кінець у джерелі — останній чинний день.</param>
/// <param name="RegistryDefId">Довідник, до якого прив'язана сутність; <c>null</c> — не прив'язана.</param>
/// <remarks>
/// ⚠ Чотири поля політики синку — обов'язкові: <c>PUT …/registry/policy</c> —
/// повна заміна, і форма мусить стартувати з ЧИННОЇ політики, а не з типової
/// (інакше мовчки затерла б атрибути дат).
/// </remarks>
public sealed record SourceEntityStatus(
    int Id,
    string Code,
    string? DisplayName,
    string? EntityPath,
    string Transport,
    bool IsActive,
    CollectionRunStatus? LastRun,
    DateTime? OldestGap,
    int DataSourceId,
    string DataSourceCode,
    RegistryMissingPolicy OnMissingInSource,
    string? ValidFromAttribute,
    string? ValidToAttribute,
    bool ValidToInclusive,
    int? RegistryDefId = null);

/// <summary>Що джерело вже віддало за одним полем мапінгу (<c>BE-27</c>).</summary>
/// <param name="Points">Скільки точок у <c>ext.RawDataPoint</c>.</param>
/// <param name="FirstAt">Мітка найстарішої точки; <c>null</c> — точок немає.</param>
/// <param name="LastAt">Мітка найсвіжішої точки; <c>null</c> — точок немає.</param>
public sealed record CollectedFieldStats(int Points, DateTime? FirstAt, DateTime? LastAt);

/// <summary>Підсумок прогону збору.</summary>
/// <param name="FinishedAt">Коли завершився; <c>null</c> — ще виконується.</param>
/// <param name="Status">Статус: <c>Succeeded</c>, <c>Degraded</c>, <c>Failed</c>.</param>
/// <param name="PointsRetrieved">Скільки точок отримано.</param>
public sealed record CollectionRunStatus(DateTime? FinishedAt, string Status, int PointsRetrieved);

