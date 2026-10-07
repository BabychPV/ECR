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
    /// Ключі опублікованих версій методологій, прив'язаних до цільової версії, що посилаються на колонки
    /// ІНШОЇ версії того самого шаблону (правила <c>MatchJson</c> і обов'язкові входи).
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
}
