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
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<DocumentSummary>> HandleAsync(
        int? projectId, int? periodKey, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var profile = await ProfileAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

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
        var visibleProjects = ReadableProjects(profile);

        var all = await documents
            .ListAsync(projectId, new PeriodKeyFilter(periodKey), page, visibleProjects, ct)
            .ConfigureAwait(false);

        // ⚠ Постфільтр лишається другим рубежем: перелік документів чужого
        // проєкту — це вже відомості про те, які об'єкти звітують і як часто, і
        // помилка в побудові фільтра запиту не має цього відкривати.
        var visible = all.Items
            .Where(d => profile.LevelFor(ResourceKind.Project, d.ProjectId) >= GrantLevel.Read)
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

    /// <summary>Профіль користувача з перевіркою функціонального права.</summary>
    internal static async Task<AccessProfile> ProfileAsync(
        IAccessDecisionService access, ICurrentUser currentUser, string permission, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(permission))
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {permission}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = permission,
                });
        }

        return profile;
    }
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
        return document is null
               || profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Read
            ? null
            : document;
    }
}
