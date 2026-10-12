using Ecr.Application.Documents.VersionMigration;

namespace Ecr.Application.Ports;

/// <summary>Що лежить у документах проєкту — вхід планувальника переносу (ФВ-7.5).</summary>
/// <param name="DocumentIds">Документи проєкту.</param>
/// <param name="Cells">Непорожні значення за колонками й описами рядків поточної версії.</param>
/// <param name="HeaderValues">Непорожні значення шапки за полями поточної версії.</param>
/// <param name="LockedSheets">Скільки пар «аркуш × період» подано або затверджено.</param>
public sealed record VersionMigrationScope(
    IReadOnlyList<long> DocumentIds,
    IReadOnlyList<VersionMigrationCellUsage> Cells,
    IReadOnlyDictionary<int, long> HeaderValues,
    int LockedSheets);

/// <summary>Мапінг збору, що адресує колонку вихідної версії шаблону (D-1 RC20).</summary>
/// <param name="MappingId">Мапінг (<c>ext.EntityFieldMap.Id</c>).</param>
/// <param name="ColumnDefId">Колонка вихідної версії, на яку він вказує.</param>
/// <param name="IsActive"><c>false</c> — призупинений (збір за ним нічого не пише).</param>
/// <param name="ColumnPath">Шлях колонки за кодами <c>SHEET.TABLE.COLUMN</c> (джерело й поле не розкриваються).</param>
public sealed record CollectionMappingRef(int MappingId, int ColumnDefId, bool IsActive, string ColumnPath);

/// <summary>Поле мапінгу подій «атрибут → колонка» (D-1b RC20).</summary>
/// <param name="FieldId"><c>ext.SourceEventFieldMap.Id</c>.</param>
/// <param name="ColumnDefId">Колонка-ціль.</param>
public sealed record EventFieldRef(int FieldId, int ColumnDefId);

/// <summary>Мапінг подій джерела на динамічну таблицю документа (D-1b RC20).</summary>
/// <param name="MapId"><c>ext.SourceEventMap.Id</c>.</param>
/// <param name="SourceEntityId">Сутність-шаблон подій.</param>
/// <param name="DocumentId">Документ ділянки.</param>
/// <param name="TableDefId">Таблиця-ціль.</param>
/// <param name="IsActive"><c>false</c> — призупинений.</param>
/// <param name="Fields">Поля мапінгу.</param>
public sealed record EventMapRef(
    int MapId, int SourceEntityId, long DocumentId, int TableDefId, bool IsActive, IReadOnlyList<EventFieldRef> Fields);

/// <summary>Прив'язка «атрибут → колонка, вікно = рядок» (D-1b RC20).</summary>
/// <param name="MapId"><c>ext.RowWindowMap.Id</c>.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="TargetColumnDefId">Колонка-ціль.</param>
/// <param name="StartColumnDefId">Початок вікна.</param>
/// <param name="EndColumnDefId">Кінець вікна.</param>
/// <param name="SelectorColumnDefId">Селектор; <c>null</c> — немає.</param>
/// <param name="IsActive"><c>false</c> — призупинена.</param>
public sealed record RowWindowMapRef(
    int MapId, int TableDefId, int TargetColumnDefId, int StartColumnDefId, int EndColumnDefId,
    int? SelectorColumnDefId, bool IsActive);

/// <summary>Нова колонка поля мапінгу подій.</summary>
/// <param name="FieldId">Поле.</param>
/// <param name="ColumnDefId">Колонка цільової версії.</param>
public sealed record EventFieldRemap(int FieldId, int ColumnDefId);

/// <summary>Куди переїжджає мапінг подій.</summary>
/// <param name="MapId">Мапінг.</param>
/// <param name="TableDefId">Таблиця цільової версії.</param>
/// <param name="Fields">Нові колонки його полів.</param>
public sealed record EventMapRemap(int MapId, int TableDefId, IReadOnlyList<EventFieldRemap> Fields);

/// <summary>Куди переїжджає прив'язка вікна.</summary>
/// <param name="MapId">Прив'язка.</param>
/// <param name="TableDefId">Таблиця цільової версії.</param>
/// <param name="TargetColumnDefId">Колонка-ціль.</param>
/// <param name="StartColumnDefId">Початок вікна.</param>
/// <param name="EndColumnDefId">Кінець вікна.</param>
/// <param name="SelectorColumnDefId">Селектор.</param>
public sealed record WindowMapRemap(
    int MapId, int TableDefId, int TargetColumnDefId, int StartColumnDefId, int EndColumnDefId, int? SelectorColumnDefId);

/// <summary>Рішення плану: які мапінги подій і вікон переносяться (D-1b RC20).</summary>
/// <param name="Events">Мапінги подій.</param>
/// <param name="Windows">Прив'язки вікон.</param>
public sealed record ExtendedMappingRemap(IReadOnlyList<EventMapRemap> Events, IReadOnlyList<WindowMapRemap> Windows)
{
    /// <summary>Нічого переносити.</summary>
    public static ExtendedMappingRemap Empty { get; } = new([], []);
}

/// <summary>Користувачі, чиї профілі в кеші треба скинути після переносу.</summary>
/// <param name="Ids">Користувачі (не більше стелі).</param>
/// <param name="Overflow">Користувачів більше за стелю: перелік неповний, скидати треба ВЕСЬ кеш.</param>
public sealed record GrantedUsers(IReadOnlyList<int> Ids, bool Overflow);

/// <summary>Сховище переносу документів проєкту на нову версію шаблону (ФВ-7.5).</summary>
public interface IDocumentVersionMigrationStore
{
    /// <summary>
    /// Блокує рядок проєкту до кінця транзакції й повертає його поточну версію
    /// шаблону; <c>null</c> — проєкту немає.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише всередині транзакції: без неї блок знімається одразу, і два
    /// переноси того самого проєкту розійшлися б з планом, який перевіряли.
    /// </remarks>
    public Task<int?> LockProjectVersionAsync(int projectId, CancellationToken ct);

    /// <summary>Документи проєкту за зростанням <c>Id</c> — порядок, у якому перенос блокує їхню структуру.</summary>
    public Task<IReadOnlyList<long>> ListDocumentIdsAsync(int projectId, CancellationToken ct);

    /// <summary>Рахує, що лежить у документах проєкту.</summary>
    public Task<VersionMigrationScope> ReadScopeAsync(int projectId, CancellationToken ct);

    /// <summary>
    /// Скільки грантів (будь-яких: заборона й дозвіл) стоїть на перелічених аркушах,
    /// таблицях і колонках вихідної версії — тих, що новій версії нема куди скопіювати.
    /// </summary>
    /// <remarks>
    /// ⚠ Не лише <c>IsDeny</c>: дозвіл нижчого рівня на ресурсі (Read під Write проєкту)
    /// теж звужує доступ — береться найдрібніший рівень, — тож його втрата розширює доступ.
    /// </remarks>
    public Task<int> CountDenyGrantsAsync(
        IReadOnlyCollection<int> sheetIds, IReadOnlyCollection<int> tableIds, IReadOnlyCollection<int> columnIds,
        CancellationToken ct);

    /// <summary>
    /// Скільки АКТИВНИХ прив'язок методологій до колонок вихідної версії не переїде: колонка не має
    /// відповідника в цільовій версії (<paramref name="columnMap"/>) або на відповідній колонці немає
    /// активної прив'язки тієї ж методології й виходу (D-13).
    /// </summary>
    /// <param name="sourceVersionId">Вихідна версія шаблону.</param>
    /// <param name="columnMap">Колонка вихідної версії → колонка цільової (з плану).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Прив'язка живе на методології й адресується колонкою, а ідентифікатори колонок у версій свої: після
    /// переносу прогін шукає прив'язки по колонках ЦІЛЬОВОЇ версії, і без відповідника обчислювана колонка
    /// лишилася б порожньою без жодної помилки.
    /// </remarks>
    public Task<int> CountUnmappedBindingsAsync(
        int sourceVersionId, IReadOnlyDictionary<int, int> columnMap, CancellationToken ct);

    /// <summary>
    /// Мапінги збору (<c>ext.EntityFieldMap</c>), що адресують колонки вихідної версії шаблону (D-1 RC20).
    /// </summary>
    /// <param name="sourceVersionId">Вихідна версія шаблону.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Мапінг не має ані проєкту, ані власної версії: він адресує колонку числовим <c>ColumnDefId</c> конкретної
    /// версії шаблону, і матеріалізація шукає екземпляри таблиць саме за цією колонкою. Після переносу проєкту
    /// на іншу версію мапінг, що лишився на колонці старої, нічого не пише (збір «успішний», а комірки порожні).
    /// </remarks>
    public Task<IReadOnlyList<CollectionMappingRef>> ListCollectionMappingsAsync(int sourceVersionId, CancellationToken ct);

    /// <summary>
    /// Переносить мапінги збору з колонок вихідної версії на колонки цільової за мапою переносу (D-1 RC20).
    /// </summary>
    /// <param name="columnMap">Колонка вихідної версії → колонка цільової (з плану).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки мапінгів перенесено.</returns>
    /// <remarks>
    /// ⚠ Лише всередині транзакції переносу, після <see cref="LockProjectVersionAsync"/>: мапінги й документи
    /// перемикаються разом, інакше збір писав би за мапінгом, чия колонка вже не відповідає версії проєкту.
    /// Чіпає лише мапінги на колонку (<c>TargetKind = Column</c>) з ключем у мапі — решту (довідники, колонки
    /// інших версій) не зачіпає.
    /// </remarks>
    public Task<int> RemapCollectionMappingsAsync(IReadOnlyDictionary<int, int> columnMap, CancellationToken ct);

    /// <summary>
    /// Мапінги подій джерела (<c>ext.SourceEventMap</c> із полями) документів проєкту (D-1b RC20).
    /// </summary>
    /// <param name="documentIds">Документи проєкту, що переноситься (мапінг належить конкретному документу).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Лише мапінги ЦИХ документів: мапінги інших проєктів не читаються й не змінюються.
    /// </remarks>
    public Task<IReadOnlyList<EventMapRef>> ListEventMapsAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct);

    /// <summary>
    /// Прив'язки «вікно = рядок» (<c>ext.RowWindowMap</c>) на таблицях обох версій шаблону (D-1b RC20).
    /// </summary>
    /// <param name="tableDefIds">Таблиці вихідної й цільової версій (цільові потрібні для виявлення зайнятої цілі).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RowWindowMapRef>> ListRowWindowMapsAsync(IReadOnlyCollection<int> tableDefIds, CancellationToken ct);

    /// <summary>
    /// Переносить мапінги подій і вікон на таблиці/колонки цільової версії, а провенанс підтягувань
    /// (<c>ext.RowWindowValue</c>) проєкту — на нові колонки (D-1b RC20).
    /// </summary>
    /// <param name="projectId">Проєкт, що переноситься (провенанс — лише його екземплярів таблиць).</param>
    /// <param name="remap">Що переносити (рішення плану).</param>
    /// <param name="columnMap">Колонка вихідної версії → колонка цільової (з плану).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Лише всередині транзакції переносу, після <c>ApplyAsync</c>. Складені ключі <c>FK_RWM_*</c> —
    /// (TableDefId, ColumnDefId): таблиця й усі колонки міняються ОДНИМ оператором.
    /// </remarks>
    public Task RemapEventAndWindowMapsAsync(
        int projectId, ExtendedMappingRemap remap, IReadOnlyDictionary<int, int> columnMap, CancellationToken ct);

    /// <summary>
    /// Ключі опублікованих версій методологій, прив'язаних до цільової версії, що посилаються на колонки
    /// ІНШОЇ версії того самого шаблону (правила <c>MatchJson</c> і обов'язкові входи) і НЕ мають відповідника за
    /// шляхом (аркуш/таблиця/колонка) у цільовій версії (C1: з відповідником ключ перекладається при читанні).
    /// </summary>
    /// <param name="targetVersionId">Цільова версія шаблону.</param>
    /// <param name="limit">Стеля переліку (решта не потрібна: відмова й так одна).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Підписи «методологія версія правило|вхід аркуш.таблиця.колонка» чужої колонки; порожньо — все гаразд.</returns>
    /// <remarks>
    /// ⚠ <c>MatchJson</c>/<c>ColumnDefId</c> правила — ідентифікатори колонок конкретної версії шаблону, а версія
    /// методології обирається за датою, не за версією шаблону. Після переносу документ шукає ключі серед колонок
    /// ЦІЛЬОВОЇ версії: предикат не збігається ніколи (прогін нічого не рахує), а вимога <c>Block</c> блокує
    /// збереження. Чужими вважаються лише колонки версій ТОГО САМОГО шаблону — методологія може обслуговувати
    /// й інші шаблони. Активні прив'язки, активні правила, версії з <c>EffectiveFrom</c> (опубліковані й виведені).
    /// </remarks>
    public Task<IReadOnlyList<string>> ListForeignMethodologyKeysAsync(
        int targetVersionId, int limit, CancellationToken ct);

    /// <summary>
    /// Користувачі з ПРЯМИМ призначенням ролі, яка має гранти на аркуші/таблиці/колонки
    /// вихідної версії, що переносяться: їхні профілі в кеші треба скинути після коміту.
    /// </summary>
    /// <param name="plan">План переносу.</param>
    /// <param name="limit">Стеля переліку; перевищення — <see cref="GrantedUsers.Overflow"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<GrantedUsers> ListUsersWithGrantsAsync(VersionMigrationPlan plan, int limit, CancellationToken ct);

    /// <summary>
    /// Переносить дані всіх документів проєкту за планом і перемикає версію
    /// проєкту. Викликається лише в транзакції, після
    /// <see cref="LockProjectVersionAsync"/>.
    /// </summary>
    public Task ApplyAsync(int projectId, int targetVersionId, VersionMigrationPlan plan, CancellationToken ct);

    /// <summary>
    /// Те саме, що <see cref="ApplyAsync(int, int, VersionMigrationPlan, CancellationToken)"/>, і після кожної
    /// команди (пачки) повідомляє про стадію й кількість змінених рядків.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="targetVersionId">Цільова версія.</param>
    /// <param name="plan">План переносу.</param>
    /// <param name="onStep">Приймач прогресу; <c>null</c> — без прогресу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Типова реалізація прогрес ігнорує (обгортки у тестах, що знають лише чотириаргументний виклик):
    /// фонова задача тоді просто не бачить кроків усередині транзакції.
    /// </remarks>
    public Task ApplyAsync(
        int projectId, int targetVersionId, VersionMigrationPlan plan, VersionMigrationStepReporter? onStep, CancellationToken ct)
        => ApplyAsync(projectId, targetVersionId, plan, ct);
}
