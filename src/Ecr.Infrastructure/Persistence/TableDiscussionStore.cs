// src/Ecr.Infrastructure/Persistence/TableDiscussionStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ITableDiscussionStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⛔ Торкається лише <c>doc.TableDiscussion*</c> (і читає імена з <c>sec.User</c>, періоди з <c>doc.TableInstance</c>).
/// Жодного запису в комірки, розрахунки чи стани. Блокування — <c>UPDLOCK, HOLDLOCK</c> на ключі зведення: двоє, що
/// одночасно пишуть у ще не існуючий чат, серіалізуються діапазонним замком, а ревізія зростає без втрат.
/// </remarks>
public sealed class TableDiscussionStore(EcrDbContext db) : ITableDiscussionStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TableDiscussionSummaryRow>> SummaryAsync(
        long documentId, IReadOnlyCollection<string> fullCodes,
        IReadOnlyDictionary<string, IReadOnlyCollection<int>> periodRestricted, int userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fullCodes);
        ArgumentNullException.ThrowIfNull(periodRestricted);

        var full = fullCodes.ToList();

        // Таблиці з повним доступом: зведення (всього, останнє) + два корельованих підзапити по індексу чату.
        var fullQuery =
            from s in db.TableDiscussions.AsNoTracking()
            where s.DocumentId == documentId && s.MessageCount > 0 && full.Contains(s.TableDefCode)
            let lastRead = db.TableDiscussionReads
                .Where(r => r.UserId == userId && r.DocumentId == documentId && r.TableDefCode == s.TableDefCode)
                .Select(r => r.LastReadMessageId).FirstOrDefault()
            select new SummaryShape
            {
                Code = s.TableDefCode,
                Total = s.MessageCount,
                Unread = db.TableDiscussionMessages.Count(m =>
                    m.DocumentId == documentId && m.TableDefCode == s.TableDefCode && m.Id > lastRead
                    && m.AuthorUserId != userId && m.DeletedAt == null),
                LastAt = s.LastMessageAt,
                LastId = s.LastMessageId,
                Mentioned = db.TableDiscussionMentions.Any(x =>
                    x.UserId == userId && x.DocumentId == documentId && x.TableDefCode == s.TableDefCode),
            };

        if (periodRestricted.Count == 0)
        {
            return ToRows(await fullQuery.ToListAsync(ct).ConfigureAwait(false));
        }

        // Таблиці, видимі лише в частині періодів: рахуємо з повідомлень дозволених періодів (та сама команда: UNION ALL).
        var codes = periodRestricted.Keys.ToList();
        var pairs = periodRestricted
            .SelectMany(p => p.Value.Select(period => p.Key + "|" + period.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

#pragma warning disable CA1305 // ToString() транслюється EF у CAST(... AS nvarchar), культура не береться
        var narrowedQuery =
            from m in db.TableDiscussionMessages.AsNoTracking()
            where m.DocumentId == documentId && m.DeletedAt == null && codes.Contains(m.TableDefCode)
                  && pairs.Contains(m.TableDefCode + "|" + m.PeriodKey.ToString())
            join r in db.TableDiscussionReads.Where(r => r.UserId == userId && r.DocumentId == documentId)
                on m.TableDefCode equals r.TableDefCode into reads
            from rd in reads.DefaultIfEmpty()
            group new { m.Id, m.AuthorUserId, m.CreatedAt, Read = rd == null ? 0L : rd.LastReadMessageId } by m.TableDefCode
            into g
            select new SummaryShape
            {
                Code = g.Key,
                Total = g.Count(),
                Unread = g.Count(x => x.Id > x.Read && x.AuthorUserId != userId),
                LastAt = g.Max(x => (DateTime?)x.CreatedAt),
                LastId = g.Max(x => x.Id),
                Mentioned = db.TableDiscussionMentions.Any(x => x.UserId == userId && x.DocumentId == documentId
                    && x.TableDefCode == g.Key
                    && db.TableDiscussionMessages.Any(mm => mm.Id == x.MessageId && mm.DeletedAt == null
                        && pairs.Contains(mm.TableDefCode + "|" + mm.PeriodKey.ToString()))),
            };

#pragma warning restore CA1305

        return ToRows(await fullQuery.Concat(narrowedQuery).ToListAsync(ct).ConfigureAwait(false));
    }

    private static List<TableDiscussionSummaryRow> ToRows(List<SummaryShape> shapes)
        => [.. shapes.Select(s => new TableDiscussionSummaryRow(s.Code, s.Total, s.Unread, s.LastAt, s.LastId, s.Mentioned))];

    /// <summary>Форма рядка підсумку для SQL: об'єднання (UNION ALL) вимагає однакової проєкції в обох запитах.</summary>
    private sealed class SummaryShape
    {
        public string Code { get; init; } = null!;

        public int Total { get; init; }

        public int Unread { get; init; }

        public DateTime? LastAt { get; init; }

        public long LastId { get; init; }

        public bool Mentioned { get; init; }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> DocumentPeriodsAsync(long documentId, CancellationToken ct)
        => await db.TableInstances.AsNoTracking()
            .Where(i => i.DocumentId == documentId)
            .Select(i => i.PeriodKeyValue)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TableDiscussionStateRow> GetStateAsync(long documentId, string tableCode, CancellationToken ct)
    {
        var row = await db.TableDiscussions.AsNoTracking()
            .Where(s => s.DocumentId == documentId && s.TableDefCode == tableCode)
            .Select(s => new TableDiscussionStateRow(s.Revision, s.LastMessageId))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return row ?? new TableDiscussionStateRow(0, 0);
    }

    /// <inheritdoc />
    public async Task<long> GetLastReadAsync(int userId, long documentId, string tableCode, CancellationToken ct)
        => await db.TableDiscussionReads.AsNoTracking()
            .Where(r => r.UserId == userId && r.DocumentId == documentId && r.TableDefCode == tableCode)
            .Select(r => r.LastReadMessageId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TableDiscussionMessageRow>> ListAsync(
        long documentId, string tableCode, long? before, long? after, int? periodKey,
        IReadOnlyCollection<int>? allowedPeriods, int take, CancellationToken ct)
    {
        var query = db.TableDiscussionMessages.AsNoTracking()
            .Where(m => m.DocumentId == documentId && m.TableDefCode == tableCode);

        if (allowedPeriods is not null)
        {
            var allowed = allowedPeriods.ToList();
            query = query.Where(m => allowed.Contains(m.PeriodKey));
        }

        if (periodKey is { } period)
        {
            query = query.Where(m => m.PeriodKey == period);
        }

        query = after is { } afterId
            ? query.Where(m => m.Id > afterId).OrderBy(m => m.Id)
            : (before is { } beforeId ? query.Where(m => m.Id < beforeId) : query).OrderByDescending(m => m.Id);

        return await (
            from m in query.Take(take)
            join u in db.Users.AsNoTracking() on m.AuthorUserId equals u.Id
            select new TableDiscussionMessageRow(
                m.Id, m.TableDefCode, m.PeriodKey, m.AuthorUserId, u.DisplayName, m.Body,
                m.CreatedAt, m.EditedAt, m.DeletedAt))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<int>>> MentionsAsync(
        IReadOnlyCollection<long> messageIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<int>>();
        }

        var ids = messageIds.ToList();
        var rows = await db.TableDiscussionMentions.AsNoTracking()
            .Where(x => ids.Contains(x.MessageId))
            .Select(x => new { x.MessageId, x.UserId })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.GroupBy(x => x.MessageId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(x => x.UserId).Order()]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> UserNamesAsync(IReadOnlyCollection<int> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var ids = userIds.Distinct().ToList();
        return await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TableDiscussionMentionRow>> MyMentionsAsync(int userId, int take, CancellationToken ct)
        => await (
            from x in db.TableDiscussionMentions.AsNoTracking()
            where x.UserId == userId
            join m in db.TableDiscussionMessages.AsNoTracking() on x.MessageId equals m.Id
            where m.DeletedAt == null
            join u in db.Users.AsNoTracking() on m.AuthorUserId equals u.Id
            orderby m.Id descending
            select new TableDiscussionMentionRow(
                m.Id, m.DocumentId, m.TableDefCode, m.PeriodKey, m.AuthorUserId, u.DisplayName, m.Body, m.CreatedAt))
            .Take(take)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<TableDiscussionState> LockStateAsync(long documentId, string tableCode, CancellationToken ct)
    {
        var state = await db.TableDiscussions
            .FromSql($"SELECT * FROM doc.TableDiscussion WITH (UPDLOCK, HOLDLOCK) WHERE DocumentId = {documentId} AND TableDefCode = {tableCode}")
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (state is null)
        {
            state = new TableDiscussionState(documentId, tableCode);
            db.TableDiscussions.Add(state);
        }

        return state;
    }

    /// <inheritdoc />
    public Task<TableDiscussionMessage?> FindMessageForUpdateAsync(
        long documentId, string tableCode, long messageId, CancellationToken ct)
        => db.TableDiscussionMessages
            .FromSql($"SELECT * FROM doc.TableDiscussionMessage WITH (UPDLOCK, ROWLOCK) WHERE Id = {messageId} AND DocumentId = {documentId} AND TableDefCode = {tableCode}")
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public void Add(TableDiscussionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        db.TableDiscussionMessages.Add(message);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ReplaceMentionsAsync(
        TableDiscussionMessage message, IReadOnlyCollection<int> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(userIds);

        var wanted = userIds.Distinct().ToHashSet();
        var existing = await db.TableDiscussionMentions
            .Where(x => x.MessageId == message.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        db.TableDiscussionMentions.RemoveRange(existing.Where(x => !wanted.Contains(x.UserId)));

        var have = existing.Select(x => x.UserId).ToHashSet();
        var added = wanted.Where(u => !have.Contains(u)).Order().ToList();
        db.TableDiscussionMentions.AddRange(added.Select(
            u => new TableDiscussionMention(message.Id, u, message.DocumentId, message.TableDefCode)));

        return added;
    }

    /// <inheritdoc />
    public Task RemoveMentionsAsync(long messageId, CancellationToken ct)
        => db.TableDiscussionMentions.Where(x => x.MessageId == messageId).ExecuteDeleteAsync(ct);

    /// <inheritdoc />
    public Task<DateTime?> LastLiveAtAsync(long documentId, string tableCode, CancellationToken ct)
        => db.TableDiscussionMessages.AsNoTracking()
            .Where(m => m.DocumentId == documentId && m.TableDefCode == tableCode && m.DeletedAt == null)
            .OrderByDescending(m => m.Id)
            .Select(m => (DateTime?)m.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public async Task MarkReadAsync(int userId, long documentId, string tableCode, long upTo, CancellationToken ct)
    {
        var read = await db.TableDiscussionReads
            .FromSql($"SELECT * FROM doc.TableDiscussionRead WITH (UPDLOCK, HOLDLOCK) WHERE UserId = {userId} AND DocumentId = {documentId} AND TableDefCode = {tableCode}")
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (read is null)
        {
            db.TableDiscussionReads.Add(new TableDiscussionRead(userId, documentId, tableCode, upTo));
        }
        else
        {
            read.Advance(upTo);
        }

        await db.TableDiscussionMentions
            .Where(x => x.UserId == userId && x.DocumentId == documentId && x.TableDefCode == tableCode && x.MessageId <= upTo)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}
