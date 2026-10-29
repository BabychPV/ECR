// src/Ecr.Application/Documents/Discussions/TableDiscussionQueries.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Documents.EditAssignments;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.Discussions;

/// <summary>Читання чатів таблиць: підсумок документа, сторінка повідомлень, кандидати у згадки, мої згадки.</summary>
/// <remarks>
/// ⛔ Доступ — те саме рішення, що й зріз таблиці (<see cref="TableDiscussionAccess"/>). Схована таблиця — 404.
/// Роль, звужена періодами, бачить лише повідомлення видимих їй періодів; той самий предикат — у підсумку,
/// «непрочитаному» й згадках.
/// </remarks>
public sealed class TableDiscussionQueries(
    IAccessDecisionService access,
    ICurrentUser currentUser,
    ITableDiscussionStore store,
    IEditAssignmentManagementStore candidateSource,
    IClock clock)
{

    /// <summary>Підсумок чатів усіх таблиць документа: ОДНА команда до таблиць чату.</summary>
    public async Task<TableDiscussionSummaryDto> GetSummaryAsync(long documentId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, "Document.View", ct).ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, "Document.View", ct).ConfigureAwait(false);
        var userId = currentUser.UserId!.Value;

        var scope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        var codes = scope.TableCodes();
        var full = codes.Where(scope.CanReadTableCode).ToList();
        var restricted = new Dictionary<string, IReadOnlyCollection<int>>(StringComparer.Ordinal);

        var hidden = codes.Except(full, StringComparer.Ordinal).ToList();
        if (hidden.Count > 0 && TableDiscussionAccess.MayBePeriodRestricted(profile))
        {
            var periods = (await store.DocumentPeriodsAsync(documentId, ct).ConfigureAwait(false))
                .Where(p => new PeriodKey(p).IsValid).ToList();

            foreach (var code in hidden)
            {
                var allowed = periods.Where(p => scope.CanReadTableCodeIn(code, new PeriodKey(p))).ToList();
                if (allowed.Count > 0)
                {
                    restricted[code] = allowed;
                }
            }
        }

        var rows = full.Count == 0 && restricted.Count == 0
            ? []
            : await store.SummaryAsync(documentId, full, restricted, userId, ct).ConfigureAwait(false);

        var canWrite = await TableDiscussionAccess.CanDiscussAsync(access, profile, documentId, ct).ConfigureAwait(false);

        return new TableDiscussionSummaryDto(
            [.. rows.OrderBy(r => r.TableCode, StringComparer.Ordinal).Select(r => new TableDiscussionSummaryItemDto(
                r.TableCode, r.Total, r.Unread, r.LastAt, r.LastMessageId, r.MentionedMe))],
            canWrite);
    }

    /// <summary>
    /// Сторінка повідомлень чату таблиці. <paramref name="ifNoneMatch"/> збігся з ревізією — <c>Page = null</c> (304).
    /// </summary>
    public async Task<(TableDiscussionPageDto? Page, string ETag)> ListMessagesAsync(
        long documentId, string tableCode, long? before, long? after, int? limit, int? periodKey,
        string? ifNoneMatch, CancellationToken ct)
    {
        var chat = await TableDiscussionAccess
            .ResolveAsync(access, currentUser, store, documentId, tableCode, ct).ConfigureAwait(false);

        if ((before is not null && after is not null)
            || (before is < 0) || (after is < 0)
            || (periodKey is { } p && !new PeriodKey(p).IsValid))
        {
            throw TableDiscussionAccess.BodyInvalid("Недійсні межі сторінки чату.");
        }

        var state = await store.GetStateAsync(documentId, tableCode, ct).ConfigureAwait(false);
        var lastRead = await store.GetLastReadAsync(chat.UserId, documentId, tableCode, ct).ConfigureAwait(false);
        var canWrite = await TableDiscussionAccess.CanDiscussAsync(access, chat.Profile, documentId, ct).ConfigureAwait(false);
        var canModerate = await TableDiscussionAccess.CanModerateAsync(access, chat.Profile, documentId, ct).ConfigureAwait(false);

        var size = TableDiscussionRules.PageSize(limit);
        var etag = string.Create(
            CultureInfo.InvariantCulture,
            $"\"tc-{state.Revision}-{lastRead}-{(canWrite ? 'w' : '-')}{(canModerate ? 'm' : '-')}-{before}-{after}-{size}-{periodKey}\"");

        if (IsNotModified(ifNoneMatch, etag))
        {
            return (null, etag);
        }

        var rows = await store
            .ListAsync(documentId, tableCode, before, after, periodKey, chat.AllowedPeriods,
                after is null ? size + 1 : size, ct)
            .ConfigureAwait(false);

        var hasMore = false;
        if (after is null)
        {
            hasMore = rows.Count > size;
            rows = [.. rows.Take(size).Reverse()];
        }

        var messages = await MapAsync(rows, chat.UserId, canModerate, ct).ConfigureAwait(false);

        return (new TableDiscussionPageDto(
            messages, hasMore, state.Revision, state.LastMessageId, lastRead, canWrite, canModerate), etag);
    }

    /// <summary>Кандидати у згадки: лише користувачі, які бачать таблицю (в періоді, якщо заданий).</summary>
    public async Task<IReadOnlyList<TableDiscussionMentionCandidateDto>> MentionCandidatesAsync(
        long documentId, string tableCode, string? q, int? periodKey, CancellationToken ct)
    {
        var chat = await TableDiscussionAccess
            .ResolveAsync(access, currentUser, store, documentId, tableCode, ct).ConfigureAwait(false);
        await DocumentVisibility
            .RequireVisibleAsync(access, chat.Profile, documentId, "Document.Discuss", ct).ConfigureAwait(false);

        var query = q?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length < 2)
        {
            return [];
        }

        if (periodKey is { } pk && (!new PeriodKey(pk).IsValid || !chat.CanSeePeriod(pk)))
        {
            return [];
        }

        PeriodKey? period = periodKey is { } v ? new PeriodKey(v) : null;
        var result = new List<TableDiscussionMentionCandidateDto>();

        // Страничний перебір: кандидатів без доступу відсіює CanSee, тож береться більше, ніж потрібно.
        for (var skip = 0; skip < 200 && result.Count < 20; skip += 40)
        {
            var page = await candidateSource.SearchCandidatesAsync(query, skip, 40, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var candidate in page)
            {
                if (candidate.Id == chat.UserId || result.Count >= 20)
                {
                    continue;
                }

                if (await TableDiscussionAccess
                        .CanSeeAsync(access, candidate.Id, documentId, tableCode, period, ct).ConfigureAwait(false))
                {
                    result.Add(new TableDiscussionMentionCandidateDto(candidate.Id, candidate.DisplayName));
                }
            }

            if (page.Count < 40)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Непрочитані згадки поточного користувача в таблицях і періодах, які він досі бачить.</summary>
    public async Task<TableDiscussionMentionTasksDto> MyMentionsAsync(int? limit, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new Errors.AccessDeniedException(
                         "ECR-AUTH-0401", "Потрібна автентифікація.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var size = TableDiscussionRules.PageSize(limit);
        var rows = await store.MyMentionsAsync(userId, size * 3, ct).ConfigureAwait(false);

        var scopes = new Dictionary<long, DocumentReadScope?>();
        var items = new List<TableDiscussionMentionTaskDto>();

        foreach (var row in rows)
        {
            if (items.Count >= size)
            {
                break;
            }

            if (!scopes.TryGetValue(row.DocumentId, out var scope))
            {
                var read = await access.CanReadDocumentAsync(profile, row.DocumentId, ct).ConfigureAwait(false);
                scope = read.IsAllowed
                    ? await access.ReadScopeAsync(profile, row.DocumentId, ct).ConfigureAwait(false)
                    : null;
                scopes[row.DocumentId] = scope;
            }

            var period = new PeriodKey(row.PeriodKey);
            if (scope is null || !period.IsValid || !scope.CanReadTableCodeIn(row.TableCode, period))
            {
                continue;
            }

            items.Add(new TableDiscussionMentionTaskDto(
                row.MessageId, row.DocumentId, row.TableCode, row.PeriodKey, row.AuthorUserId, row.AuthorName,
                Snippet(row.Body), Utc(row.CreatedAt)));
        }

        return new TableDiscussionMentionTasksDto(items);
    }

    // ── спільне ─────────────────────────────────────────────────────────────────────

    /// <summary>Будує DTO повідомлень: імена згаданих, права читача на кожне.</summary>
    internal async Task<IReadOnlyList<TableDiscussionMessageDto>> MapAsync(
        IReadOnlyList<TableDiscussionMessageRow> rows, int viewerId, bool canModerate, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var mentions = await store.MentionsAsync([.. rows.Select(r => r.Id)], ct).ConfigureAwait(false);
        var names = await store
            .UserNamesAsync([.. mentions.Values.SelectMany(v => v).Distinct()], ct).ConfigureAwait(false);

        return [.. rows.Select(r => Map(r, viewerId, canModerate, clock.UtcNow, mentions, names))];
    }

    internal static TableDiscussionMessageDto Map(
        TableDiscussionMessageRow row, int viewerId, bool canModerate, DateTime now,
        IReadOnlyDictionary<long, IReadOnlyList<int>> mentions, IReadOnlyDictionary<int, string> names)
    {
        var deleted = row.DeletedAt is not null;
        var editableUntil = row.CreatedAt + TableDiscussionMessage.EditWindow;
        var canEdit = !deleted && row.AuthorUserId == viewerId && now <= editableUntil;

        var mentioned = deleted || !mentions.TryGetValue(row.Id, out var ids)
            ? []
            : ids.Where(names.ContainsKey).Select(u => new TableDiscussionMentionDto(u, names[u])).ToList();

        return new TableDiscussionMessageDto(
            row.Id, row.TableCode, row.PeriodKey, row.AuthorUserId, row.AuthorName,
            deleted ? null : row.Body, deleted, Utc(row.CreatedAt), Utc(row.EditedAt), Utc(row.DeletedAt),
            canEdit, canEdit ? Utc(editableUntil) : null,
            !deleted && (row.AuthorUserId == viewerId || canModerate), mentioned);
    }

    internal static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    private static string Snippet(string body)
    {
        var flat = body.Replace('\n', ' ').Replace('\t', ' ');
        return flat.Length <= 160 ? flat : flat[..160];
    }

    /// <summary>Чи збігся <c>If-None-Match</c> з ETag (список, слабка форма, <c>*</c>).</summary>
    internal static bool IsNotModified(string? header, string etag)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        foreach (var raw in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = raw.StartsWith("W/", StringComparison.Ordinal) ? raw[2..] : raw;
            if (candidate == "*" || string.Equals(candidate, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
