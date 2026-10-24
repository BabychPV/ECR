using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents;

/// <summary>Перелік документів. Право <c>Document.View</c>.</summary>
public sealed class ListDocumentsHandler(
    IDocumentStore documents, IAccessDecisionService access, ICurrentUser currentUser,
    IDocumentListSummaryStore samples, Domain.Abstractions.IClock? clock = null)
{
    /// <summary>Право перегляду документів.</summary>
    public const string Permission = "Document.View";

    /// <summary>Повертає сторінку документів, видимих користувачу.</summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі.</param>
    /// <param name="periodKey">Період для зведеного стану; <c>null</c> — без стану.</param>
    /// <param name="state">Зведений стан (<c>Draft|Submitted|Approved|Rejected</c>); порожньо — будь-який.</param>
    /// <param name="mine">Лише документи, де користувач — автор або подавав аркуш.</param>
    /// <param name="hasLateEdits">
    /// Лише документи з (<c>true</c>) або без (<c>false</c>) пізньої правки (<c>BE-09b</c>);
    /// <c>null</c> — без фільтра.
    /// </param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PagedResult<DocumentSummary>> HandleAsync(
        int? projectId, int? periodKey, string? state, bool mine, bool? hasLateEdits,
        CursorRequest page, CancellationToken ct)
        => HandleAsync(projectId, periodKey, state, mine, hasLateEdits, query: null, page, ct);

    /// <summary>Максимальна довжина пошукового запиту; довший обрізається мовчки.</summary>
    public const int MaxQueryLength = 100;

    /// <summary>Те саме, з пошуком <paramref name="query"/> (UI-18) за кодом і назвою документа.</summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі.</param>
    /// <param name="periodKey">Період для зведеного стану; <c>null</c> — без стану.</param>
    /// <param name="state">Зведений стан; порожньо — будь-який.</param>
    /// <param name="mine">Лише документи, де користувач — автор або подавав аркуш.</param>
    /// <param name="hasLateEdits">Фільтр пізніх правок; <c>null</c> — без фільтра.</param>
    /// <param name="query">
    /// Підрядок коду/назви; порожній чи пробіли — без пошуку; довший за
    /// <see cref="MaxQueryLength"/> обрізається. Застосовується ЗАПИТОМ до стелі
    /// сторінки, поруч із межею проєктів, а не постфільтром.
    /// </param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PagedResult<DocumentSummary>> HandleAsync(
        int? projectId, int? periodKey, string? state, bool mine, bool? hasLateEdits,
        string? query, CursorRequest page, CancellationToken ct)
        => HandleAsync(projectId, periodKey, state, mine, hasLateEdits, query, resultsStale: null, staleBy: null, page, ct);

    /// <summary>Допустиме значення <c>staleBy</c>: правки поточного користувача.</summary>
    public const string StaleByMe = "me";

    /// <summary>Те саме, з фільтром застарілих результатів методологій (<c>resultsStale</c>, <c>staleBy=me</c>).</summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі.</param>
    /// <param name="periodKey">Період; для <paramref name="resultsStale"/> обов'язковий.</param>
    /// <param name="state">Зведений стан; порожньо — будь-який.</param>
    /// <param name="mine">Лише документи, де користувач — автор або подавав аркуш.</param>
    /// <param name="hasLateEdits">Фільтр пізніх правок; <c>null</c> — без фільтра.</param>
    /// <param name="query">Пошук за кодом і назвою; порожній — без пошуку.</param>
    /// <param name="resultsStale">
    /// Лише документи зі (<c>true</c>) застарілими або без (<c>false</c>) застарілих результатів; <c>null</c> — без
    /// фільтра. ⛔ Документи проєктів, де читач не бачить хоч щось нижче проєкту, виключаються в обох напрямках;
    /// ✎ D-446 — так само документи-надання учасника, у яких він не бачить хоч щось.
    /// </param>
    /// <param name="staleBy"><c>me</c> — лише застарілі через правки поточного користувача; потребує <paramref name="resultsStale"/> = <c>true</c>.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<PagedResult<DocumentSummary>> HandleAsync(
        int? projectId, int? periodKey, string? state, bool mine, bool? hasLateEdits,
        string? query, bool? resultsStale, string? staleBy, CursorRequest page, CancellationToken ct)
        => HandleAsync(projectId, periodKey, state, mine, hasLateEdits, query, resultsStale, staleBy, ownerUserId: null, page, ct);

    /// <summary>Те саме, з фільтром за власником документа (✎ D-450, <c>ownerUserId</c>).</summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі.</param>
    /// <param name="periodKey">Період; для <paramref name="resultsStale"/> обов'язковий.</param>
    /// <param name="state">Зведений стан; порожньо — будь-який.</param>
    /// <param name="mine">Лише документи, де користувач — автор або подавав аркуш.</param>
    /// <param name="hasLateEdits">Фільтр пізніх правок; <c>null</c> — без фільтра.</param>
    /// <param name="query">Пошук за кодом і назвою; порожній — без пошуку.</param>
    /// <param name="resultsStale">Фільтр застарілих результатів; <c>null</c> — без фільтра.</param>
    /// <param name="staleBy"><c>me</c> — лише застарілі через правки поточного користувача.</param>
    /// <param name="ownerUserId">
    /// Лише документи цього власника (<c>CreatedByUserId</c>); <c>null</c> — будь-якого. Не більше нуля — 422; разом із
    /// <paramref name="mine"/> — 422 (два різні «чиї» в одному запиті суперечать одне одному, а мовчазний вибір одного
    /// з них показав би не той перелік, який людина просила). ⛔ Документи, де ім'я власника читачеві не віддається
    /// (звужений доступ), у фільтр не потрапляють — інакше він розкривав би власника.
    /// </param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<DocumentSummary>> HandleAsync(
        int? projectId, int? periodKey, string? state, bool mine, bool? hasLateEdits,
        string? query, bool? resultsStale, string? staleBy, int? ownerUserId, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var profile = await ProfileAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var staleByMe = ParseStaleBy(staleBy, resultsStale, periodKey);
        ValidateOwner(ownerUserId, mine);
        var filter = new DocumentListFilter(
            ParseState(state, periodKey), mine ? profile.UserId : null, hasLateEdits, Query: NormalizeQuery(query),
            ResultsStale: resultsStale, StaleByUserId: staleByMe ? profile.UserId : null, OwnerUserId: ownerUserId);

        // ⛔ Родина REQ, а не CELL (`P-25`, рядок 1): хибний `limit` — це
        // помилка параметра запиту, і показувати її в обробнику помилок
        // комірки означало б говорити про сітку, якої ще ніхто не відкривав.
        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = CursorRequest.MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⛔ ФВ-6.14: лише проєкти, де є і грант, і саме право перегляду — оператор з областю «A» бачить документи
        // лише A. Перевірка в проєкті — у ТОМУ САМОМУ методі, що й вхідна (сторож `ProjectPermissionCheckTests`).
        var visibleProjects = ReadableProjects(profile, Permission);
        (filter, var scopes, var participant, var bounded) =
            await ReaderBoundsAsync(profile, visibleProjects, projectId, periodKey, filter, ct)
            .ConfigureAwait(false);

        var all = await documents
            .ListAsync(projectId, new PeriodKeyFilter(periodKey), filter, page, visibleProjects, ct)
            .ConfigureAwait(false);

        // ⚠ Постфільтр лишається другим рубежем: перелік документів чужого
        // проєкту — це вже відомості про те, які об'єкти звітують і як часто, і
        // помилка в побудові фільтра запиту не має цього відкривати.
        // ✎ D-443: документ поза проєктами з грантом проходить лише з ДІЮЧИМ наданням (один запит на сторінку).
        var byRoles = all.Items
            .Where(d => profile.SeesDocumentsOf(d.ProjectId) && PermissionCheck.IsGrantedIn(profile, Permission, d.ProjectId))
            .Select(d => d.Id)
            .ToHashSet();
        var others = all.Items.Where(d => !byRoles.Contains(d.Id)).Select(d => d.Id).ToList();
        var participated = participant && others.Count > 0
            ? await access.ParticipatedDocumentsAsync(profile, others, ct).ConfigureAwait(false)
            : new HashSet<long>();
        // ✎ D-446: і лише з ПОРАХОВАНИМИ межами (другий рубіж до `DocumentParticipationFilter.DocumentIds` у запиті):
        // надання, що з'явилось між читанням меж і запитом, видно з наступним запитом, а не з позначками без меж.
        var visible = all.Items
            .Where(d => byRoles.Contains(d.Id)
                        || (participated.Contains(d.Id) && bounded.Contains(d.Id) && !profile.DeniesProject(d.ProjectId)))
            .ToList();

        // ⛔ Аркуші, схована від читача (Deny / звуження ролі аркушами), не називаються ні кодом,
        // ні назвою, ні станом, ні лічильниками: фільтр — у `DocumentSheetVisibility`.
        visible = await DocumentSheetVisibility.ApplyAsync(access, profile, visible, periodKey, scopes, ct).ConfigureAwait(false);

        // ⛔ `all.TotalCount` НЕ проводиться далі як є — саме це й було дірою:
        // обробник не може перевірити, що число зі сховища враховує гранти, а
        // «1 з 5000» розкриває, скільки документів у проєктах, до яких доступу
        // немає. Точна кількість віддається лише тоді, коли вона справді відома
        // з цієї сторінки: запит починався з початку послідовності й вона
        // вичерпана. Інакше — `null`, «підрахунок недоступний» (контракт
        // `PagedResult.TotalCount`), а не правдоподібне неправильне число.
        var total = page.Cursor is null && all.NextCursor is null ? visible.Count : (int?)null;

        return new PagedResult<DocumentSummary>(visible, all.NextCursor, total);
    }

    /// <summary>
    /// Межі читача для запиту переліку: проєкти з грантом і правом, пари схованого (лише за ролями) і D-443 документи-надання
    /// з порахованими межами. ✎ D-450: винесено з переліку, щоб вибір власників (<see cref="HandleOwnersAsync"/>) ішов
    /// РІВНО тим самим шляхом видимості, а не власним, ширшим.
    /// </summary>
    /// <remarks>
    /// <c>visibleProjects</c> — проєкти з грантом і правом (<see cref="ReadableProjects(AccessProfile, string)"/>) —
    /// рахує викликач, у тому самому методі, що й вхідну перевірку (ФВ-6.14).
    /// </remarks>
    private async Task<(DocumentListFilter Filter, IReadOnlyDictionary<int, DocumentReadScope>? Scopes,
        bool Participant, HashSet<long> Bounded)> ReaderBoundsAsync(
        AccessProfile profile, HashSet<int> visibleProjects, int? projectId, int? periodKey, DocumentListFilter filter,
        CancellationToken ct)
    {
        // ⛔ Гранти йдуть у ЗАПИТ, не лише в постфільтр (аудит 2026-09-16, §3.3).
        // Постфільтр сам по собі давав дві діри: `TotalCount` рахувався по ВСІХ
        // проєктах системи — користувач з грантом на один проєкт бачив, скільки
        // документів у чужих, — а сторінка віддавала менше за `page.Limit`
        // видимих елементів, поки `NextCursor` вказував далі в НЕфільтрованій
        // послідовності.
        // ⛔ ФВ-6.14: `visibleProjects` — лише проєкти, де є і грант, і саме право
        // перегляду (рахує викликач).

        // ⛔ Фільтр за станом рахується лише по аркушах, які читач бачить: інакше `state=Rejected`
        // знаходить документ, відхилений схованим аркушем, — оракул (смуга зведення цього не показує).
        // ⛔ Так само позначка і фільтр `hasLateEdits` не враховують пізні правки схованих колонок (R-7)
        // (✎ D-446: у документі-наданні — власних меж учасника, див. нижче).
        // ⛔ N1-01: межі ідуть ПАРАМИ «проєкт, Id» (версія шаблону спільна для кількох проєктів).
        // ⛔ N1-02: межі рахуються лише для `projectId` (якщо задано) і ті самі йдуть в `ApplyAsync` нижче —
        // без другого кола `ReadScopeAsync` по тих самих проєктах.
        // ✎ D-443: межі-фільтри цілого проєкту — лише за РОЛЯМИ: надання учасника відкриває один документ, і
        // його межі не поширюються на інші документи проєкту (інакше фільтр за станом знаходив би їх за аркушем,
        // відкритим лише в документі-наданні).
        var roleProfile = profile.WithoutParticipations();
        (filter, var scopes) = await DocumentSheetVisibility
            .HiddenFilterAsync(samples, access, roleProfile, visibleProjects, projectId, periodKey, filter, ct)
            .ConfigureAwait(false);

        // ✎ D-443: документи, відкриті наданням учасника, — у тому самому запиті, поза проєктами із забороною.
        // ✎ D-444 (AN-131): фільтр СТАНУ документи-надання додає — з їхніми власними межами (пари «документ, аркуш»,
        // `DocumentSheetVisibility.ParticipantScopesAsync`), тими самими, що смуга зведення: лічильник стану над
        // таблицею і таблиця під фільтром цього стану не розходяться.
        // ✎ D-446: так само фільтри й позначки пізніх правок і застарілих результатів (раніше документи-надання з
        // ними мовчки не додавались, а позначка пізніх правок була завжди `false`):
        //  • пізні правки — у ВЛАСНИХ межах учасника (пари «документ, аркуш/таблиця/колонка»), тим самим предикатом
        //    сховища (BE-09b), що й для пар проєкту; картка рахує ті самі межі (R-7, `HasVisibleLateEditsAsync`);
        //  • застарілість — по ВСЬОМУ документу: видимий учасникові цілком документ — у фільтрі як звичайний, частково
        //    схований — поза фільтром в обох напрямках (рядок — `null`, `DocumentSheetVisibility.For`).
        //  • поза проєктами з грантом — лише документи з ПОРАХОВАНИМИ межами (`DocumentIds`, закрито за замовчуванням).
        // ✎ D-446: документ проєкту з роллю, що має ще й надання, звужується і парами проєкту (лише роль), і власними —
        // свідомо, як фільтр стану D-444: лише недолік позначки (надання, що відкриває схований роллю аркуш), не витік.
        var participant = profile.HasDocumentParticipations && clock is not null;
        HashSet<long> bounded = [];
        if (participant)
        {
            // ⚠ Без періоду — межі без періоду (недійсний ключ 0), як і картка без періоду: позначка пізніх правок
            // тоді — за будь-який період.
            var own = (await DocumentSheetVisibility.ParticipantScopesAsync(access, profile, periodKey ?? 0, ct)
                    .ConfigureAwait(false))
                .OrderBy(s => s.Key)
                .ToList();
            bounded = [.. own.Select(s => s.Key)];

            filter = filter with
            {
                Participation = new DocumentParticipationFilter(
                    profile.UserId, clock!.UtcNow, profile.DeniedProjectIds(),
                    HiddenSheets: [.. own.SelectMany(s => s.Value.HiddenSheetIds().Select(id => (s.Key, id)))],
                    HiddenTables: [.. own.SelectMany(s => s.Value.HiddenTableIds().Select(id => (s.Key, id)))],
                    HiddenColumns: [.. own.SelectMany(s => s.Value.HiddenColumnIds().Select(id => (s.Key, id)))],
                    NarrowedDocumentIds: filter.ResultsStale is null
                        ? null
                        : [.. own.Where(s => DocumentSheetVisibility.IsProjectNarrowed(s.Value)).Select(s => s.Key)],
                    DocumentIds: [.. bounded.Order()]),
            };
        }

        return (filter, scopes, participant, bounded);
    }

    /// <summary>Стеля кількості людей у виборі власника (✎ D-450): далі — пошук за іменем.</summary>
    public const int MaxOwners = 50;

    /// <summary>
    /// ✎ D-450: різні власники документів, які читач бачить, — для вибору у фільтрі «Власник». Право <c>Document.View</c>.
    /// </summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі видимі.</param>
    /// <param name="periodKey">Період, у межах якого рахуються межі читача (як у переліку); <c>null</c> — без періоду.</param>
    /// <param name="query">Підрядок імені; порожній — без пошуку; довший за <see cref="MaxQueryLength"/> обрізається.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Видимість — тим самим шляхом, що й перелік (<see cref="ReaderBoundsAsync"/> + базовий запит сховища): проєкти
    /// з роллю і D-443 документи-надання з порахованими межами. Окремий «простіший» запит назвав би власників документів,
    /// яких читач не бачить, — оракул «хто працює в чужому проєкті». Документи, де читачеві закрито хоч щось, не
    /// рахуються: ім'я власника там не віддається і в рядку переліку.
    /// </remarks>
    public async Task<Dto.DocumentOwnersResponse> HandleOwnersAsync(
        int? projectId, int? periodKey, string? query, CancellationToken ct)
    {
        var profile = await ProfileAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        // ⛔ ФВ-6.14: вхід «хоч у якомусь проєкті» — і тут же перевірка в проєкті: лише проєкти з грантом і правом.
        var visibleProjects = ReadableProjects(profile, Permission);
        var (filter, _, _, _) = await ReaderBoundsAsync(profile, visibleProjects, projectId, periodKey, default, ct)
            .ConfigureAwait(false);

        var owners = await documents
            .ListOwnersAsync(projectId, filter, visibleProjects, NormalizeQuery(query), MaxOwners, ct)
            .ConfigureAwait(false);

        return new Dto.DocumentOwnersResponse(owners);
    }

    /// <summary>
    /// ✎ D-450: <c>ownerUserId</c> — додатне ціле й не разом із <c>mine</c>; інакше 422 тієї самої родини REQ, що й
    /// решта параметрів переліку.
    /// </summary>
    private static void ValidateOwner(int? ownerUserId, bool mine)
    {
        if (ownerUserId is null)
        {
            return;
        }

        if (ownerUserId <= 0)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "ownerUserId має бути додатним цілим.",
                // ⚠ Без ехо значення в деталях: каталог його не підставляє, а людина знає, що надіслала (T1-01,
                // сторож `DenyLeakGuardTests` — Deny-контекст файлу).
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.ownerUserId" });
        }

        if (mine)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "mine і ownerUserId разом не приймаються: оберіть одне.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.ownerWithMine" });
        }
    }

    /// <summary>Обрізає пробіли й довжину; порожній пошук — <c>null</c> (без фільтра).</summary>
    internal static string? NormalizeQuery(string? query)
    {
        var trimmed = query?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > MaxQueryLength ? trimmed[..MaxQueryLength] : trimmed;
    }

    /// <summary>
    /// Розбір <c>resultsStale</c>/<c>staleBy</c>: фільтр без періоду, невідоме <c>staleBy</c> і <c>staleBy</c> без
    /// <c>resultsStale=true</c> — 422 (мовчазне «усі» читалося б як «застарілих немає»).
    /// </summary>
    /// <returns><c>true</c> — лише правки поточного користувача.</returns>
    private static bool ParseStaleBy(string? staleBy, bool? resultsStale, int? periodKey)
    {
        if (resultsStale is not null && periodKey is null)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Фільтр застарілих результатів потребує періоду.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.resultsStaleNeedsPeriod" });
        }

        if (string.IsNullOrWhiteSpace(staleBy))
        {
            return false;
        }

        if (!string.Equals(staleBy.Trim(), StaleByMe, StringComparison.OrdinalIgnoreCase) || resultsStale != true)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"staleBy приймає лише «{StaleByMe}» і разом із resultsStale=true.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.staleBy",
                    ["staleBy"] = staleBy,
                });
        }

        return true;
    }

    /// <summary>Фільтр стану: порожньо — без фільтра; невідоме ім'я або стан без періоду — 422.</summary>
    /// <remarks>
    /// ⛔ Звірка з ІМЕНАМИ, не <c>Enum.TryParse</c>: той прийняв би «2» і «99».
    /// Невідомий стан — відмова, а не мовчазне «усі»: порожній перелік на
    /// друкарську помилку читався б як «таких документів немає».
    /// </remarks>
    private static DocumentStatus? ParseState(string? state, int? periodKey)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        var known = Enum.GetValues<DocumentStatus>()
            .Cast<DocumentStatus?>()
            .FirstOrDefault(v => string.Equals(v.ToString(), state.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Стану документа «{state}» не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.documentState",
                    ["state"] = state,
                });

        // Стан документа поза періодом не визначений (`D-93`).
        return periodKey is null
            ? throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                "Фільтр стану потребує періоду.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-REQ-0422.documentStateNeedsPeriod" })
            : known;
    }

    /// <summary>Проєкти, на які є грант читання (ключі <c>Project:{id}</c>).</summary>
    /// <remarks>
    /// Гранти в профілі вже розгорнуті <c>Project → Sheet → Table → Column</c>,
    /// тож набір тут ТОЧНО той самий, що перевіряє
    /// <see cref="AccessProfile.LevelFor"/> — заборони включно: рівень беремо
    /// через <c>LevelFor</c>, а не з <c>Grants</c> напряму.
    /// </remarks>
    internal static HashSet<int> ReadableProjects(AccessProfile profile)
    {
        var prefix = $"{ResourceKind.Project}:";
        var ids = new HashSet<int>();

        foreach (var key in profile.Grants.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(
                    key.AsSpan(prefix.Length),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var id))
            {
                continue;
            }

            if (profile.LevelFor(ResourceKind.Project, id) >= GrantLevel.Read)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Проєкти з грантом читання, у яких є ще й проєктне право
    /// <paramref name="permission"/> (ФВ-6.14).
    /// </summary>
    /// <param name="profile">Профіль.</param>
    /// <param name="permission">Проєктне право (напр. <c>Document.View</c>).</param>
    /// <remarks>
    /// ⚠ D-214: і проєкти, де документ відкриває лише роль, звужена аркушами
    /// чи періодами (<see cref="AccessProfile.SeesDocumentsOf"/>) — перелік
    /// документів належить рівню документа.
    /// </remarks>
    internal static HashSet<int> ReadableProjects(AccessProfile profile, string permission)
    {
        var ids = VisibleProjects(profile);
        ids.RemoveWhere(id => !PermissionCheck.IsGrantedIn(profile, permission, id));
        return ids;
    }

    /// <summary>
    /// Проєкти, документи яких профіль БАЧИТЬ (<see cref="AccessProfile.SeesDocumentsOf"/>), без
    /// жодного проєктного права — для шляхів, де видимий документ без права дає <c>403</c> із причиною,
    /// а невидимий відсутній (B-08).
    /// </summary>
    /// <param name="profile">Профіль.</param>
    internal static HashSet<int> VisibleProjects(AccessProfile profile)
    {
        var ids = ReadableProjects(profile);
        ids.UnionWith(profile.Scoped.Keys.Where(profile.SeesDocumentsOf));
        return ids;
    }

    /// <summary>
    /// Профіль користувача з ВХІДНОЮ перевіркою проєктного права — бодай у
    /// якомусь проєкті (ФВ-6.14).
    /// </summary>
    /// <remarks>
    /// ⛔ Викликач зобов'язаний далі перевірити право в проєкті ресурсу
    /// (<see cref="AccessProfile.Has(string, int)"/>) — сторож
    /// <c>ProjectPermissionCheckTests</c> вимагає цього в тому самому методі.
    /// </remarks>
    internal static Task<AccessProfile> ProfileAsync(
        IAccessDecisionService access, ICurrentUser currentUser, string permission, CancellationToken ct)
        => PermissionCheck.RequireInAnyProjectAsync(access, currentUser, permission, ct);
}

/// <summary>Документ і стан його аркушів. Право <c>Document.View</c>.</summary>
public sealed class GetDocumentHandler(
    IDocumentStore documents, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає документ; <c>null</c> — не існує або невидимий.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період для стану аркушів.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DocumentSummary?> HandleAsync(
        long documentId, int? periodKey, CancellationToken ct)
    {
        var profile = await ListDocumentsHandler
            .ProfileAsync(access, currentUser, ListDocumentsHandler.Permission, ct)
            .ConfigureAwait(false);

        var document = await documents
            .FindAsync(documentId, new PeriodKeyFilter(periodKey), ct)
            .ConfigureAwait(false);

        // ⚠ Документ без гранта віддається як «не знайдено», а не «заборонено».
        // Різниця між 403 і 404 тут сама по собі є відомістю: за нею видно,
        // які документи існують у проєктах, доступу до яких немає.
        // ⛔ ФВ-6.14: без права перегляду В ЦЬОМУ проєкті — так само невидимий.
        if (document is null)
        {
            return null;
        }

        // ✎ D-443: без ролі на проєкт документ видно лише з ДІЮЧИМ наданням учасника саме в ньому (і без заборони
        // проєкту) — `RequireInDocumentAsync` робить цю перевірку одним запитом лише для профілю-учасника.
        if (!profile.SeesDocumentsOf(document.ProjectId)
            || !PermissionCheck.IsGrantedIn(profile, ListDocumentsHandler.Permission, document.ProjectId))
        {
            if (!profile.HasDocumentParticipations
                || profile.DeniesProject(document.ProjectId)
                || !await access.ParticipatesAsync(profile, document.Id, ct).ConfigureAwait(false))
            {
                return null;
            }
        }

        // ⛔ Приховані від читача аркуші не потрапляють у склад, стан і лічильники картки.
        var narrowed = (await DocumentSheetVisibility
            .ApplyAsync(access, profile, [document], periodKey, known: null, ct).ConfigureAwait(false))[0];

        // ⛔ R-7: позначка пізніх правок — лише по тому, що читач бачить.
        // ✎ D-446: для документа-надання `ReadScopeAsync` дає ВЛАСНІ межі учасника — ті самі, з якими перелік рахує
        // позначку й фільтр `hasLateEdits` (`ParticipantScopesAsync`), тож картка й рядок переліку не розходяться.
        if (narrowed.HasLateEdits && DocumentSheetVisibility.HasRestrictions(profile))
        {
            var scope = (await DocumentSheetVisibility
                .ScopesAsync(access, profile, [(document.ProjectId, document.Id)], periodKey, ct)
                .ConfigureAwait(false))[document.ProjectId];

            narrowed = narrowed with
            {
                HasLateEdits = await documents
                    .HasVisibleLateEditsAsync(
                        document.Id, new PeriodKeyFilter(periodKey), DocumentSheetVisibility.HiddenOf(scope, document.ProjectId), ct)
                    .ConfigureAwait(false),
            };
        }

        // EA-C: рішення сервера для пункту меню «Доступ до редагування»; автор береться з сховища, а не з
        // OwnerUserId відповіді (той null для звуженого читача).
        return narrowed with
        {
            CanManageEditAccess = EditAssignments.EditAssignmentAccess.CanManage(
                profile, document.ProjectId, document.OwnerUserId ?? 0, document.ProjectIsReadOnly),
        };
    }
}
