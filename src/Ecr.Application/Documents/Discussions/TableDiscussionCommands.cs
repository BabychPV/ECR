// src/Ecr.Application/Documents/Discussions/TableDiscussionCommands.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.Discussions;

/// <summary>Запис у чат таблиці: нове повідомлення, правка, м'яке видалення, «прочитано».</summary>
/// <remarks>
/// ⛔ Пише лише в <c>doc.TableDiscussion*</c> і журнал безпеки. Комірки, розрахунки, стани аркушів і перерахунок не
/// торкаються. Дозволено в будь-якому стані документа (коментар — не дані). Зведення чату (<c>Revision</c>, лічильник)
/// оновлюється в ТІЙ САМІЙ транзакції, що й повідомлення.
/// </remarks>
public sealed class TableDiscussionCommands(
    IAccessDecisionService access,
    ICurrentUser currentUser,
    ITableDiscussionStore store,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock,
    TableDiscussionQueries queries)
{

    /// <summary>Тип події журналу: повідомлення написано.</summary>
    public const string PostedEvent = "TableDiscussionMessagePosted";

    /// <summary>Тип події журналу: повідомлення відредаговано.</summary>
    public const string EditedEvent = "TableDiscussionMessageEdited";

    /// <summary>Тип події журналу: повідомлення видалено (текст зберігається тут).</summary>
    public const string DeletedEvent = "TableDiscussionMessageDeleted";

    /// <summary>Пише повідомлення.</summary>
    public async Task<TableDiscussionMessageDto> PostAsync(
        long documentId, string tableCode, PostTableDiscussionMessageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chat = await TableDiscussionAccess
            .ResolveAsync(access, currentUser, store, documentId, tableCode, ct).ConfigureAwait(false);
        await DocumentVisibility
            .RequireVisibleAsync(access, chat.Profile, documentId, "Document.Discuss", ct).ConfigureAwait(false);

        var body = TableDiscussionRules.Normalize(request.Body);
        if (body is null || !new PeriodKey(request.PeriodKey).IsValid
            || (request.MentionUserIds?.Count ?? 0) > TableDiscussionAccess.MaxMentions)
        {
            throw TableDiscussionAccess.BodyInvalid("Текст повідомлення, період або згадки недійсні.");
        }

        if (!chat.CanSeePeriod(request.PeriodKey))
        {
            throw TableDiscussionAccess.Forbidden(
                "err.ECR-ACCS-0403.discussionPeriodForbidden", "Ваш доступ не охоплює цей період.");
        }

        var mentions = await TableDiscussionAccess
            .FilterMentionsAsync(access, documentId, tableCode, request.PeriodKey, request.MentionUserIds, chat.UserId, ct)
            .ConfigureAwait(false);

        var now = clock.UtcNow;
        var message = new TableDiscussionMessage(documentId, tableCode, request.PeriodKey, chat.UserId, body, now);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var state = await store.LockStateAsync(documentId, tableCode, innerCt).ConfigureAwait(false);

            store.Add(message);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false); // ідентифікатор — з бази

            state.OnPosted(message.Id, now);
            await store.ReplaceMentionsAsync(message, mentions, innerCt).ConfigureAwait(false);

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now, PostedEvent, null, null,
                    JsonSerializer.Serialize(new
                    {
                        documentId, tableCode, messageId = message.Id, periodKey = request.PeriodKey,
                        length = body.Length, mentions,
                    }),
                    chat.UserId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return await MapOneAsync(message, chat, ct).ConfigureAwait(false);
    }

    /// <summary>Правка свого повідомлення в межах вікна.</summary>
    public async Task<TableDiscussionMessageDto> EditAsync(
        long documentId, string tableCode, long messageId, EditTableDiscussionMessageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chat = await TableDiscussionAccess
            .ResolveAsync(access, currentUser, store, documentId, tableCode, ct).ConfigureAwait(false);
        await DocumentVisibility
            .RequireVisibleAsync(access, chat.Profile, documentId, "Document.Discuss", ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        TableDiscussionMessage? edited = null;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var message = await store.FindMessageForUpdateAsync(documentId, tableCode, messageId, innerCt).ConfigureAwait(false);
            if (message is null || !chat.CanSeePeriod(message.PeriodKey))
            {
                throw TableDiscussionAccess.MessageNotFound(documentId);
            }

            if (message.IsDeleted)
            {
                throw Rule("err.ECR-REQ-0422.discussionMessageDeleted", "Повідомлення видалено.");
            }

            if (message.AuthorUserId != chat.UserId)
            {
                throw TableDiscussionAccess.Forbidden(
                    "err.ECR-ACCS-0403.discussionEditForbidden", "Правити повідомлення може лише автор.");
            }

            if (!message.IsWithinEditWindow(now))
            {
                throw Rule(
                    "err.ECR-REQ-0422.discussionEditWindowClosed", "Вікно правки закрито.",
                    new Dictionary<string, object?>
                    {
                        ["minutes"] = ((int)TableDiscussionMessage.EditWindow.TotalMinutes)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                    });
            }

            var body = TableDiscussionRules.Normalize(request.Body);
            if (body is null || (request.MentionUserIds?.Count ?? 0) > TableDiscussionAccess.MaxMentions)
            {
                throw TableDiscussionAccess.BodyInvalid("Текст повідомлення або згадки недійсні.");
            }

            var state = await store.LockStateAsync(documentId, tableCode, innerCt).ConfigureAwait(false);
            var oldBody = message.Body;
            message.Edit(body, now);
            state.OnEdited();

            IReadOnlyList<int>? mentions = null;
            if (request.MentionUserIds is not null)
            {
                mentions = await TableDiscussionAccess
                    .FilterMentionsAsync(access, documentId, tableCode, message.PeriodKey, request.MentionUserIds, chat.UserId, innerCt)
                    .ConfigureAwait(false);
                await store.ReplaceMentionsAsync(message, mentions, innerCt).ConfigureAwait(false);
            }

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now, EditedEvent, null, null,
                    JsonSerializer.Serialize(new { documentId, tableCode, messageId, oldBody, newBody = body, mentions }),
                    chat.UserId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            edited = message;
        }, ct).ConfigureAwait(false);

        return await MapOneAsync(edited!, chat, ct).ConfigureAwait(false);
    }

    /// <summary>М'яке видалення (автор або модератор); повторне — без змін.</summary>
    public async Task DeleteAsync(long documentId, string tableCode, long messageId, CancellationToken ct)
    {
        var chat = await TableDiscussionAccess
            .ResolveAsync(access, currentUser, store, documentId, tableCode, ct).ConfigureAwait(false);

        var moderator = await TableDiscussionAccess.CanModerateAsync(access, chat.Profile, documentId, ct).ConfigureAwait(false);
        var author = await TableDiscussionAccess.CanDiscussAsync(access, chat.Profile, documentId, ct).ConfigureAwait(false);

        var now = clock.UtcNow;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var message = await store.FindMessageForUpdateAsync(documentId, tableCode, messageId, innerCt).ConfigureAwait(false);
            if (message is null || !chat.CanSeePeriod(message.PeriodKey))
            {
                throw TableDiscussionAccess.MessageNotFound(documentId);
            }

            if (message.IsDeleted)
            {
                return;
            }

            var own = message.AuthorUserId == chat.UserId && author;
            if (!own && !moderator)
            {
                throw TableDiscussionAccess.Forbidden(
                    "err.ECR-ACCS-0403.discussionDeleteForbidden", "Видалити може автор або модератор.");
            }

            var state = await store.LockStateAsync(documentId, tableCode, innerCt).ConfigureAwait(false);
            message.SoftDelete(chat.UserId, now);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await store.RemoveMentionsAsync(messageId, innerCt).ConfigureAwait(false);
            state.OnDeleted(await store.LastLiveAtAsync(documentId, tableCode, innerCt).ConfigureAwait(false));

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now, DeletedEvent, message.AuthorUserId, null,
                    JsonSerializer.Serialize(new
                    {
                        documentId, tableCode, messageId, periodKey = message.PeriodKey, body = message.Body,
                        byModerator = !own,
                    }),
                    chat.UserId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Позначка «прочитано до» (монотонна; понад найбільший id чату зрізається).</summary>
    public async Task MarkReadAsync(
        long documentId, string tableCode, MarkTableDiscussionReadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var chat = await TableDiscussionAccess
            .ResolveAsync(access, currentUser, store, documentId, tableCode, ct).ConfigureAwait(false);

        if (request.LastReadMessageId < 0)
        {
            throw TableDiscussionAccess.BodyInvalid("Недійсна позначка «прочитано».");
        }

        var state = await store.GetStateAsync(documentId, tableCode, ct).ConfigureAwait(false);
        var upTo = Math.Min(request.LastReadMessageId, state.LastMessageId);
        if (upTo == 0)
        {
            return;
        }

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await store.MarkReadAsync(chat.UserId, documentId, tableCode, upTo, innerCt).ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<TableDiscussionMessageDto> MapOneAsync(
        TableDiscussionMessage message, TableChatAccess chat, CancellationToken ct)
    {
        var canModerate = await TableDiscussionAccess.CanModerateAsync(access, chat.Profile, message.DocumentId, ct).ConfigureAwait(false);
        var names = await store.UserNamesAsync([message.AuthorUserId], ct).ConfigureAwait(false);
        var row = new TableDiscussionMessageRow(
            message.Id, message.TableDefCode, message.PeriodKey, message.AuthorUserId,
            names.GetValueOrDefault(message.AuthorUserId, string.Empty), message.Body,
            message.CreatedAt, message.EditedAt, message.DeletedAt);

        return (await queries.MapAsync([row], chat.UserId, canModerate, ct).ConfigureAwait(false))[0];
    }

    private static Errors.BusinessRuleException Rule(
        string messageKey, string message, IReadOnlyDictionary<string, object?>? extra = null)
        => EditAssignments.EditAssignmentAccess.Invalid(messageKey, message, extra);
}
