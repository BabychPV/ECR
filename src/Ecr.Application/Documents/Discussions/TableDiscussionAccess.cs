// src/Ecr.Application/Documents/Discussions/TableDiscussionAccess.cs
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Documents.EditAssignments;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.Discussions;

/// <summary>Хто й до чого в чаті таблиці має доступ у цьому запиті.</summary>
/// <param name="Profile">Профіль користувача.</param>
/// <param name="UserId">Користувач.</param>
/// <param name="Scope">Межі читання документа.</param>
/// <param name="AllowedPeriods">
/// <c>null</c> — таблиця читається в УСІХ періодах; інакше — лише ці періоди (роль, звужена періодами, D-214).
/// </param>
internal sealed record TableChatAccess(
    AccessProfile Profile, int UserId, DocumentReadScope Scope, IReadOnlyCollection<int>? AllowedPeriods)
{
    /// <summary>Чи видимий період.</summary>
    public bool CanSeePeriod(int periodKey) => AllowedPeriods is null || AllowedPeriods.Contains(periodKey);
}

/// <summary>Правила тексту, лімітів і доступу чату таблиці.</summary>
/// <remarks>
/// ⛔ Читання — те саме рішення, що й зріз таблиці: документ видимий (<c>Document.View</c> у проєкті), таблиця читається
/// межею читання (<see cref="DocumentReadScope"/>). Схована таблиця й невидимий документ — ОДНА відповідь <c>404</c>
/// (<see cref="DocumentVisibility.DocumentNotFound"/>), без оракула існування розмови.
/// </remarks>
internal static class TableDiscussionAccess
{
    /// <summary>Скільки згадок у повідомленні.</summary>
    public const int MaxMentions = 10;

    /// <summary>Ключ відмови: текст/період/згадки недійсні.</summary>
    public const string BodyInvalidKey = "err.ECR-REQ-0422.discussionBodyInvalid";

    /// <summary>Читає доступ до чату таблиці; кидає 404 для невидимого документа/таблиці.</summary>
    public static async Task<TableChatAccess> ResolveAsync(
        IAccessDecisionService access, ICurrentUser currentUser, ITableDiscussionStore store,
        long documentId, string tableCode, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, "Document.View", ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, "Document.View", ct).ConfigureAwait(false);

        var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(tableCode) || tableCode.Length > TableDiscussionRules.MaxCodeLength)
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        if (scope.CanReadTableCode(tableCode))
        {
            return new TableChatAccess(profile, currentUser.UserId!.Value, scope, null);
        }

        if (!scope.KnowsTableCode(tableCode))
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        // Роль, звужена періодами: таблиця видима лише в частині періодів документа. Профіль без такого звуження
        // (і без наданих учаснику) сюди не ходить: «не читається зараз» для нього означає «схована у всіх».
        if (!MayBePeriodRestricted(profile))
        {
            throw DocumentVisibility.DocumentNotFound(documentId);
        }

        var periods = await store.DocumentPeriodsAsync(documentId, ct).ConfigureAwait(false);
        var allowed = periods
            .Where(p => new PeriodKey(p).IsValid && scope.CanReadTableCodeIn(tableCode, new PeriodKey(p)))
            .ToList();

        return allowed.Count == 0
            ? throw DocumentVisibility.DocumentNotFound(documentId)
            : new TableChatAccess(profile, currentUser.UserId!.Value, scope, allowed);
    }

    /// <summary>Чи може профіль бути обмежений ПЕРІОДОМ (звужена роль D-214 або надання учасника документа).</summary>
    public static bool MayBePeriodRestricted(AccessProfile profile)
        => profile.HasDocumentParticipations
           || profile.Scoped.Values.Any(s => s.Narrowed.Any(l => l.PeriodFrom is not null || l.PeriodTo is not null));

    /// <summary>Чи має користувач <c>Document.Discuss</c> у проєкті документа; не кидає.</summary>
    public static async Task<bool> CanDiscussAsync(
        IAccessDecisionService access, AccessProfile profile, long documentId, CancellationToken ct)
    {
        try
        {
            await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, "Document.Discuss", ct).ConfigureAwait(false);
            return true;
        }
        catch (AccessDeniedException)
        {
            return false;
        }
    }

    /// <summary>Чи має користувач <c>Document.DiscussModerate</c> у проєкті документа; не кидає.</summary>
    public static async Task<bool> CanModerateAsync(
        IAccessDecisionService access, AccessProfile profile, long documentId, CancellationToken ct)
    {
        try
        {
            await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, "Document.DiscussModerate", ct).ConfigureAwait(false);
            return true;
        }
        catch (AccessDeniedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Лише ті з запитаних користувачів, які самі бачать таблицю в цьому періоді; решта (і неіснуючі, і вимкнені) мовчки
    /// відкидаються — відповідь не відрізняє «немає такого» від «не бачить таблицю».
    /// </summary>
    public static async Task<IReadOnlyList<int>> FilterMentionsAsync(
        IAccessDecisionService access, long documentId, string tableCode, int periodKey,
        IReadOnlyCollection<int>? requested, int authorUserId, CancellationToken ct)
    {
        if (requested is null || requested.Count == 0)
        {
            return [];
        }

        var period = new PeriodKey(periodKey);
        var result = new List<int>();

        foreach (var userId in requested.Where(u => u > 0 && u != authorUserId).Distinct().Take(MaxMentions))
        {
            if (await CanSeeAsync(access, userId, documentId, tableCode, period, ct).ConfigureAwait(false))
            {
                result.Add(userId);
            }
        }

        return result;
    }

    /// <summary>Чи бачить користувач таблицю в періоді (документ видимий, межа читання).</summary>
    public static async Task<bool> CanSeeAsync(
        IAccessDecisionService access, int userId, long documentId, string tableCode, PeriodKey? period, CancellationToken ct)
    {
        var profile = await EditAssignmentAccess.TryBuildProfileAsync(access, userId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return false;
        }

        try
        {
            await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, "Document.View", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AccessDeniedException or NotFoundException)
        {
            return false;
        }

        var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        return period is { } p ? scope.CanReadTableCodeIn(tableCode, p) : scope.CanReadTableCode(tableCode);
    }

    public static NotFoundException MessageNotFound(long documentId) => DocumentVisibility.DocumentNotFound(documentId);

    public static BusinessRuleException BodyInvalid(string message)
        => EditAssignmentAccess.Invalid(
            BodyInvalidKey, message,
            new Dictionary<string, object?>
            {
                ["max"] = TableDiscussionMessage.MaxBodyLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxMentions"] = MaxMentions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    public static AccessDeniedException Forbidden(string messageKey, string message)
        => new(ErrorCodes.AccessDenied, message, new Dictionary<string, object?> { ["messageKey"] = messageKey });
}

/// <summary>Нормалізація тексту й межі чату таблиці.</summary>
internal static class TableDiscussionRules
{
    /// <summary>Довжина коду таблиці (як <c>TableDef.Code</c>).</summary>
    public const int MaxCodeLength = 64;

    /// <summary>Сторінка за замовчуванням і стеля.</summary>
    public const int DefaultPage = 50;

    /// <summary>Стеля сторінки.</summary>
    public const int MaxPage = 100;

    /// <summary>
    /// Простий текст: переноси рядків нормалізуються до <c>\n</c>; прибираються керівні символи (крім <c>\n</c> і табуляції),
    /// символи керування двонапрямним текстом (підміна напрямку), нульової ширини й нескінченні пробіли по краях.
    /// </summary>
    /// <returns>Нормалізований текст або <c>null</c>, якщо він порожній чи довший за стелю.</returns>
    public static string? Normalize(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        var sb = new StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\r')
            {
                if (i + 1 < body.Length && body[i + 1] == '\n')
                {
                    i++;
                }

                sb.Append('\n');
                continue;
            }

            if (c is '\n' or '\t')
            {
                sb.Append(c);
                continue;
            }

            if (char.IsControl(c) || IsInvisibleOrDirectional(c))
            {
                continue;
            }

            sb.Append(c);
        }

        var text = sb.ToString().Trim();
        return text.Length is 0 or > TableDiscussionMessage.MaxBodyLength ? null : text;
    }

    /// <summary>Символи керування напрямком тексту, нульової ширини й роздільники рядків/абзаців (підміна вигляду тексту).</summary>
    private static bool IsInvisibleOrDirectional(char c)
        => (c >= 0x202A && c <= 0x202E)
           || (c >= 0x2066 && c <= 0x2069)
           || c is (char)0x200B or (char)0x200C or (char)0x200D or (char)0x2060 or (char)0xFEFF or (char)0x2028 or (char)0x2029;
    /// <summary>Розмір сторінки: порожній — за замовчуванням, поза межами — вужчий допустимий.</summary>
    public static int PageSize(int? limit) => Math.Clamp(limit ?? DefaultPage, 1, MaxPage);
}
