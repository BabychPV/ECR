// src/Ecr.Application/Documents/Discussions/TableDiscussionCommands.cs
namespace Ecr.Application.Documents.Discussions;

/// <summary>Запис у чат таблиці: нове повідомлення, правка, м'яке видалення, «прочитано».</summary>
/// <remarks>
/// CONTRACT-ONLY (перший коміт лінії lane/integ-tablechat-server): див. <see cref="TableDiscussionQueries"/>.
/// </remarks>
public sealed class TableDiscussionCommands
{
    /// <summary>Пише повідомлення.</summary>
    public Task<TableDiscussionMessageDto> PostAsync(
        long documentId, string tableCode, PostTableDiscussionMessageRequest request, CancellationToken ct)
        => throw new NotImplementedException("table chat: contract commit");

    /// <summary>Правка свого повідомлення в межах вікна.</summary>
    public Task<TableDiscussionMessageDto> EditAsync(
        long documentId, string tableCode, long messageId, EditTableDiscussionMessageRequest request, CancellationToken ct)
        => throw new NotImplementedException("table chat: contract commit");

    /// <summary>М'яке видалення (автор або модератор).</summary>
    public Task DeleteAsync(long documentId, string tableCode, long messageId, CancellationToken ct)
        => throw new NotImplementedException("table chat: contract commit");

    /// <summary>Позначка «прочитано до».</summary>
    public Task MarkReadAsync(
        long documentId, string tableCode, MarkTableDiscussionReadRequest request, CancellationToken ct)
        => throw new NotImplementedException("table chat: contract commit");
}
