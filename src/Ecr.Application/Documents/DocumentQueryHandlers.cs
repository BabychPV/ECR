using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents;

/// <summary>Перелік документів. Право <c>Document.View</c>.</summary>
public sealed class ListDocumentsHandler(
    IDocumentStore documents, IAccessDecisionService access, ICurrentUser currentUser)
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
    public async Task<PagedResult<DocumentSummary>> HandleAsync(
        int? projectId, int? periodKey, string? state, bool mine, bool? hasLateEdits,
        CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var profile = await ProfileAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var filter = new DocumentListFilter(ParseState(state, periodKey), mine ? profile.UserId : null, hasLateEdits);

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

        // ⛔ Гранти йдуть у ЗАПИТ, не лише в постфільтр (аудит 2026-09-16, §3.3).
        // Постфільтр сам по собі давав дві діри: `TotalCount` рахувався по ВСІХ
        // проєктах системи — користувач з грантом на один проєкт бачив, скільки
        // документів у чужих, — а сторінка віддавала менше за `page.Limit`
        // видимих елементів, поки `NextCursor` вказував далі в НЕфільтрованій
        // послідовності.
        // ⛔ ФВ-6.14: лише проєкти, де є і грант, і саме право перегляду —
        // оператор з областю «A» бачить документи лише A.
        var visibleProjects = ReadableProjects(profile, Permission);

        var all = await documents
            .ListAsync(projectId, new PeriodKeyFilter(periodKey), filter, page, visibleProjects, ct)
            .ConfigureAwait(false);

        // ⚠ Постфільтр лишається другим рубежем: перелік документів чужого
        // проєкту — це вже відомості про те, які об'єкти звітують і як часто, і
        // помилка в побудові фільтра запиту не має цього відкривати.
        var visible = all.Items
            .Where(d => profile.SeesDocumentsOf(d.ProjectId) && PermissionCheck.IsGrantedIn(profile, Permission, d.ProjectId))
            .ToList();

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
        var ids = ReadableProjects(profile);
        ids.UnionWith(profile.Scoped.Keys.Where(profile.SeesDocumentsOf));
        ids.RemoveWhere(id => !PermissionCheck.IsGrantedIn(profile, permission, id));
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
        return document is null
               || !profile.SeesDocumentsOf(document.ProjectId)
               || !PermissionCheck.IsGrantedIn(profile, ListDocumentsHandler.Permission, document.ProjectId)
            ? null
            : document;
    }
}
